// ============================================================================
// WackeEdl - Qualcomm Service | 高通服务
// ============================================================================
// [ZH] 高通刷写服务 - 整合 Sahara 和 Firehose 协议的高层 API
// [EN] Qualcomm Flash Service - High-level API integrating Sahara and Firehose
// [JA] Qualcommフラッシュサービス - SaharaとFirehoseを統合した高レベルAPI
// [KO] Qualcomm 플래싱 서비스 - Sahara와 Firehose를 통합한 고수준 API
// [RU] Сервис прошивки Qualcomm - Высокоуровневый API для Sahara и Firehose
// [ES] Servicio de flasheo Qualcomm - API de alto nivel para Sahara y Firehose
// ============================================================================
// Features: Device connection, partition R/W, flash workflow management
// Copyright (c) 2025-2026 WackeEdl | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WackeEdl.Common;
using WackeEdl.Qualcomm.Common;
using WackeEdl.Qualcomm.Database;
using WackeEdl.Qualcomm.Models;
using WackeEdl.Qualcomm.Protocol;
using WackeEdl.Qualcomm.Authentication;
// 已合并到 WackeEdl.Qualcomm.Common 和 WackeEdl.Qualcomm.Protocol

namespace WackeEdl.Qualcomm.Services
{
    /// <summary>
    /// 连接状态
    /// </summary>
    public enum QualcommConnectionState
    {
        Disconnected,
        Connecting,
        SaharaMode,
        FirehoseMode,
        Ready,
        Error
    }

    /// <summary>
    /// 高通刷写服务
    /// </summary>
    public partial class QualcommService : IDisposable
    {
        private SerialPortManager _portManager;
        private SaharaClient _sahara;
        private FirehoseClient _firehose;
        private readonly Action<string> _log;
        private readonly Action<string> _logDetail;  // 详细调试日志 (只写入文件)
        private readonly Action<long, long> _progress;
        private readonly OplusSuperFlashManager _oplusSuperManager;
        private readonly DeviceInfoService _deviceInfoService;
        private bool _disposed;
        
        // 操作监视器
        private Watchdog _watchdog;

        // 状态
        public QualcommConnectionState State { get; private set; }
        public QualcommChipInfo ChipInfo { get { return _sahara != null ? _sahara.ChipInfo : null; } }
        public uint SaharaProtocolVersion { get { return _sahara != null ? _sahara.ProtocolVersion : 0; } }
        public bool IsVipDevice { get; set; }
        public string StorageType { get { return _firehose != null ? _firehose.StorageType : "ufs"; } }
        public int SectorSize { get { return _firehose != null ? _firehose.SectorSize : 4096; } }
        public string CurrentSlot { get { return _firehose != null ? _firehose.CurrentSlot : "nonexistent"; } }
        
        // 最后使用的连接参数 (用于状态显示)
        public string LastPortName { get; private set; }
        public string LastStorageType { get; private set; }

        // 分区缓存
        private Dictionary<int, List<PartitionInfo>> _partitionCache;
        
        // 端口管理标志位 (用于操作完成后释放端口)
        private bool _portClosed = false;          // 端口是否已关闭
        private bool _keepPortOpen = false;        // 是否保持端口打开 (用于连续操作)
        private QualcommChipInfo _cachedChipInfo;  // 缓存的芯片信息 (端口关闭后保留)
        
        // 新增: Diag 客户端、Loader 检测器、Motorola 支持
        private IDiagClient _diagClient;
        private LoaderFeatureDetector _loaderDetector;
        private MotorolaSupport _motorolaSupport;
        private LoaderFeatures _loaderFeatures;
        private byte[] _pendingSaharaHelloFromProbe;

        /// <summary>
        /// 状态变化事件
        /// </summary>
        public event EventHandler<QualcommConnectionState> StateChanged;
        
        /// <summary>
        /// 端口断开事件 (设备自己断开时触发)
        /// </summary>
        public event EventHandler PortDisconnected;
        
        /// <summary>
        /// 小米授权令牌事件 (内置签名失败时触发，需要弹窗显示令牌)
        /// Token 格式: VQ 开头的 Base64 字符串
        /// </summary>
        public event Action<string> XiaomiAuthTokenRequired;
        
        /// <summary>
        /// 检查是否真正连接 (会验证端口状态)
        /// </summary>
        public bool IsConnected 
        { 
            get 
            { 
                if (State != QualcommConnectionState.Ready)
                    return false;
                    
                // 验证端口是否真正可用
                if (_portManager == null || !_portManager.ValidateConnection())
                {
                    // 端口已断开，更新状态
                    HandlePortDisconnected();
                    return false;
                }
                return true;
            } 
        }
        
        /// <summary>
        /// 快速检查连接状态 (不验证端口，用于UI高频显示)
        /// </summary>
        public bool IsConnectedFast
        {
            get { return State == QualcommConnectionState.Ready && _portManager != null && _portManager.IsOpen; }
        }

        /// <summary>
        /// Diag 连接状态 (QMSL)
        /// </summary>
        public bool IsDiagConnected
        {
            get { return _diagClient != null && _diagClient.IsConnected; }
        }
        
        /// <summary>
        /// 验证连接是否有效
        /// </summary>
        public bool ValidateConnection()
        {
            if (State != QualcommConnectionState.Ready)
                return false;
                
            if (_portManager == null)
                return false;
                
            // 检查端口是否在系统中
            if (!_portManager.IsPortAvailable())
            {
                _logDetail("[高通] 端口已从系统中移除");
                HandlePortDisconnected();
                return false;
            }
            
            // 验证端口连接
            if (!_portManager.ValidateConnection())
            {
                _logDetail("[高通] 端口连接验证失败");
                HandlePortDisconnected();
                return false;
            }
            
            return true;
        }
        
        /// <summary>
        /// 处理端口断开 (设备自己断开)
        /// </summary>
        private void HandlePortDisconnected()
        {
            if (State == QualcommConnectionState.Disconnected)
                return;
                
            _log("[高通] 检测到设备断开");
            
            // 清理资源 (忽略释放异常，确保完整清理)
            if (_portManager != null)
            {
                try { _portManager.Close(); } 
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[QualcommService] 关闭端口异常: {ex.Message}"); }
                try { _portManager.Dispose(); } 
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[QualcommService] 释放端口异常: {ex.Message}"); }
                _portManager = null;
            }
            
            if (_firehose != null)
            {
                try { _firehose.Dispose(); } 
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[QualcommService] 释放 Firehose 异常: {ex.Message}"); }
                _firehose = null;
            }
            
            // 清空分区缓存 (设备断开后缓存无效)
            _partitionCache.Clear();
            _pendingSaharaHelloFromProbe = null;
            
            SetState(QualcommConnectionState.Disconnected);
            PortDisconnected?.Invoke(this, EventArgs.Empty);
        }

        public QualcommService(Action<string> log = null, Action<long, long> progress = null, Action<string> logDetail = null)
        {
            _log = log ?? delegate { };
            _logDetail = logDetail ?? delegate { };
            _progress = progress;
            _oplusSuperManager = new OplusSuperFlashManager(_log);
            _deviceInfoService = new DeviceInfoService(_log, _logDetail);
            _partitionCache = new Dictionary<int, List<PartitionInfo>>();
            State = QualcommConnectionState.Disconnected;
            
            // 初始化操作监视器
            _watchdog = new Watchdog("Qualcomm", WatchdogManager.DefaultTimeouts.Qualcomm, _logDetail);
            _watchdog.OnTimeout += OnWatchdogTimeout;
        }
        
