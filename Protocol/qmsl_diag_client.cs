using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WackeEdl.Qualcomm.Common;
using WackeEdl.Qualcomm.Common.Runtime;

namespace WackeEdl.Qualcomm.Protocol
{
    /// <summary>
    /// QMSL-based Diag client.
    /// References:
    /// - QCLoader.h
    /// - DiagProtocol.cpp
    /// </summary>
    public sealed class QmslDiagClient : IDiagClient
    {
        private const string QmslDllName = "QMSL_MSVC10R.dll";
        private const ushort NvImeiItemId = 550;
        private const ushort NvMeidItemId = 1943;
        private const int NvBufferLength = 128;

        private const int ModeOfflineDigital = 1;
        private const int ModeReset = 2;

        // Win32 stdout 重定向 (屏蔽 QMSL DLL 的 NVItem 噪声输出)
        private const int STD_OUTPUT_HANDLE = -11;
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);
        private static readonly TimeSpan CancelConvergeGrace = TimeSpan.FromSeconds(15.0);
        private static readonly TimeSpan DisconnectGateWaitTimeout = TimeSpan.FromSeconds(4.0);

        private readonly Action<string> _logDetail;
        private readonly object _syncRoot = new object();
        private readonly SemaphoreSlim _nativeOperationGate = new SemaphoreSlim(1, 1);
        private readonly NvToolCallback _nvCallback;

        private IntPtr _resourceContext = IntPtr.Zero;
        private bool _disposed;
        private string _connectedPort;

        private IProgress<int> _activeProgress;
        private string _activeProgressOp;
        private int _activeQcnWritePhase = -1;
        private int _lastProgress = -1;
        private NativeOperationState _nativeOperationState = NativeOperationState.Idle;
        private string _nativeOperationName = "IDLE";
        private DateTime _nativeOperationStartUtc = DateTime.MinValue;

        private static readonly object NativeLoadLock = new object();
        private static IntPtr s_qmslModule = IntPtr.Zero;
        private static bool s_nvWriteExtChecked;
        private static QlibDiagNvWriteExtDelegate s_nvWriteExt;

        private enum NativeOperationState
        {
            Idle,
            Running,
            CancelRequested,
            Quarantined
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void NvToolCallback(
            IntPtr qmslContext,
            ushort subscriptionId,
            ushort nvId,
            ushort sourceFunc,
            ushort evt,
            ushort progress);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate byte QlibDiagNvWriteExtDelegate(
            IntPtr resourceContext,
            ushort itemId,
            byte[] itemData,
            ushort contextId,
            int length,
            out ushort status);