        /// <summary>
        /// 输出设备连接摘要 (芯片/存储/认证状态)
        /// </summary>
        private void LogDeviceSummary()
        {
            if (ChipInfo != null)
            {
                _log(string.Format("[高通] 芯片: {0} (MSM: 0x{1:X8})", 
                    ChipInfo.ChipName ?? "Unknown", ChipInfo.MsmId));
                if (!string.IsNullOrEmpty(ChipInfo.SerialHex))
                    _log(string.Format("[高通] 序列号: {0}", ChipInfo.SerialHex));
                if (!string.IsNullOrEmpty(ChipInfo.PkHash) && ChipInfo.PkHash.Length >= 16)
                    _log(string.Format("[高通] PK Hash: {0}...", ChipInfo.PkHash.Substring(0, 16)));
            }
            if (_firehose != null)
            {
                _log(string.Format("[高通] 存储: {0}, 扇区: {1}B, Payload: {2}KB",
                    _firehose.StorageType ?? LastStorageType ?? "unknown",
                    _firehose.SectorSize, _firehose.MaxPayloadSize / 1024));
            }
            if (IsVipDevice)
            {
                _log("[高通] VIP 高权限模式已激活");
                if (_firehose != null && _firehose.VipSpoofLabel != null)
                    _log(string.Format("[高通] VIP 欺骗模式: {0}", _firehose.VipSpoofLabel));
            }
        }

        /// <summary>
        /// 操作监视器超时处理
        /// </summary>
        private void OnWatchdogTimeout(object sender, WatchdogTimeoutEventArgs e)
        {
            _log($"[高通] 操作监视器超时: {e.OperationName} (等待 {e.ElapsedTime.TotalSeconds:F1}秒)");
            
            // 超时次数过多时尝试重置
            if (e.TimeoutCount >= 3)
            {
                _log("[高通] 多次超时，尝试重置连接...");
                e.ShouldReset = false; // 停止监视器
                
                // 触发端口断开事件
                HandlePortDisconnected();
            }
        }
        
        /// <summary>
        /// 重置监视器 - 在长时间操作中调用以重置计时器
        /// </summary>
        public void FeedWatchdog()
        {
            _watchdog?.Feed();
        }
        
        /// <summary>
        /// 启动操作监视器
        /// </summary>
        public void StartWatchdog(string operation)
        {
            _watchdog?.Start(operation);
        }
        
        /// <summary>
        /// 停止操作监视器
        /// </summary>
        public void StopWatchdog()
        {
            _watchdog?.Stop();
        }

        /// <summary>
        /// 创建 EDL 串口管理器（对齐 EDLLib 的连接参数）。
        /// </summary>
        private SerialPortManager CreateEdlPortManager()
        {
            return new SerialPortManager(_logDetail)
            {
                BaudRate = 9600,
                ReadTimeout = 200,
                WriteTimeout = 200
            };
        }

        /// <summary>
        /// 探测端口当前模式 (Sahara / Firehose)。
        /// </summary>
        public async Task<EdlDeviceMode> ProbeDeviceModeAsync(string portName, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(portName))
                return EdlDeviceMode.Unknown;

            _logDetail(string.Format("[ProbeMode] start port={0}", portName));

            try
            {
                using var probePort = CreateEdlPortManager();
                bool opened = await probePort.OpenAsync(portName, 3, false, ct);
                if (!opened)
                {
                    _logDetail(string.Format("[ProbeMode] open failed: {0}", portName));
                    return EdlDeviceMode.Unknown;
                }

                await Task.Delay(40, ct);

                if (TryReadModeFromBuffer(probePort, out EdlDeviceMode modeFromBuffer, out string reasonFromBuffer, out byte[] helloFromBuffer))
                {
                    CacheProbeMode(modeFromBuffer, helloFromBuffer);
                    _logDetail(string.Format("[ProbeMode] detected from residual buffer: mode={0}, reason={1}", modeFromBuffer, reasonFromBuffer));
                    return modeFromBuffer;
                }

                // 对齐 EDLLib: 无响应时发送 ResetStateMachine(0x13) 触发设备重新发 Hello。
                byte[] resetStateMachine = new byte[8] { 19, 0, 0, 0, 8, 0, 0, 0 };
                const int maxProbeRetries = 5;

                for (int i = 0; i < maxProbeRetries; i++)
                {
                    probePort.Write(resetStateMachine);
                    _logDetail(string.Format("[ProbeMode] sent reset-state-machine ({0}/{1})", i + 1, maxProbeRetries));
                    await Task.Delay(120, ct);

                    if (TryReadModeFromBuffer(probePort, out EdlDeviceMode detectedMode, out string detectedReason, out byte[] helloAfterProbe))
                    {
                        CacheProbeMode(detectedMode, helloAfterProbe);
                        _logDetail(string.Format("[ProbeMode] detected after reset-state-machine: mode={0}, reason={1}", detectedMode, detectedReason));
                        return detectedMode;
                    }
                }

                _logDetail("[ProbeMode] no definitive response after reset-state-machine probes.");
                return EdlDeviceMode.Unknown;
            }
            catch (OperationCanceledException)
            {
                _logDetail("[ProbeMode] canceled.");
                return EdlDeviceMode.Unknown;
            }
            catch (Exception ex)
            {
                _logDetail(string.Format("[ProbeMode] exception: {0}: {1}", ex.GetType().Name, ex.Message));
                return EdlDeviceMode.Unknown;
            }
        }

        private void CacheProbeMode(EdlDeviceMode mode, byte[] helloPacket)
        {
            if (mode == EdlDeviceMode.Sahara && helloPacket != null && helloPacket.Length >= 8)
            {
                _pendingSaharaHelloFromProbe = helloPacket;
            }
            else if (mode == EdlDeviceMode.Firehose)
            {
                _pendingSaharaHelloFromProbe = null;
            }
        }

        private static bool TryReadModeFromBuffer(SerialPortManager port, out EdlDeviceMode mode, out string reason, out byte[] helloPacket)
        {
            mode = EdlDeviceMode.Unknown;
            reason = "empty";
            helloPacket = null;

            if (port == null || !port.IsOpen)
                return false;

            int bytesToRead = port.BytesToRead;
            if (bytesToRead <= 0)
                return false;

            int count = Math.Min(bytesToRead, 8192);
            byte[] buffer = new byte[count];
            int read = port.Read(buffer, 0, count);
            if (read <= 0)
                return false;

            string text;
            try
            {
                text = Encoding.UTF8.GetString(buffer, 0, read);
            }
            catch
            {
                text = string.Empty;
            }

            if (IsFirehoseXmlPayload(text))
            {
                mode = EdlDeviceMode.Firehose;
                reason = string.Format("xml({0}B)", read);
                return true;
            }

            if (IsSaharaHelloPacket(buffer, read))
            {
                mode = EdlDeviceMode.Sahara;
                reason = string.Format("hello({0}B)", read);
                helloPacket = new byte[read];
                Buffer.BlockCopy(buffer, 0, helloPacket, 0, read);
                return true;
            }

            reason = "head=" + GetHexPreview(buffer, read, 16);
            return false;
        }

        private static bool IsFirehoseXmlPayload(string payload)
        {
            if (string.IsNullOrEmpty(payload))
                return false;

            return payload.Contains("<?xml", StringComparison.OrdinalIgnoreCase)
                || payload.Contains("<data", StringComparison.OrdinalIgnoreCase)
                || payload.Contains("<response", StringComparison.OrdinalIgnoreCase)
                || payload.Contains("<log", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSaharaHelloPacket(byte[] buffer, int length)
        {
            return buffer != null
                && length >= 4
                && buffer[0] == 1
                && buffer[1] == 0
                && buffer[2] == 0
                && buffer[3] == 0;
        }

        private static string GetHexPreview(byte[] buffer, int length, int maxBytes)
        {
            if (buffer == null || length <= 0)
                return "<none>";

            int count = Math.Min(length, Math.Max(1, maxBytes));
            return BitConverter.ToString(buffer, 0, count);
        }

        private void ApplyPendingSaharaHelloIfAny(SaharaClient saharaClient)
        {
            if (saharaClient == null)
                return;

            if (_pendingSaharaHelloFromProbe != null && _pendingSaharaHelloFromProbe.Length >= 8)
            {
                saharaClient.SetPendingHelloData(_pendingSaharaHelloFromProbe);
                _logDetail(string.Format("[ProbeMode] applied cached Sahara hello ({0} bytes).", _pendingSaharaHelloFromProbe.Length));
                _pendingSaharaHelloFromProbe = null;
            }
        }

        /// <summary>
        /// 导出探测阶段缓存的 Sahara Hello 包（用于跨 Service 实例复用）。
        /// </summary>
        public byte[] ExportPendingSaharaHello()
        {
            if (_pendingSaharaHelloFromProbe == null || _pendingSaharaHelloFromProbe.Length < 8)
                return null;

            byte[] copy = new byte[_pendingSaharaHelloFromProbe.Length];
            Buffer.BlockCopy(_pendingSaharaHelloFromProbe, 0, copy, 0, copy.Length);
            return copy;
        }

        /// <summary>
        /// 导入探测阶段缓存的 Sahara Hello 包（用于连接前回灌）。
        /// </summary>
        public void ImportPendingSaharaHello(byte[] helloPacket)
        {
            if (helloPacket == null || helloPacket.Length < 8)
                return;

            _pendingSaharaHelloFromProbe = new byte[helloPacket.Length];
            Buffer.BlockCopy(helloPacket, 0, _pendingSaharaHelloFromProbe, 0, helloPacket.Length);
            _logDetail(string.Format("[ProbeMode] imported cached Sahara hello ({0} bytes).", helloPacket.Length));
        }

        #region 连接管理

        // ==================== 连接公共流程 ====================

        /// <summary>
        /// Sahara 上传 Loader (byte[]) 并切换到 Firehose 模式
        /// </summary>
        private async Task<bool> SaharaUploadAndCreateFirehoseAsync(string portName, byte[] loaderData, string loaderAlias, CancellationToken ct)
        {
            if (loaderData == null || loaderData.Length == 0)
            {
                _log("[高通] Loader 数据为空");
                SetState(QualcommConnectionState.Error);
                return false;
            }

            _portManager = CreateEdlPortManager();
            if (!await _portManager.OpenAsync(portName, 3, false, ct))
            {
                _log("[高通] 无法打开端口");
                SetState(QualcommConnectionState.Error);
                return false;
            }

            SetState(QualcommConnectionState.SaharaMode);
            Action<double> saharaProgress = _progress != null
                ? (Action<double>)(percent => _progress((long)percent, 100))
                : null;
            _sahara = new SaharaClient(_portManager, _log, _logDetail, saharaProgress);
            ApplyPendingSaharaHelloIfAny(_sahara);

            if (!await _sahara.HandshakeAndUploadAsync(loaderData, loaderAlias, ct))
            {
                _log("[高通] Sahara 握手/Loader 上传失败");
                SetState(QualcommConnectionState.Error);
                return false;
            }

            return await ReopenPortAndCreateFirehoseAsync(portName, ct);
        }

        /// <summary>
        /// Sahara 上传 Programmer (文件路径) 并切换到 Firehose 模式
        /// </summary>
        private async Task<bool> SaharaUploadAndCreateFirehoseAsync(string portName, string programmerPath, CancellationToken ct)
        {
            if (!File.Exists(programmerPath))
            {
                _log("[高通] Programmer 文件不存在: " + programmerPath);
                SetState(QualcommConnectionState.Error);
                return false;
            }

            _portManager = CreateEdlPortManager();
            if (!await _portManager.OpenAsync(portName, 3, false, ct))
            {
                _log("[高通] 无法打开端口");
                SetState(QualcommConnectionState.Error);
                return false;
            }

            SetState(QualcommConnectionState.SaharaMode);
            Action<double> saharaProgress = _progress != null
                ? (Action<double>)(percent => _progress((long)percent, 100))
                : null;
            _sahara = new SaharaClient(_portManager, _log, _logDetail, saharaProgress);
            ApplyPendingSaharaHelloIfAny(_sahara);

            if (!await _sahara.HandshakeAndUploadAsync(programmerPath, ct))
            {
                _log("[高通] Sahara 握手失败");
                SetState(QualcommConnectionState.Error);
                return false;
            }

            return await ReopenPortAndCreateFirehoseAsync(portName, ct);
        }

        /// <summary>
        /// 关闭 Sahara 端口，重新打开并创建 FirehoseClient
        /// </summary>
        private async Task<bool> ReopenPortAndCreateFirehoseAsync(string portName, CancellationToken ct)
        {
            _log("正在发送 Firehose 引导文件 : 成功");
            await Task.Delay(1000, ct);

            _portManager.Close();
            await Task.Delay(500, ct);

            if (!await _portManager.OpenAsync(portName, 5, true, ct))
            {
                _log("[高通] 无法重新打开端口");
                SetState(QualcommConnectionState.Error);
                return false;
            }

            SetState(QualcommConnectionState.FirehoseMode);
            _firehose = new FirehoseClient(_portManager, _log, _progress, _logDetail);
            PassChipInfoToFirehose();
            return true;
        }

        /// <summary>
        /// 传递芯片信息到 Firehose 客户端
        /// </summary>
        private void PassChipInfoToFirehose()
        {
            if (ChipInfo != null && _firehose != null)
            {
                _firehose.ChipSerial = ChipInfo.SerialHex;
                _firehose.ChipHwId = ChipInfo.HwIdHex;
                _firehose.ChipPkHash = ChipInfo.PkHash;
            }
        }

        /// <summary>
        /// 配置 Firehose
        /// </summary>
        private async Task<bool> ConfigureFirehoseAsync(string storageType, CancellationToken ct)
        {
            _log("正在配置 Firehose...");
            bool configOk = await _firehose.ConfigureAsync(storageType, 0, ct);
            if (!configOk)
            {
                _log("配置 Firehose : 失败");
                SetState(QualcommConnectionState.Error);
                return false;
            }
            _log("配置 Firehose : 成功");
            return true;
        }

        /// <summary>
        /// 完成连接 (保存参数、注册事件、设为 Ready)
        /// </summary>
        private void FinalizeConnection(string portName, string storageType, string successMsg)
        {
            LastPortName = portName;
            LastStorageType = storageType;
            if (_portManager != null)
                _portManager.PortDisconnected += (s, e) => HandlePortDisconnected();
            SetState(QualcommConnectionState.Ready);
            _log(successMsg);
            LogDeviceSummary();
        }

        // ==================== 连接方法 ====================

        /// <summary>
        /// 连接设备 (通用入口，文件路径 Loader，支持多种认证模式)
        /// </summary>
        public async Task<bool> ConnectAsync(string portName, string programmerPath, string storageType = "ufs", 
            string authMode = "none", string digestPath = "", string signaturePath = "",
            CancellationToken ct = default(CancellationToken))
        {
            bool connectSucceeded = false;
            try
            {
                SetState(QualcommConnectionState.Connecting);
                _log("等待高通 EDL USB 设备 : 成功");
                _log(string.Format("USB 端口 : {0}", portName));
                _log("正在连接设备 : 成功");

                if (!await SaharaUploadAndCreateFirehoseAsync(portName, programmerPath, ct))
                    return false;

                string authModeLower = authMode.ToLowerInvariant();
                IsVipDevice = (authModeLower == "vip" || authModeLower == "oplus");

                // 配置前认证 (VIP/Oplus/Xiaomi)
                bool preConfigAuth = (authModeLower == "vip" || authModeLower == "oplus" || authModeLower == "xiaomi");
                if (preConfigAuth)
                {
                    _log(string.Format("[高通] 执行 {0} 认证 (配置前)...", authMode.ToUpper()));
                    bool authOk = false;
                    
                    if (authModeLower == "vip" || authModeLower == "oplus")
                    {
                        if (!string.IsNullOrEmpty(digestPath) && !string.IsNullOrEmpty(signaturePath))
                            authOk = await PerformVipAuthManualAsync(digestPath, signaturePath, ct);
                        else
                        {
                            _log("[高通] O+认证需要 Digest 和 Signature 文件，将回退到普通模式");
                            IsVipDevice = false;
                        }
                    }
                    else if (authModeLower == "xiaomi")
                    {
                        var xiaomi = new XiaomiAuthStrategy(_log);
                        xiaomi.OnAuthTokenRequired += token => XiaomiAuthTokenRequired?.Invoke(token);
                        authOk = await xiaomi.AuthenticateAsync(_firehose, programmerPath, ct);
                    }
                    
                    if (authOk)
                        _log(string.Format("[高通] {0} 认证成功", authMode.ToUpper()));
                    else
                    {
                        _log(string.Format("[高通] {0} 认证失败，连接中止", authMode.ToUpper()));
                        SetState(QualcommConnectionState.Error);
                        return false;
                    }
                }
                else if (authModeLower == "none")
                {
                    _log("[高通] 已选择无认证模式，跳过自动认证");
                }

                if (!await ConfigureFirehoseAsync(storageType, ct))
                    return false;

                // 配置后认证 (OnePlus)
                if (!preConfigAuth && authModeLower != "none")
                {
                    _log(string.Format("[高通] 执行 {0} 认证 (配置后)...", authMode.ToUpper()));
                    bool authOk = false;
                    if (authModeLower == "oneplus")
                    {
                        var oneplus = new OnePlusAuthStrategy(_log);
                        authOk = await oneplus.AuthenticateAsync(_firehose, programmerPath, ct);
                    }
                    _log(authOk
                        ? string.Format("[高通] {0} 认证成功", authMode.ToUpper())
                        : string.Format("[高通] {0} 认证失败", authMode.ToUpper()));
                }

                FinalizeConnection(portName, storageType, "[高通] 连接成功");
                connectSucceeded = true;
                return true;
            }
            catch (OperationCanceledException)
            {
                _log("[高通] 连接已取消");
                SetState(QualcommConnectionState.Disconnected);
                return false;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] 连接错误 - {0}", ex.Message));
                SetState(QualcommConnectionState.Error);
                return false;
            }
            finally
            {
                if (!connectSucceeded)
                    CleanupAfterConnectFailure("ConnectAsync");
            }
        }

        /// <summary>
        /// 使用 Loader 二进制数据连接设备 (无认证)
        /// </summary>
        public async Task<bool> ConnectWithLoaderDataAsync(string portName, byte[] loaderData, string storageType = "ufs", CancellationToken ct = default(CancellationToken))
        {
            bool connectSucceeded = false;
            try
            {
                SetState(QualcommConnectionState.Connecting);
                _log("[高通] 使用 Loader 数据连接...");
                _log(string.Format("USB 端口 : {0}", portName));

                if (!await SaharaUploadAndCreateFirehoseAsync(portName, loaderData, "Loader", ct))
                    return false;

                if (!await ConfigureFirehoseAsync(storageType, ct))
                    return false;

                FinalizeConnection(portName, storageType, "[高通] Loader 连接成功");
                connectSucceeded = true;
                return true;
            }
            catch (OperationCanceledException)
            {
                _log("[高通] 连接已取消");
                SetState(QualcommConnectionState.Disconnected);
                return false;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] 连接错误 - {0}", ex.Message));
                SetState(QualcommConnectionState.Error);
                return false;
            }
            finally
            {
                if (!connectSucceeded)
                    CleanupAfterConnectFailure("ConnectWithLoaderDataAsync");
            }
        }