        [DllImport(QmslDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void QLIB_SetLibraryMode(byte useQpstMode);

        [DllImport(QmslDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr QLIB_ConnectServer(uint comPort);

        [DllImport(QmslDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern byte QLIB_IsPhoneConnected(IntPtr resourceContext);

        [DllImport(QmslDllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern byte QLIB_DIAG_EXT_BUILD_ID_F(
            IntPtr resourceContext,
            out uint msmHwVersion,
            out uint mobModel,
            StringBuilder mobSwRev,
            StringBuilder modelStr);

        [DllImport(QmslDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern byte QLIB_NV_SetTargetSupportMultiSIM(
            IntPtr resourceContext,
            [MarshalAs(UnmanagedType.I1)] bool targetSupportMultiSim);

        [DllImport(QmslDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void QLIB_NV_ConfigureCallBack(
            IntPtr resourceContext,
            NvToolCallback callback);

        [DllImport(QmslDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern byte QLIB_DIAG_NV_READ_EXT_F(
            IntPtr resourceContext,
            ushort itemId,
            byte[] itemData,
            ushort contextId,
            int length,
            out ushort status);

        [DllImport(QmslDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern byte QLIB_DIAG_SPC_F(
            IntPtr resourceContext,
            byte[] spc,
            out int spcResult);

        [DllImport(QmslDllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern byte QLIB_BackupNVFromMobileToQCN(
            IntPtr resourceContext,
            string qcnPath,
            out int resultCode);

        [DllImport(QmslDllName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern byte QLIB_NV_LoadNVsFromQCN(
            IntPtr resourceContext,
            string qcnPath,
            out int numOfNvItemValuesLoaded,
            out int resultCode);

        [DllImport(QmslDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern byte QLIB_NV_WriteNVsToMobile(
            IntPtr resourceContext,
            out int resultCode);

        [DllImport(QmslDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern byte QLIB_DIAG_CONTROL_F(IntPtr resourceContext, int mode);

        [DllImport(QmslDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void QLIB_DisconnectServer(IntPtr resourceContext);

        public QmslDiagClient(Action<string> logDetail = null)
        {
            _logDetail = logDetail ?? delegate { };
            _nvCallback = OnNvProgressCallback;
        }

        public bool IsConnected
        {
            get
            {
                lock (_syncRoot)
                {
                    return _resourceContext != IntPtr.Zero;
                }
            }
        }

        public Task<bool> ConnectAsync(string portName, int baudRate = 115200)
        {
            return RunSerializedNativeAsync(() => ConnectCore(portName, baudRate), "DiagConnect");
        }

        public void Disconnect()
        {
            bool gateEntered = false;
            try
            {
                gateEntered = _nativeOperationGate.Wait(DisconnectGateWaitTimeout);
                if (!gateEntered)
                {
                    Log(string.Format("Disconnect: native gate busy for >{0:F1}s, quarantining context.",
                        DisconnectGateWaitTimeout.TotalSeconds));

                    IntPtr orphanCtx = DetachContextForQuarantine();
                    if (orphanCtx == IntPtr.Zero)
                    {
                        SetNativeState(NativeOperationState.Idle, null);
                        return;
                    }

                    SetNativeState(NativeOperationState.Quarantined, "Disconnect");
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            // Give in-flight native call a grace window before hard disconnect.
                            await Task.Delay(CancelConvergeGrace).ConfigureAwait(false);
                        }
                        catch
                        {
                        }

                        DisconnectContext(orphanCtx, "disconnect-timeout");
                        SetNativeState(NativeOperationState.Idle, null);
                    });
                    return;
                }

                DisconnectCore();
                SetNativeState(NativeOperationState.Idle, null);
            }
            finally
            {
                if (gateEntered)
                    _nativeOperationGate.Release();
            }
        }

        public Task<bool> SendSpcAsync(string spc = "000000")
        {
            return RunSerializedNativeAsync(() => SendSpcCore(spc), "DiagSendSpc");
        }

        public Task<string> ReadImeiAsync(int slot = 1)
        {
            return RunSerializedNativeAsync(() => ReadImeiCore(slot), "DiagReadImei");
        }

        public Task<bool> WriteImeiAsync(string imei, int slot = 1)
        {
            return RunSerializedNativeAsync(() => WriteImeiCore(imei, slot), "DiagWriteImei");
        }

        public async Task<ImeiInfo> ReadAllImeiAsync()
        {
            var info = new ImeiInfo
            {
                Imei1 = await ReadImeiAsync(1).ConfigureAwait(false),
                Imei2 = await ReadImeiAsync(2).ConfigureAwait(false),
                Imei3 = await ReadImeiAsync(3).ConfigureAwait(false),
                Imei4 = await ReadImeiAsync(4).ConfigureAwait(false)
            };
            return info;
        }

        public Task<string> ReadMeidAsync()
        {
            return RunSerializedNativeAsync(ReadMeidCore, "DiagReadMeid");
        }

        public Task<bool> ReadQcnAsync(string filePath, IProgress<int> progress = null, CancellationToken cancellationToken = default)
        {
            return RunSerializedNativeAsync(() => ReadQcnCore(filePath, progress), "DiagReadQcn", cancellationToken, allowCancellation: true);
        }

        public Task<bool> WriteQcnAsync(string filePath, IProgress<int> progress = null, CancellationToken cancellationToken = default)
        {
            return RunSerializedNativeAsync(() => WriteQcnCore(filePath, progress), "DiagWriteQcn", cancellationToken, allowCancellation: true);
        }

        public Task<bool> SwitchToDownloadModeAsync()
        {
            Log("SwitchToDownloadMode: QMSL path does not expose a dedicated EDL mode command.");
            return Task.FromResult(false);
        }

        public Task<bool> RebootAsync()
        {
            return RunSerializedNativeAsync(RebootCore, "DiagReboot");
        }

        private bool ConnectCore(string portName, int baudRate)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(QmslDiagClient));

            if (!OperatingSystem.IsWindows())
            {
                Log("Connect failed: QMSL is Windows-only.");
                return false;
            }

            if (!TryParseComPort(portName, out uint comPort))
            {
                Log(string.Format("Connect failed: invalid COM port '{0}'.", portName ?? "<null>"));
                return false;
            }

            if (!EnsureQmslLoaded())
                return false;

            DisconnectCore();

            try
            {
                Log(string.Format("Connect start: port={0}, com={1}, baud={2}", portName, comPort, baudRate));
                QLIB_SetLibraryMode(0);
                Log("SetLibraryMode(0) ok.");

                IntPtr ctx = QLIB_ConnectServer(comPort);
                if (ctx == IntPtr.Zero)
                {
                    Log("Connect failed: QLIB_ConnectServer returned NULL.");
                    return false;
                }

                byte phoneConnected = QLIB_IsPhoneConnected(ctx);
                if (phoneConnected == 0)
                {
                    Log("Connect failed: QLIB_IsPhoneConnected=0.");
                    QLIB_DisconnectServer(ctx);
                    return false;
                }

                lock (_syncRoot)
                {
                    _resourceContext = ctx;
                    _connectedPort = portName;
                }

                try
                {
                    QLIB_NV_ConfigureCallBack(ctx, _nvCallback);
                    Log("NV progress callback configured.");
                }
                catch (Exception ex)
                {
                    Log(string.Format("NV callback configure warning: {0}: {1}", ex.GetType().Name, ex.Message));
                }

                TryLogBuildInfo(ctx);
                SendSpcCore("000000");
                TrySetMultiSim(true);
                if (TryResolveNvWriteExt(out _))
                    Log("IMEI write support: QLIB_DIAG_NV_WRITE_EXT_F available.");
                else
                    Log("IMEI write support: QLIB_DIAG_NV_WRITE_EXT_F not found.");

                Log("Connect success.");
                return true;
            }
            catch (Exception ex)
            {
                Log(string.Format("Connect exception: {0}: {1}", ex.GetType().Name, ex.Message));
                DisconnectCore();
                return false;
            }
        }

        private void DisconnectCore()
        {
            IntPtr ctx;
            lock (_syncRoot)
            {
                ctx = _resourceContext;
                _resourceContext = IntPtr.Zero;
                _connectedPort = null;
                _activeProgress = null;
                _activeProgressOp = null;
                _activeQcnWritePhase = -1;
                _lastProgress = -1;
            }

            if (ctx == IntPtr.Zero)
                return;

            try
            {
                QLIB_DisconnectServer(ctx);
                Log("Disconnected.");
            }
            catch (Exception ex)
            {
                Log(string.Format("Disconnect warning: {0}: {1}", ex.GetType().Name, ex.Message));
            }
        }

        private bool SendSpcCore(string spc)
        {
            if (!TryGetContext(out IntPtr ctx))
            {
                Log("SendSPC failed: not connected.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(spc) || spc.Length != 6 || !AllDigits(spc))
            {
                Log("SendSPC failed: SPC must be 6 digits.");
                return false;
            }

            byte[] spcBytes = Encoding.ASCII.GetBytes(spc);
            int spcResult = -1;
            byte ok = QLIB_DIAG_SPC_F(ctx, spcBytes, out spcResult);
            Log(string.Format("SendSPC: ok={0}, result={1}", ok, spcResult));
            return ok != 0;
        }

        private string ReadImeiCore(int slot)
        {
            if (!TryGetContext(out IntPtr ctx))
            {
                Log("ReadIMEI failed: not connected.");
                return null;
            }

            int context = NormalizeImeiSlot(slot);
            byte[] nv = new byte[NvBufferLength];
            ushort status;
            byte ok = QLIB_DIAG_NV_READ_EXT_F(ctx, NvImeiItemId, nv, (ushort)context, nv.Length, out status);
            Log(string.Format("ReadIMEI slot={0}: ok={1}, status=0x{2:X4}", context + 1, ok, status));
            if (ok == 0)
                return null;

            string imei = DecodeImeiByQcdReference(nv);
            if (!IsValidImei(imei))
                imei = DecodeImeiByClassicBcd(nv);

            if (!IsValidImei(imei))
            {
                Log(string.Format("ReadIMEI slot={0}: decode failed.", context + 1));
                return null;
            }

            Log(string.Format("ReadIMEI slot={0}: {1}", context + 1, SensitiveDataPolicy.DisplayImei(imei)));
            return imei;
        }

        private bool WriteImeiCore(string imei, int slot)
        {
            if (!TryGetContext(out IntPtr ctx))
            {
                Log("WriteIMEI failed: not connected.");
                return false;
            }

            if (!IsValidImei(imei))
            {
                Log("WriteIMEI failed: IMEI must be 15 digits.");
                return false;
            }

            if (!TryResolveNvWriteExt(out QlibDiagNvWriteExtDelegate writer))
            {
                Log("WriteIMEI failed: QLIB_DIAG_NV_WRITE_EXT_F export not found.");
                return false;
            }

            int context = NormalizeImeiSlot(slot);
            byte[] nvData = EncodeImeiToNv(imei);
            ushort status;
            byte ok = writer(ctx, NvImeiItemId, nvData, (ushort)context, nvData.Length, out status);
            Log(string.Format("WriteIMEI slot={0}: ok={1}, status=0x{2:X4}", context + 1, ok, status));
            return ok != 0 && status == 0;
        }

        private string ReadMeidCore()
        {
            if (!TryGetContext(out IntPtr ctx))
            {
                Log("ReadMEID failed: not connected.");
                return null;
            }

            byte[] nv = new byte[NvBufferLength];
            ushort status;
            byte ok = QLIB_DIAG_NV_READ_EXT_F(ctx, NvMeidItemId, nv, 0, nv.Length, out status);
            Log(string.Format("ReadMEID: ok={0}, status=0x{1:X4}", ok, status));
            if (ok == 0)
                return null;

            int size = Math.Min(7, nv.Length);
            return BitConverter.ToString(nv, 0, size).Replace("-", string.Empty);
        }

        private bool ReadQcnCore(string filePath, IProgress<int> progress)
        {
            if (!TryGetContext(out IntPtr ctx))
            {
                Log("ReadQCN failed: not connected.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(filePath))
            {
                Log("ReadQCN failed: file path is empty.");
                return false;
            }

            string fullPath = Path.GetFullPath(filePath);
            string dir = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            SetActiveProgress(progress, "QCN_READ");
            progress?.Report(0);

            bool success = false;
            try
            {
                Log(string.Format("ReadQCN start: {0}", fullPath));
                int resultCode;
                byte ok = QLIB_BackupNVFromMobileToQCN(ctx, fullPath, out resultCode);
                success = ok != 0 && File.Exists(fullPath) && new FileInfo(fullPath).Length > 0;
                if (success)
                {
                    long size = new FileInfo(fullPath).Length;
                    Log(string.Format("ReadQCN done: ok=1, resultCode={0}, size={1} bytes", resultCode, size));
                }
                else
                {
                    Log(string.Format("ReadQCN failed: ok={0}, resultCode={1}", ok, resultCode));
                }

                return success;
            }
            finally
            {
                FinishActiveProgress(success);
            }
        }

        private bool WriteQcnCore(string filePath, IProgress<int> progress)
        {
            if (!TryGetContext(out IntPtr ctx))
            {
                Log("WriteQCN failed: not connected.");
                return false;
            }

            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                Log(string.Format("WriteQCN failed: file not found '{0}'.", filePath ?? "<null>"));
                return false;
            }

            string fullPath = Path.GetFullPath(filePath);
            SetActiveProgress(progress, "QCN_WRITE");
            progress?.Report(0);

            bool success = false;
            try
            {
                Log(string.Format("WriteQCN load start: {0}", fullPath));
                int loadedCount;
                int loadResultCode;
                SetQcnWritePhase(0);

                // QMSL DLL 在 Load/Write QCN 时会向 stdout 输出 NVItem 噪声
                // 临时将 stdout 重定向到 NUL 以屏蔽
                byte loadOk;
                using (var suppress = new StdoutSuppressor())
                {
                    loadOk = QLIB_NV_LoadNVsFromQCN(ctx, fullPath, out loadedCount, out loadResultCode);
                }
                Log(string.Format("WriteQCN load: ok={0}, loaded={1}, resultCode={2}", loadOk, loadedCount, loadResultCode));
                if (loadOk == 0)
                    return false;

                SetQcnWritePhase(1);
                progress?.Report(45);

                int writeResultCode;
                byte writeOk;
                using (var suppress = new StdoutSuppressor())
                {
                    writeOk = QLIB_NV_WriteNVsToMobile(ctx, out writeResultCode);
                }
                Log(string.Format("WriteQCN write: ok={0}, resultCode={1}", writeOk, writeResultCode));
                success = writeOk != 0;
                return success;
            }
            finally
            {
                FinishActiveProgress(success);
            }
        }

        private bool RebootCore()
        {
            if (!TryGetContext(out IntPtr ctx))
            {
                Log("Reboot failed: not connected.");
                return false;
            }

            byte step1 = QLIB_DIAG_CONTROL_F(ctx, ModeOfflineDigital);
            Thread.Sleep(1200);
            byte step2 = QLIB_DIAG_CONTROL_F(ctx, ModeReset);
            Log(string.Format("Reboot: offline_d={0}, reset={1}", step1, step2));
            return step1 != 0 && step2 != 0;
        }

        private bool TryGetContext(out IntPtr ctx)
        {
            lock (_syncRoot)
            {
                ctx = _resourceContext;
                return ctx != IntPtr.Zero;
            }
        }

        private async Task<T> RunSerializedNativeAsync<T>(
            Func<T> operation,
            string operationName,
            CancellationToken cancellationToken = default,
            bool allowCancellation = false)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(QmslDiagClient));

            if (allowCancellation && cancellationToken.IsCancellationRequested)
            {
                Log(string.Format("{0}: cancellation requested before start.", operationName));
                return default;
            }

            try
            {
                if (allowCancellation && cancellationToken.CanBeCanceled)
                    await _nativeOperationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                else
                    await _nativeOperationGate.WaitAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log(string.Format("{0}: canceled while waiting native operation gate.", operationName));
                return default;
            }

            SetNativeState(NativeOperationState.Running, operationName);
            Task<T> worker = Task.Run(operation);
            try
            {
                if (!allowCancellation || !cancellationToken.CanBeCanceled)
                    return await worker.ConfigureAwait(false);

                Task cancelWait = Task.Delay(Timeout.Infinite, cancellationToken);
                Task completed = await Task.WhenAny(worker, cancelWait).ConfigureAwait(false);
                if (completed == worker)
                    return await worker.ConfigureAwait(false);

                SetNativeState(NativeOperationState.CancelRequested, operationName);
                Log(string.Format("{0}: cancellation requested, waiting worker convergence ({1:F0}s).",
                    operationName, CancelConvergeGrace.TotalSeconds));

                Task converge = await Task.WhenAny(worker, Task.Delay(CancelConvergeGrace)).ConfigureAwait(false);
                if (converge == worker)
                {
                    try
                    {
                        _ = await worker.ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log(string.Format("{0}: worker fault after cancellation: {1}: {2}",
                            operationName, ex.GetType().Name, ex.Message));
                    }

                    DisconnectCore();
                    return default;
                }

                IntPtr orphanCtx = DetachContextForQuarantine();
                SetNativeState(NativeOperationState.Quarantined, operationName);
                Log(string.Format("{0}: worker did not converge, context quarantined.", operationName));

                _ = worker.ContinueWith(
                    t =>
                    {
                        try
                        {
                            if (t.IsFaulted)
                                _ = t.Exception;
                        }
                        catch
                        {
                        }

                        DisconnectContext(orphanCtx, operationName + " converge");
                        SetNativeState(NativeOperationState.Idle, null);
                    },
                    TaskScheduler.Default);

                return default;
            }
            finally
            {
                _nativeOperationGate.Release();
                if (!IsNativeState(NativeOperationState.Quarantined))
                    SetNativeState(NativeOperationState.Idle, null);
            }
        }

        private IntPtr DetachContextForQuarantine()
        {
            lock (_syncRoot)
            {
                IntPtr ctx = _resourceContext;
                _resourceContext = IntPtr.Zero;
                _connectedPort = null;
                _activeProgress = null;
                _activeProgressOp = null;
                _activeQcnWritePhase = -1;
                _lastProgress = -1;
                return ctx;
            }
        }

        private void DisconnectContext(IntPtr ctx, string reason)
        {
            if (ctx == IntPtr.Zero)
                return;

            try
            {
                QLIB_DisconnectServer(ctx);
                Log(string.Format("Disconnected context ({0}).", reason));
            }
            catch (Exception ex)
            {
                Log(string.Format("Disconnect context warning ({0}): {1}: {2}", reason, ex.GetType().Name, ex.Message));
            }
        }

        private bool IsNativeState(NativeOperationState state)
        {
            lock (_syncRoot)
            {
                return _nativeOperationState == state;
            }
        }

        private void SetNativeState(NativeOperationState state, string operationName)
        {
            lock (_syncRoot)
            {
                _nativeOperationState = state;
                _nativeOperationName = operationName ?? "IDLE";
                _nativeOperationStartUtc = state == NativeOperationState.Running ? DateTime.UtcNow : DateTime.MinValue;
            }
            Log(string.Format("NativeState => {0} ({1})", state, operationName ?? "N/A"));
        }

        private void SetActiveProgress(IProgress<int> progress, string operation)
        {
            lock (_syncRoot)
            {
                _activeProgress = progress;
                _activeProgressOp = operation;
                _activeQcnWritePhase = string.Equals(operation, "QCN_WRITE", StringComparison.Ordinal) ? 0 : -1;
                _lastProgress = -1;
            }
        }

        private void SetQcnWritePhase(int phase)
        {
            lock (_syncRoot)
            {
                if (string.Equals(_activeProgressOp, "QCN_WRITE", StringComparison.Ordinal))
                    _activeQcnWritePhase = phase;
            }
        }

        private void FinishActiveProgress(bool success)
        {
            IProgress<int> progress;
            lock (_syncRoot)
            {
                progress = _activeProgress;
                _activeProgress = null;
                _activeProgressOp = null;
                _activeQcnWritePhase = -1;
                _lastProgress = -1;
            }

            if (success)
                progress?.Report(100);
        }

        private void OnNvProgressCallback(
            IntPtr qmslContext,
            ushort subscriptionId,
            ushort nvId,
            ushort sourceFunc,
            ushort evt,
            ushort progress)
        {
            int rawProgress = Clamp(progress, 0, 100);
            IProgress<int> reporter = null;
            string opName = null;
            int qcnWritePhase = -1;
            bool shouldLog = false;
            bool shouldReport = false;
            int mappedProgress = rawProgress;

            lock (_syncRoot)
            {
                reporter = _activeProgress;
                opName = _activeProgressOp;
                qcnWritePhase = _activeQcnWritePhase;

                if (string.Equals(opName, "QCN_WRITE", StringComparison.Ordinal))
                {
                    if (qcnWritePhase <= 0)
                    {
                        mappedProgress = Clamp((int)Math.Round(rawProgress * 0.45, MidpointRounding.AwayFromZero), 0, 45);
                    }
                    else
                    {
                        mappedProgress = Clamp(45 + (int)Math.Round(rawProgress * 0.55, MidpointRounding.AwayFromZero), 45, 100);
                    }
                }

                if (mappedProgress != _lastProgress)
                {
                    _lastProgress = mappedProgress;
                    shouldReport = true;
                    shouldLog = mappedProgress == 0 || mappedProgress == 100 || (mappedProgress % 5 == 0);
                }
            }

            if (shouldReport)
            {
                try
                {
                    reporter?.Report(mappedProgress);
                }
                catch
                {
                }
            }

            if (shouldLog)
            {
                Log(string.Format(
                    "Progress callback: op={0}, progress={1}% (raw={2}%, phase={3}), sid={4}, nv={5}, source={6}, event={7}",
                    opName ?? "N/A",
                    mappedProgress,
                    rawProgress,
                    qcnWritePhase,
                    subscriptionId,
                    nvId,
                    sourceFunc,
                    evt));
            }
        }

        private bool EnsureQmslLoaded()
        {
            lock (NativeLoadLock)
            {
                if (s_qmslModule != IntPtr.Zero)
                    return true;

                if (QmslRuntimeLoader.EnsureLoaded(Log))
                {
                    IntPtr loadedModule = QmslRuntimeLoader.LoadedModule;
                    if (loadedModule != IntPtr.Zero)
                    {
                        s_qmslModule = loadedModule;
                        string loadedPath = QmslRuntimeLoader.LoadedPath;
                        if (!string.IsNullOrWhiteSpace(loadedPath))
                            Log(string.Format("QMSL ready: {0}", loadedPath));
                        else
                            Log("QMSL ready via runtime bootstrap.");
                        return true;
                    }
                }

                foreach (string dir in GetQmslSearchDirectories())
                {
                    if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                        continue;

                    string candidate = Path.Combine(dir, QmslDllName);
                    if (!File.Exists(candidate))
                        continue;

                    try
                    {
                        if (NativeLibrary.TryLoad(candidate, out s_qmslModule) && s_qmslModule != IntPtr.Zero)
                        {
                            Log(string.Format("QMSL loaded: {0}", candidate));
                            break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log(string.Format("QMSL load attempt failed: {0}: {1}", ex.GetType().Name, ex.Message));
                    }
                }

                if (s_qmslModule == IntPtr.Zero)
                {
                    try
                    {
                        if (NativeLibrary.TryLoad(QmslDllName, out s_qmslModule) && s_qmslModule != IntPtr.Zero)
                            Log("QMSL loaded via default DLL search.");
                    }
                    catch (Exception ex)
                    {
                        Log(string.Format("QMSL default load failed: {0}: {1}", ex.GetType().Name, ex.Message));
                    }
                }

                if (s_qmslModule == IntPtr.Zero)
                {
                    Log("QMSL load failed: QMSL_MSVC10R.dll not found.");
                    return false;
                }

                return true;
            }
        }

        private bool TryResolveNvWriteExt(out QlibDiagNvWriteExtDelegate writer)
        {
            lock (NativeLoadLock)
            {
                if (!s_nvWriteExtChecked)
                {
                    s_nvWriteExtChecked = true;
                    if (s_qmslModule != IntPtr.Zero &&
                        NativeLibrary.TryGetExport(s_qmslModule, "QLIB_DIAG_NV_WRITE_EXT_F", out IntPtr pWriteExt) &&
                        pWriteExt != IntPtr.Zero)
                    {
                        s_nvWriteExt = Marshal.GetDelegateForFunctionPointer<QlibDiagNvWriteExtDelegate>(pWriteExt);
                        Log("Resolved export: QLIB_DIAG_NV_WRITE_EXT_F");
                    }
                }

                writer = s_nvWriteExt;
                return writer != null;
            }
        }

        private void TryLogBuildInfo(IntPtr ctx)
        {
            try
            {
                var sw = new StringBuilder(512);
                var model = new StringBuilder(512);
                byte ok = QLIB_DIAG_EXT_BUILD_ID_F(ctx, out uint hw, out uint mobModel, sw, model);
                if (ok != 0)
                {
                    Log(string.Format(
                        "BuildInfo: hw=0x{0:X8}, model={1}, sw='{2}', name='{3}'",
                        hw,
                        mobModel,
                        sw.ToString().Trim(),
                        model.ToString().Trim()));
                }
                else
                {
                    Log("BuildInfo read failed.");
                }
            }
            catch (Exception ex)
            {
                Log(string.Format("BuildInfo exception: {0}: {1}", ex.GetType().Name, ex.Message));
            }
        }

        private void TrySetMultiSim(bool enable)
        {
            try
            {
                if (!TryGetContext(out IntPtr ctx))
                    return;

                byte ok = QLIB_NV_SetTargetSupportMultiSIM(ctx, enable);
                Log(string.Format("SetMultiSim({0}) -> {1}", enable, ok));
            }
            catch (Exception ex)
            {
                Log(string.Format("SetMultiSim exception: {0}: {1}", ex.GetType().Name, ex.Message));
            }
        }

        private static int NormalizeImeiSlot(int slot)
        {
            if (slot <= 1)
                return 0;
            if (slot >= 4)
                return 3;
            return slot - 1;
        }

        private static bool TryParseComPort(string portName, out uint comPort)
        {
            comPort = 0;
            if (string.IsNullOrWhiteSpace(portName))
                return false;

            string text = portName.Trim().ToUpperInvariant();
            if (text.StartsWith("COM", StringComparison.Ordinal))
                text = text.Substring(3);

            if (!uint.TryParse(text, out comPort))
                return false;

            return comPort > 0;
        }

        private static bool AllDigits(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] < '0' || s[i] > '9')
                    return false;
            }
            return true;
        }

        private static bool IsValidImei(string imei)
        {
            return !string.IsNullOrWhiteSpace(imei) && imei.Length == 15 && AllDigits(imei);
        }

        private static string DecodeImeiByQcdReference(byte[] data)
        {
            if (data == null || data.Length < 9)
                return null;

            int[] digits = new int[15];
            int cursor = 0;
            for (int i = 1; i <= 8; i++)
            {
                if (i != 8)
                {
                    digits[cursor] = (data[i] & 0xF0) >> 4;
                    digits[cursor + 1] = data[i + 1] & 0x0F;
                }
                else
                {
                    digits[cursor] = (data[i] & 0xF0) >> 4;
                }
                cursor += 2;
            }

            var sb = new StringBuilder(15);
            for (int i = 0; i < digits.Length; i++)
            {
                if (digits[i] < 0 || digits[i] > 9)
                    return null;
                sb.Append((char)('0' + digits[i]));
            }
            return sb.ToString();
        }

        private static string DecodeImeiByClassicBcd(byte[] data)
        {
            if (data == null || data.Length < 9)
                return null;

            var sb = new StringBuilder(15);
            for (int i = 1; i <= 8 && i < data.Length; i++)
            {
                byte b = data[i];
                int low = b & 0x0F;
                int high = (b >> 4) & 0x0F;
                if (i == 1)
                {
                    if (high < 10)
                        sb.Append((char)('0' + high));
                }
                else
                {
                    if (low < 10)
                        sb.Append((char)('0' + low));
                    if (high < 10)
                        sb.Append((char)('0' + high));
                }
            }

            return sb.Length >= 15 ? sb.ToString(0, 15) : null;
        }

        private static byte[] EncodeImeiToNv(string imei)
        {
            byte[] data = new byte[NvBufferLength];
            data[0] = 0x08;
            data[1] = (byte)(0xA0 | (imei[0] - '0'));

            for (int i = 1; i < 8; i++)
            {
                int idx = i * 2 - 1;
                int low = imei[idx] - '0';
                int high = (idx + 1 < imei.Length) ? (imei[idx + 1] - '0') : 0x0F;
                data[i + 1] = (byte)((high << 4) | low);
            }

            return data;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }

        private IEnumerable<string> GetQmslSearchDirectories()
        {
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddDir(string d)
            {
                if (!string.IsNullOrWhiteSpace(d))
                    dirs.Add(d.Trim());
            }

            string baseDir = AppContext.BaseDirectory;
            AddDir(baseDir);
            AddDir(Path.Combine(baseDir, "lib"));

            return dirs;
        }

        private void Log(string message)
        {
            try
            {
                _logDetail(string.Format("[QMSL] {0}", message));
            }
            catch
            {
            }
        }

        /// <summary>
        /// 临时屏蔽 native stdout (QMSL DLL 在写入 QCN 时会向 stdout 输出大量 NVItem 噪声)。
        /// 通过将 Win32 stdout handle 重定向到 NUL，Dispose 时还原。
        /// </summary>
        private sealed class StdoutSuppressor : IDisposable
        {
            private readonly IntPtr _savedHandle;
            private readonly IntPtr _nulHandle;
            private bool _restored;

            public StdoutSuppressor()
            {
                _savedHandle = GetStdHandle(STD_OUTPUT_HANDLE);
                // GENERIC_WRITE=0x40000000, FILE_SHARE_WRITE=2, OPEN_EXISTING=3
                _nulHandle = CreateFileW("NUL", 0x40000000, 2, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (_nulHandle != IntPtr.Zero && _nulHandle != new IntPtr(-1))
                    SetStdHandle(STD_OUTPUT_HANDLE, _nulHandle);
            }

            public void Dispose()
            {
                if (_restored) return;
                _restored = true;
                if (_savedHandle != IntPtr.Zero)
                    SetStdHandle(STD_OUTPUT_HANDLE, _savedHandle);
                if (_nulHandle != IntPtr.Zero && _nulHandle != new IntPtr(-1))
                    CloseHandle(_nulHandle);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            DisconnectCore();
            _disposed = true;
        }
    }
}