        /// <summary>
        /// OnePlus 旧签名认证连接 (Demacia/SetProjModel)
        /// 流程: Sahara -> Firehose -> OnePlusAuth -> Configure
        /// </summary>
        public async Task<bool> ConnectWithOldOplusAuthAsync(string portName, byte[] loaderData, string storageType = "ufs", CancellationToken ct = default(CancellationToken))
        {
            bool connectSucceeded = false;
            try
            {
                SetState(QualcommConnectionState.Connecting);
                _log("[OnePlus] 使用 Loader 数据连接...");
                _log(string.Format("USB 端口 : {0}", portName));

                if (!await SaharaUploadAndCreateFirehoseAsync(portName, loaderData, "OnePlus_Loader", ct))
                    return false;

                // OnePlus 认证 (Configure 之前)
                _log("[OnePlus] 执行 Demacia/SetProjModel 认证...");
                var oneplusAuth = new Authentication.OnePlusAuthStrategy(_log);
                bool authOk = await oneplusAuth.AuthenticateAsync(_firehose, null, ct);
                _log(authOk ? "[OnePlus] 认证成功" : "[OnePlus] 认证失败");

                if (!await ConfigureFirehoseAsync(storageType, ct))
                    return false;

                FinalizeConnection(portName, storageType, "[OnePlus] 连接成功");
                connectSucceeded = true;
                return true;
            }
            catch (OperationCanceledException)
            {
                _log("[OnePlus] 连接已取消");
                SetState(QualcommConnectionState.Disconnected);
                return false;
            }
            catch (Exception ex)
            {
                _log(string.Format("[OnePlus] 连接错误 - {0}", ex.Message));
                SetState(QualcommConnectionState.Error);
                return false;
            }
            finally
            {
                if (!connectSucceeded)
                    CleanupAfterConnectFailure("ConnectWithOldOplusAuthAsync");
            }
        }

        /// <summary>
        /// 小米内置签名绕过连接 (MiBypass - 直接写入预置 sig，不读取 blob)
        /// 流程: Sahara -> Firehose -> MiBypassAuth -> Configure
        /// </summary>
        public async Task<bool> ConnectWithMiBypassAuthAsync(string portName, byte[] loaderData, string storageType = "ufs", CancellationToken ct = default(CancellationToken))
        {
            bool connectSucceeded = false;
            try
            {
                SetState(QualcommConnectionState.Connecting);
                _log("[MiBypass] 使用 Loader 数据连接...");
                _log(string.Format("USB 端口 : {0}", portName));

                if (!await SaharaUploadAndCreateFirehoseAsync(portName, loaderData, "Mi_Loader", ct))
                    return false;

                // MiBypass 认证 (Configure 之前)
                _log("[MiBypass] 尝试内置签名绕过...");
                var bypass = new Authentication.MiBypassAuthStrategy(_log);
                bool authOk = await bypass.AuthenticateAsync(_firehose, null, ct);
                _log(authOk ? "[MiBypass] 绕过成功" : "[MiBypass] 绕过失败");

                if (!await ConfigureFirehoseAsync(storageType, ct))
                    return false;

                FinalizeConnection(portName, storageType, "[MiBypass] 连接成功");
                connectSucceeded = true;
                return true;
            }
            catch (OperationCanceledException)
            {
                _log("[MiBypass] 连接已取消");
                SetState(QualcommConnectionState.Disconnected);
                return false;
            }
            catch (Exception ex)
            {
                _log(string.Format("[MiBypass] 连接错误 - {0}", ex.Message));
                SetState(QualcommConnectionState.Error);
                return false;
            }
            finally
            {
                if (!connectSucceeded)
                    CleanupAfterConnectFailure("ConnectWithMiBypassAuthAsync");
            }
        }

        /// <summary>
        /// 小米完整认证连接 (MiAuth - 读取 blob 获取令牌，等待用户签名后写入 sig)
        /// 流程: Sahara -> Firehose -> 读取blob -> Configure -> 等待用户提供sig
        /// </summary>
        public async Task<bool> ConnectWithMiAuthAsync(string portName, byte[] loaderData, string storageType = "ufs", CancellationToken ct = default(CancellationToken))
        {
            bool connectSucceeded = false;
            try
            {
                SetState(QualcommConnectionState.Connecting);
                _log("[MiAuth] 使用 Loader 数据连接...");
                _log(string.Format("USB 端口 : {0}", portName));

                if (!await SaharaUploadAndCreateFirehoseAsync(portName, loaderData, "Mi_Loader", ct))
                    return false;

                // 读取 blob 获取令牌 (Configure 之前)
                _log("[MiAuth] 正在读取 blob...");
                var miAuth = new Authentication.MiAuthStrategy(_log);
                miAuth.OnAuthTokenRequired += token => XiaomiAuthTokenRequired?.Invoke(token);
                await miAuth.AuthenticateAsync(_firehose, null, ct);
                // AuthenticateAsync 返回 false 是正常的 (等待用户提供 sig)

                if (!await ConfigureFirehoseAsync(storageType, ct))
                    return false;

                FinalizeConnection(portName, storageType, "[MiAuth] 连接成功 (等待用户提供签名)");
                connectSucceeded = true;
                return true;
            }
            catch (OperationCanceledException)
            {
                _log("[MiAuth] 连接已取消");
                SetState(QualcommConnectionState.Disconnected);
                return false;
            }
            catch (Exception ex)
            {
                _log(string.Format("[MiAuth] 连接错误 - {0}", ex.Message));
                SetState(QualcommConnectionState.Error);
                return false;
            }
            finally
            {
                if (!connectSucceeded)
                    CleanupAfterConnectFailure("ConnectWithMiAuthAsync");
            }
        }

        /// <summary>
        /// Oplus 新签名 VIP 认证连接 (Digest+Verify+Sig+SHA256Init)
        /// 流程: Sahara -> Firehose -> NewOplusAuth -> Configure -> VIP 欺骗探测
        /// </summary>
        public async Task<bool> ConnectWithNewOplusAuthAsync(string portName, byte[] loaderData, string digestPath, string signaturePath, string storageType = "ufs", CancellationToken ct = default(CancellationToken))
        {
            bool connectSucceeded = false;
            try
            {
                SetState(QualcommConnectionState.Connecting);
                _log("[Oplus] 使用新签名方法连接...");
                _log(string.Format("USB 端口 : {0}", portName));

                if (!await SaharaUploadAndCreateFirehoseAsync(portName, loaderData, "VIP_Loader", ct))
                    return false;

                // NewOplus VIP 认证 (Configure 之前)
                if (string.IsNullOrEmpty(digestPath) || string.IsNullOrEmpty(signaturePath) ||
                    !System.IO.File.Exists(digestPath) || !System.IO.File.Exists(signaturePath))
                {
                    _log("[Oplus] 缺少 Digest/Signature 文件");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                _log(string.Format("[Oplus] Digest: {0}", System.IO.Path.GetFileName(digestPath)));
                _log(string.Format("[Oplus] Signature: {0}", System.IO.Path.GetFileName(signaturePath)));

                var strategy = new NewOplusAuthStrategy(digestPath, signaturePath, _log);
                bool vipAuthOk = await strategy.AuthenticateAsync(_firehose, null, ct);
                if (vipAuthOk)
                {
                    _log("[Oplus] VIP 认证成功");
                    IsVipDevice = true;
                }
                else
                {
                    _log("[Oplus] VIP 认证失败，连接中止");
                    IsVipDevice = false;
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                if (!await ConfigureFirehoseAsync(storageType, ct))
                    return false;

                // VIP 欺骗模式探测
                if (IsVipDevice)
                {
                    try { await _firehose.TestVipRwModeAsync(ct); }
                    catch (Exception ex) { _logDetail(string.Format("[Oplus] VIP 欺骗模式探测异常: {0}", ex.Message)); }
                }

                FinalizeConnection(portName, storageType, "[Oplus] 新签名连接成功");
                connectSucceeded = true;
                return true;
            }
            catch (OperationCanceledException)
            {
                _log("[Oplus] 连接已取消");
                SetState(QualcommConnectionState.Disconnected);
                return false;
            }
            catch (Exception ex)
            {
                _log(string.Format("[Oplus] 连接错误 - {0}", ex.Message));
                SetState(QualcommConnectionState.Error);
                return false;
            }
            finally
            {
                if (!connectSucceeded)
                    CleanupAfterConnectFailure("ConnectWithNewOplusAuthAsync");
            }
        }

        /// <summary>
        /// 直接连接 Firehose (跳过 Sahara)
        /// </summary>
        public async Task<bool> ConnectFirehoseDirectAsync(
            string portName,
            string storageType = "ufs",
            CancellationToken ct = default(CancellationToken),
            bool probeVipSpoof = true)
        {
            bool connectSucceeded = false;
            try
            {
                SetState(QualcommConnectionState.Connecting);
                _log(string.Format("[高通] 直接连接 Firehose: {0}...", portName));

                _portManager = CreateEdlPortManager();
                bool opened = await _portManager.OpenAsync(portName, 3, true, ct);
                if (!opened)
                {
                    _log("[高通] 无法打开端口");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }

                SetState(QualcommConnectionState.FirehoseMode);
                _firehose = new FirehoseClient(_portManager, _log, _progress, _logDetail);

                _log("正在配置 Firehose...");
                bool configOk = await _firehose.ConfigureAsync(storageType, 0, ct);
                if (!configOk)
                {
                    _log("配置 Firehose : 失败");
                    SetState(QualcommConnectionState.Error);
                    return false;
                }
                _log("配置 Firehose : 成功");

                // 直连模式下按用户选项执行 VIP 欺骗模式探测
                if (probeVipSpoof)
                {
                    try
                    {
                        string vipLabel = await _firehose.TestVipRwModeAsync(ct);
                        if (!string.IsNullOrEmpty(vipLabel))
                        {
                            IsVipDevice = true;
                            _logDetail(string.Format("[高通] 检测到 VIP 设备: {0}", vipLabel));
                        }
                    }
                    catch (Exception ex)
                    {
                        _logDetail(string.Format("[高通] VIP 探测跳过: {0}", ex.Message));
                    }
                }
                else
                {
                    _log("[VIP] 已按用户设置跳过欺骗模式探测");
                }

                // 保存连接参数
                LastPortName = portName;
                LastStorageType = storageType;
                
                // 注册端口断开事件
                if (_portManager != null)
                {
                    _portManager.PortDisconnected += (s, e) => HandlePortDisconnected();
                }
                
                SetState(QualcommConnectionState.Ready);
                _log("[高通] Firehose 直连成功");
                connectSucceeded = true;
                return true;
            }
            catch (OperationCanceledException)
            {
                _log("[高通] 连接已取消");
                SetState(QualcommConnectionState.Disconnected);
                return false;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] 连接错误 - {0}", ex.Message));
                SetState(QualcommConnectionState.Error);
                return false;
            }
            finally
            {
                if (!connectSucceeded)
                {
                    CleanupAfterConnectFailure("ConnectFirehoseDirectAsync");
                }
            }
        }

        /// <summary>
        /// 连接失败后的统一资源回收（端口/协议对象），避免 COM 端口残留占用。
        /// </summary>
        private void CleanupAfterConnectFailure(string scene)
        {
            try
            {
                if (_firehose != null)
                {
                    try { _firehose.Dispose(); }
                    catch (Exception ex) { _logDetail(string.Format("[高通] {0}: 释放 Firehose 异常: {1}", scene, ex.Message)); }
                    _firehose = null;
                }

                if (_sahara != null)
                {
                    try { _sahara.Dispose(); }
                    catch (Exception ex) { _logDetail(string.Format("[高通] {0}: 释放 Sahara 异常: {1}", scene, ex.Message)); }
                    _sahara = null;
                }

                if (_portManager != null)
                {
                    try { _portManager.Close(); }
                    catch (Exception ex) { _logDetail(string.Format("[高通] {0}: 关闭端口异常: {1}", scene, ex.Message)); }
                    try { _portManager.Dispose(); }
                    catch (Exception ex) { _logDetail(string.Format("[高通] {0}: 释放端口异常: {1}", scene, ex.Message)); }
                    _portManager = null;
                }

                _portClosed = false;
                IsVipDevice = false;
                _pendingSaharaHelloFromProbe = null;
                _partitionCache.Clear();

                _logDetail(string.Format("[高通] {0}: 连接失败后已释放端口资源", scene));
            }
            catch
            {
            }
        }

        /// <summary>
        /// 断开连接
        /// </summary>
        public void Disconnect()
        {
            bool hasActiveResources = _portManager != null || _sahara != null || _firehose != null;
            if (!hasActiveResources && State == QualcommConnectionState.Disconnected)
            {
                return;
            }

            _log("[高通] 断开连接");

            if (_portManager != null)
            {
                _portManager.Close();
                _portManager.Dispose();
                _portManager = null;
            }

            if (_sahara != null)
            {
                _sahara.Dispose();
                _sahara = null;
            }

            if (_firehose != null)
            {
                _firehose.Dispose();
                _firehose = null;
            }

            _partitionCache.Clear();
            IsVipDevice = false;
            _portClosed = false;
            _cachedChipInfo = null;
            _pendingSaharaHelloFromProbe = null;

            SetState(QualcommConnectionState.Disconnected);
        }
        
        /// <summary>
        /// 释放端口 (操作完成后调用，保留设备对象和状态信息)
        /// </summary>
        /// <remarks>
        /// 根据EDL工具最佳实践：操作完成后应释放端口，让其他程序可以连接设备。
        /// 调用此方法后：
        /// - 端口关闭，串口资源释放
        /// - 设备对象保留 (ChipInfo, 分区缓存等)
        /// - 下次操作前会自动重新打开端口
        /// </remarks>
        public void ReleasePort()
        {
            // 如果设置了保持端口打开，则跳过释放
            if (_keepPortOpen)
            {
                _logDetail("[高通] 端口保持打开 (连续操作模式)");
                return;
            }
            
            if (_portManager == null || !_portManager.IsOpen)
                return;
                
            try
            {
                // 缓存芯片信息 (端口关闭后仍可访问)
                if (_sahara != null && _sahara.ChipInfo != null)
                {
                    _cachedChipInfo = _sahara.ChipInfo;
                }
                
                // 关闭端口但不销毁设备对象
                _portManager.Close();
                _portClosed = true;
                
                _logDetail("[高通] 端口已释放 (设备信息保留)");
            }
            catch (Exception ex)
            {
                _logDetail("[高通] 释放端口异常: " + ex.Message);
            }
        }
        
        /// <summary>
        /// 确保端口已打开 (操作前调用)
        /// </summary>
        /// <param name="ct">取消令牌</param>
        /// <returns>端口是否可用</returns>
        public async Task<bool> EnsurePortOpenAsync(CancellationToken ct = default(CancellationToken))
        {
            // 如果端口已打开且可用，直接返回
            if (_portManager != null && _portManager.IsOpen && !_portClosed)
                return true;
                
            // 如果没有记录端口名，无法重新打开
            if (string.IsNullOrEmpty(LastPortName))
            {
                _log("[高通] 无法重新打开端口: 未记录端口名");
                return false;
            }
            
            // 检查端口是否在系统中可用（使用 LastPortName 而不是 _portManager.IsPortAvailable()）
            // 因为 ReleasePort 会清除 _portManager._currentPortName
            var availablePorts = System.IO.Ports.SerialPort.GetPortNames();
            bool portExists = Array.Exists(availablePorts, p => 
                p.Equals(LastPortName, StringComparison.OrdinalIgnoreCase));
            
            if (!portExists)
            {
                _log("[高通] 端口已从系统中移除，设备可能已断开");
                HandlePortDisconnected();
                return false;
            }
            
            // 重新打开端口
            try
            {
                _logDetail(string.Format("[高通] 重新打开端口: {0}", LastPortName));
                
                if (_portManager == null)
                {
                    _portManager = CreateEdlPortManager();
                }
                
                bool opened = await _portManager.OpenAsync(LastPortName, 3, true, ct);
                if (!opened)
                {
                    _log("[高通] 无法重新打开端口");
                    return false;
                }
                
                _portClosed = false;
                
                // 注意: Firehose 客户端保留，不需要重新创建
                // 如果 _firehose 为 null，说明连接本身有问题，需要重新完整连接
                if (_firehose == null)
                {
                    _log("[高通] Firehose 客户端丢失，需要重新完整连接");
                    return false;
                }
                
                // VIP 设备重连后需要重新探测欺骗模式
                if (IsVipDevice && string.IsNullOrEmpty(_firehose.VipSpoofLabel))
                {
                    _logDetail("[高通] 重新探测 VIP 欺骗模式...");
                    try
                    {
                        await _firehose.TestVipRwModeAsync(ct);
                    }
                    catch (Exception ex)
                    {
                        _logDetail(string.Format("[高通] VIP 探测异常: {0}", ex.Message));
                    }
                }
                
                _logDetail("[高通] 端口重新打开成功");
                return true;
            }
            catch (Exception ex)
            {
                _log("[高通] 重新打开端口失败: " + ex.Message);
                return false;
            }
        }
        
        /// <summary>
        /// 设置是否保持端口打开 (用于连续操作，如批量刷写)
        /// </summary>
        /// <param name="keepOpen">是否保持打开</param>
        public void SetKeepPortOpen(bool keepOpen)
        {
            _keepPortOpen = keepOpen;
            if (keepOpen)
                _logDetail("[高通] 设置: 保持端口打开");
            else
                _logDetail("[高通] 设置: 允许释放端口");
        }
        
        /// <summary>
        /// 获取芯片信息 (即使端口关闭也可访问缓存)
        /// </summary>
        public QualcommChipInfo GetChipInfo()
        {
            if (_sahara != null && _sahara.ChipInfo != null)
                return _sahara.ChipInfo;
            return _cachedChipInfo;
        }
        
        /// <summary>
        /// 端口是否已释放
        /// </summary>
        public bool IsPortReleased { get { return _portClosed; } }
        
        /// <summary>
        /// 重置卡住的 Sahara 状态
        /// 当设备因为其他软件或引导错误导致卡在 Sahara 模式时使用
        /// </summary>
        /// <param name="portName">端口名</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>是否成功重置</returns>
        public async Task<bool> ResetSaharaAsync(string portName, CancellationToken ct = default(CancellationToken))
        {
            _log("[高通] 尝试重置卡住的 Sahara 状态...");
            
            try
            {
                // 确保之前的连接已关闭
                Disconnect();
                await Task.Delay(200, ct);
                
                // 打开端口
                _portManager = CreateEdlPortManager();
                bool opened = await _portManager.OpenAsync(portName, 3, true, ct);
                if (!opened)
                {
                    _log("[高通] 无法打开端口");
                    return false;
                }
                
                // 创建临时 Sahara 客户端
                _sahara = new SaharaClient(_portManager, _log, _logDetail, null);
                ApplyPendingSaharaHelloIfAny(_sahara);
                
                // 尝试重置
                bool success = await _sahara.TryResetSaharaAsync(ct);
                
                if (success)
                {
                    _log("[高通] Sahara 状态已重置");
                    _log("[高通] 设备已准备好，请点击[连接]按钮重新连接");
                    
                    // 重置成功后断开连接，让用户可以正常重新连接
                    // 保留端口名以便后续连接
                    string savedPortName = portName;
                    
                    // 关闭当前连接（释放端口资源）
                    if (_portManager != null)
                    {
                        _portManager.Close();
                        _portManager.Dispose();
                        _portManager = null;
                    }
                    if (_sahara != null)
                    {
                        _sahara.Dispose();
                        _sahara = null;
                    }
                    
                    // 设置为断开状态，等待用户重新连接
                    SetState(QualcommConnectionState.Disconnected);
                    LastPortName = savedPortName;  // 保留端口名
                }
                else
                {
                    _log("[高通] 无法重置 Sahara，请尝试断电重启设备");
                    // 关闭连接
                    Disconnect();
                }
                
                return success;
            }
            catch (Exception ex)
            {
                _log("[高通] 重置 Sahara 异常: " + ex.Message);
                Disconnect();
                return false;
            }
        }
        
        /// <summary>
        /// 硬重置设备 (完全重启)
        /// </summary>
        /// <param name="portName">端口名</param>
        /// <param name="ct">取消令牌</param>
        public async Task<bool> HardResetDeviceAsync(string portName, CancellationToken ct = default(CancellationToken))
        {
            _log("[高通] 发送硬重置命令...");
            
            try
            {
                // 如果已连接 Firehose，通过 Firehose 重置
                if (_firehose != null && State == QualcommConnectionState.Ready)
                {
                    bool ok = await _firehose.ResetAsync("reset", ct);
                    Disconnect();
                    return ok;
                }
                
                // 否则尝试通过 Sahara 重置
                if (_portManager == null || !_portManager.IsOpen)
                {
                    _portManager = CreateEdlPortManager();
                    await _portManager.OpenAsync(portName, 3, true, ct);
                }
                
                if (_sahara == null)
                {
                    _sahara = new SaharaClient(_portManager, _log, _logDetail, null);
                    ApplyPendingSaharaHelloIfAny(_sahara);
                }
                
                _sahara.SendHardReset();
                _log("[高通] 硬重置命令已发送，设备将重启");
                
                await Task.Delay(500, ct);
                Disconnect();
                return true;
            }
            catch (Exception ex)
            {
                _log("[高通] 硬重置异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 执行认证
        /// </summary>
        public async Task<bool> AuthenticateAsync(string authMode, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接 Firehose，无法执行认证");
                return false;
            }

            try
            {
                switch (authMode.ToLowerInvariant())
                {
                    case "oneplus":
                        _log("[高通] 执行 OnePlus 认证...");
                        var oneplusAuth = new Authentication.OnePlusAuthStrategy();
                        // OnePlus 认证不需要外部文件，使用空字符串
                        return await oneplusAuth.AuthenticateAsync(_firehose, "", ct);

                    case "vip":
                    case "oplus":
                        _log("[高通] 执行 O+/OPPO 认证...");
                        // O+认证通常需要签名文件，这里使用默认路径
                        string vipDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vip");
                        string digestPath = System.IO.Path.Combine(vipDir, "digest.bin");
                        string signaturePath = System.IO.Path.Combine(vipDir, "signature.bin");
                        if (!System.IO.File.Exists(digestPath) || !System.IO.File.Exists(signaturePath))
                        {
                            _log("[高通] O+认证文件不存在，尝试无签名认证...");
                            // 如果没有签名文件，返回 true 继续（某些设备可能不需要认证）
                            return true;
                        }
                        bool ok = await _firehose.PerformVipAuthAsync(digestPath, signaturePath, ct);
                        if (ok) IsVipDevice = true;
                        return ok;

                    case "xiaomi":
                        _log("[高通] 执行小米认证...");
                        var xiaomiAuth = new Authentication.XiaomiAuthStrategy(_log);
                        xiaomiAuth.OnAuthTokenRequired += token => XiaomiAuthTokenRequired?.Invoke(token);
                        return await xiaomiAuth.AuthenticateAsync(_firehose, "", ct);

                    default:
                        _log(string.Format("[高通] 未知认证模式: {0}", authMode));
                        return false;
                }
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] 认证失败: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 执行 OnePlus 认证
        /// </summary>
        public async Task<bool> PerformOnePlusAuthAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接 Firehose，无法执行 OnePlus 认证");
                return false;
            }

            try
            {
                _log("[高通] 执行 OnePlus 认证...");
                var oneplusAuth = new Authentication.OnePlusAuthStrategy(_log);
                bool ok = await oneplusAuth.AuthenticateAsync(_firehose, "", ct);
                if (ok)
                    _log("[高通] OnePlus 认证成功");
                else
                    _log("[高通] OnePlus 认证失败");
                return ok;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] OnePlus 认证异常: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 执行小米认证 (兼容旧接口: 先 Bypass 后 MiAuth)
        /// </summary>
        public async Task<bool> PerformXiaomiAuthAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接 Firehose，无法执行小米认证");
                return false;
            }

            try
            {
                _log("[高通] 执行小米认证...");
                var xiaomiAuth = new Authentication.XiaomiAuthStrategy(_log);
                xiaomiAuth.OnAuthTokenRequired += token => XiaomiAuthTokenRequired?.Invoke(token);
                bool ok = await xiaomiAuth.AuthenticateAsync(_firehose, "", ct);
                if (ok)
                    _log("[高通] 小米认证成功");
                else
                    _log("[高通] 小米认证失败");
                return ok;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] 小米认证异常: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 执行小米内置签名绕过 (MiBypass)
        /// </summary>
        public async Task<bool> PerformMiBypassAuthAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接 Firehose，无法执行 MiBypass");
                return false;
            }

            try
            {
                var bypass = new Authentication.MiBypassAuthStrategy(_log);
                bool ok = await bypass.AuthenticateAsync(_firehose, "", ct);
                _log(ok ? "[高通] MiBypass 成功" : "[高通] MiBypass 失败");
                return ok;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] MiBypass 异常: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 执行小米 MiAuth (读取 blob 获取令牌)
        /// </summary>
        public async Task<bool> PerformMiAuthAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接 Firehose，无法执行 MiAuth");
                return false;
            }

            try
            {
                var miAuth = new Authentication.MiAuthStrategy(_log);
                miAuth.OnAuthTokenRequired += token => XiaomiAuthTokenRequired?.Invoke(token);
                await miAuth.AuthenticateAsync(_firehose, "", ct);
                // 返回 false 是正常的 — blob 已通过事件通知，等待用户签名
                return false;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] MiAuth 异常: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 使用用户提供的签名完成 MiAuth 认证
        /// </summary>
        public async Task<bool> PerformMiAuthWithSignatureAsync(string signatureBase64, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接 Firehose，无法执行 MiAuth 签名");
                return false;
            }

            try
            {
                var miAuth = new Authentication.MiAuthStrategy(_log);
                bool ok = await miAuth.AuthenticateWithSignatureAsync(_firehose, signatureBase64, ct);
                _log(ok ? "[高通] MiAuth 签名认证成功" : "[高通] MiAuth 签名认证失败");
                return ok;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] MiAuth 签名异常: {0}", ex.Message));
                return false;
            }
        }

        private void SetState(QualcommConnectionState newState)
        {
            if (State != newState)
            {
                State = newState;
                if (StateChanged != null)
                    StateChanged(this, newState);
            }
        }

        #endregion

        // 自动认证逻辑  qualcomm_service.auth.cs
        // 分区操作/设备控制/批量刷写  qualcomm_service.partition.cs
        // Diag诊断/Loader检测/Motorola支持  qualcomm_service.diag.cs

        #region IDisposable

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    Disconnect();
                    DisconnectDiag();
                }
                _disposed = true;
            }
        }

        ~QualcommService()
        {
            Dispose(false);
        }

        #endregion
        /// <summary>
        /// 刷写 OPLUS 固件包中的 Super 逻辑分区 (拆解写入)
        /// </summary>
        public async Task<bool> FlashOplusSuperAsync(string firmwareRoot, string nvId = "", IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return false;

            // 1. 查找 super 分区信息
            var superPart = FindPartition("super");
            if (superPart == null)
            {
                _log("[高通] 未在设备上找到 super 分区");
                return false;
            }

            // 2. 准备任务
            _log("[高通] 正在解析 OPLUS 固件 Super 布局...");
            string activeSlot = CurrentSlot;
            if (activeSlot == "nonexistent" || string.IsNullOrEmpty(activeSlot))
                activeSlot = "a";

            // 计算 super 分区总大小 (用于校验)
            long superPartitionSize = superPart.Size;
            _log(string.Format("[高通] Super 分区: 起始扇区={0}, 大小={1} MB", superPart.StartSector, superPartitionSize / 1024 / 1024));

            var tasks = await _oplusSuperManager.PrepareSuperTasksAsync(
                firmwareRoot, superPart.StartSector, (int)superPart.SectorSize, 
                activeSlot, nvId, superPartitionSize);
            
            if (tasks.Count == 0)
            {
                _log("[高通] 未找到可用的 Super 逻辑分区镜像");
                return false;
            }
            
            // 3. 校验任务
            var validation = _oplusSuperManager.ValidateTasks(tasks, superPartitionSize, (int)superPart.SectorSize);
            if (!validation.IsValid)
            {
                foreach (var err in validation.Errors)
                {
                    _log(string.Format("[MetaSuper] 错误: {0}", err));
                }
                _log("[高通] Super 刷写校验失败，已中止");
                return false;
            }
            
            // 显示警告但继续
            foreach (var warn in validation.Warnings)
            {
                _log(string.Format("[MetaSuper] 警告: {0}", warn));
            }

            // 4. 执行任务
            long totalBytes = tasks.Sum(t => t.SizeInBytes);
            long totalWritten = 0;

            _log(string.Format("[高通] 开始拆解写入 {0} 个逻辑镜像 (总计: {1} MB)...", tasks.Count, totalBytes / 1024 / 1024));

            foreach (var task in tasks)
            {
                if (ct.IsCancellationRequested) break;

                _log(string.Format("[高通] 写入 {0} [{1}] 到物理扇区 {2}...", task.PartitionName, Path.GetFileName(task.FilePath), task.PhysicalSector));
                
                // 嵌套进度计算
                var taskProgress = new Progress<double>(p => {
                    if (progress != null)
                    {
                        double currentTaskWeight = (double)task.SizeInBytes / totalBytes;
                        double overallPercent = ((double)totalWritten / totalBytes * 100) + (p * currentTaskWeight);
                        progress.Report(overallPercent);
                    }
                });

                bool success = await _firehose.FlashPartitionFromFileAsync(
                    task.PartitionName, 
                    task.FilePath, 
                    superPart.Lun, 
                    task.PhysicalSector, 
                    taskProgress, 
                    ct, 
                    IsVipDevice);

                if (!success)
                {
                    _log(string.Format("[高通] 写入 {0} 失败，流程中止", task.PartitionName));
                    return false;
                }

                totalWritten += task.SizeInBytes;
            }

            _log("[高通] OPLUS Super 拆解写入完成");
            return true;
        }
    }
}
