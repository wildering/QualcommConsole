// ============================================================================
// QualcommService - Diag诊断/Loader检测/Motorola支持 (partial class)
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WackeEdl.Common;
using WackeEdl.Qualcomm.Common;
using WackeEdl.Qualcomm.Database;
using WackeEdl.Qualcomm.Models;
using WackeEdl.Qualcomm.Protocol;
using WackeEdl.Qualcomm.Authentication;

namespace WackeEdl.Qualcomm.Services
{
    public partial class QualcommService
    {
        private static readonly TimeSpan DiagQcnReadTimeout = TimeSpan.FromMinutes(8.0);

        private static readonly TimeSpan DiagQcnWriteTimeout = TimeSpan.FromMinutes(8.0);

        #region Diag 诊断功能
        
        /// <summary>
        /// 连接到 Diag 诊断端口
        /// </summary>
        public async Task<bool> ConnectDiagAsync(string portName, int baudRate = 115200)
        {
            try
            {
                if (_diagClient == null || _diagClient is not QmslDiagClient)
                {
                    _diagClient?.Dispose();
                    _diagClient = new QmslDiagClient(_logDetail);
                }
                
                _log($"[高通] 正在连接诊断端口 {portName}...");
                _logDetail(string.Format("[QCN][Service][DiagConnect] start port={0}, baud={1}", portName, baudRate));
                var result = await _diagClient.ConnectAsync(portName, baudRate);
                
                if (result)
                {
                    _log("[高通] 诊断端口连接成功");
                    _logDetail("[QCN][Service][DiagConnect] connected");
                }
                else
                {
                    _log("[高通] 诊断端口连接失败");
                    _logDetail("[QCN][Service][DiagConnect] failed");
                }
                
                return result;
            }
            catch (Exception ex)
            {
                _log($"[高通] 诊断端口连接异常: {ex.Message}");
                _logDetail(string.Format("[QCN][Service][DiagConnect] exception: {0}: {1}", ex.GetType().Name, ex.Message));
                return false;
            }
        }
        
        /// <summary>
        /// 断开 Diag 诊断连接
        /// </summary>
        public void DisconnectDiag()
        {
            _diagClient?.Disconnect();
            _diagClient?.Dispose();
            _diagClient = null;
        }
        
        /// <summary>
        /// 发送 SPC 解锁
        /// </summary>
        public async Task<bool> SendSpcAsync(string spc = "000000")
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return false;
            }
            
            _log("[高通] 正在发送 SPC 解锁...");
            var result = await _diagClient.SendSpcAsync(spc);
            _log(result ? "[高通] SPC 解锁成功" : "[高通] SPC 解锁失败");
            return result;
        }
        
        /// <summary>
        /// 读取 IMEI
        /// </summary>
        public async Task<string> ReadDiagImeiAsync(int slot = 1)
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return null;
            }
            
            _log($"[高通] 正在读取 IMEI (Slot {slot})...");
            _logDetail(string.Format("[QCN][Service][IMEI][Read] slot={0} progress=0%", slot));
            var imei = await _diagClient.ReadImeiAsync(slot);
            
            if (!string.IsNullOrEmpty(imei))
            {
                _log($"[高通] IMEI{slot}: {SensitiveDataPolicy.DisplayImei(imei)}");
                _logDetail(string.Format("[QCN][Service][IMEI][Read] slot={0} progress=100% result=success", slot));
            }
            else
            {
                _log($"[高通] 读取 IMEI{slot} 失败");
                _logDetail(string.Format("[QCN][Service][IMEI][Read] slot={0} progress=100% result=failed", slot));
            }
            
            return imei;
        }
        
        /// <summary>
        /// 写入 IMEI
        /// </summary>
        public async Task<bool> WriteDiagImeiAsync(string imei, int slot = 1)
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return false;
            }
            
            if (string.IsNullOrEmpty(imei) || imei.Length != 15)
            {
                _log("[高通] IMEI 格式错误，必须为 15 位数字");
                return false;
            }
            
            _log($"[高通] 正在写入 IMEI (Slot {slot}): {SensitiveDataPolicy.DisplayImei(imei)}...");
            _logDetail(string.Format("[QCN][Service][IMEI][Write] slot={0} progress=0%", slot));
            var result = await _diagClient.WriteImeiAsync(imei, slot);
            _log(result ? "[高通] IMEI 写入成功" : "[高通] IMEI 写入失败");
            _logDetail(string.Format("[QCN][Service][IMEI][Write] slot={0} progress=100% result={1}",
                slot, result ? "success" : "failed"));
            return result;
        }
        
        /// <summary>
        /// 读取所有 IMEI
        /// </summary>
        public async Task<ImeiInfo> ReadAllDiagImeiAsync()
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return null;
            }
            
            _log("[高通] 正在读取所有 IMEI...");
            var info = await _diagClient.ReadAllImeiAsync();
            
            if (!string.IsNullOrEmpty(info?.Imei1))
                _log($"[高通] IMEI1: {SensitiveDataPolicy.DisplayImei(info.Imei1)}");
            if (!string.IsNullOrEmpty(info?.Imei2))
                _log($"[高通] IMEI2: {SensitiveDataPolicy.DisplayImei(info.Imei2)}");
            if (!string.IsNullOrEmpty(info?.Imei3))
                _log($"[高通] IMEI3: {SensitiveDataPolicy.DisplayImei(info.Imei3)}");
            if (!string.IsNullOrEmpty(info?.Imei4))
                _log($"[高通] IMEI4: {SensitiveDataPolicy.DisplayImei(info.Imei4)}");
            
            return info;
        }
        
        /// <summary>
        /// 读取 MEID
        /// </summary>
        public async Task<string> ReadDiagMeidAsync()
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return null;
            }
            
            _log("[高通] 正在读取 MEID...");
            var meid = await _diagClient.ReadMeidAsync();
            
            if (!string.IsNullOrEmpty(meid))
                _log($"[高通] MEID: {meid}");
            else
                _log("[高通] 读取 MEID 失败");
            
            return meid;
        }
        
        /// <summary>
        /// 读取 QCN 文件
        /// </summary>
        public async Task<bool> ReadQcnAsync(string filePath, IProgress<int> progress = null, CancellationToken cancellationToken = default)
        {
            DateTime startTime = DateTime.UtcNow;
            _logDetail(string.Format("[QCN][Service][Read] start target={0}, timeout={1}s", filePath, DiagQcnReadTimeout.TotalSeconds));
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                _logDetail("[QCN][Service][Read] abort: diag not connected");
                return false;
            }
            
            _log($"[高通] 正在读取 QCN 到 {filePath}...");
            using CancellationTokenSource timeoutCts = new CancellationTokenSource(DiagQcnReadTimeout);
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var result = await _diagClient.ReadQcnAsync(filePath, progress, linkedCts.Token);
            _log(result ? "[高通] QCN 读取成功" : "[高通] QCN 读取失败");
            if (!result)
            {
                if (timeoutCts.IsCancellationRequested)
                {
                    _log("[高通] QCN 读取超时，已中断当前 Diag 会话");
                    _logDetail("[QCN][Service][Read] timeout");
                }
                else if (cancellationToken.IsCancellationRequested)
                {
                    _log("[高通] QCN 读取已取消");
                    _logDetail("[QCN][Service][Read] canceled");
                }
            }
            TimeSpan elapsed = DateTime.UtcNow - startTime;
            if (result && File.Exists(filePath))
            {
                long size = new FileInfo(filePath).Length;
                _logDetail(string.Format("[QCN][Service][Read] done success elapsed={0:F2}s size={1} bytes",
                    elapsed.TotalSeconds, size));
            }
            else
            {
                _logDetail(string.Format("[QCN][Service][Read] done failed elapsed={0:F2}s", elapsed.TotalSeconds));
            }
            return result;
        }
        
        /// <summary>
        /// 写入 QCN 文件
        /// </summary>
        public async Task<bool> WriteQcnAsync(string filePath, IProgress<int> progress = null, CancellationToken cancellationToken = default)
        {
            DateTime startTime = DateTime.UtcNow;
            _logDetail(string.Format("[QCN][Service][Write] start source={0}, timeout={1}s", filePath, DiagQcnWriteTimeout.TotalSeconds));
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                _logDetail("[QCN][Service][Write] abort: diag not connected");
                return false;
            }
            
            if (!File.Exists(filePath))
            {
                _log($"[高通] QCN 文件不存在: {filePath}");
                _logDetail("[QCN][Service][Write] abort: file not found");
                return false;
            }
            long sourceSize = new FileInfo(filePath).Length;
            _logDetail(string.Format("[QCN][Service][Write] source size={0} bytes", sourceSize));
            
            _log($"[高通] 正在写入 QCN: {filePath}...");
            using CancellationTokenSource timeoutCts = new CancellationTokenSource(DiagQcnWriteTimeout);
            using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
            var result = await _diagClient.WriteQcnAsync(filePath, progress, linkedCts.Token);
            _log(result ? "[高通] QCN 写入成功" : "[高通] QCN 写入失败");
            if (!result)
            {
                if (timeoutCts.IsCancellationRequested)
                {
                    _log("[高通] QCN 写入超时，已中断当前 Diag 会话");
                    _logDetail("[QCN][Service][Write] timeout");
                }
                else if (cancellationToken.IsCancellationRequested)
                {
                    _log("[高通] QCN 写入已取消");
                    _logDetail("[QCN][Service][Write] canceled");
                }
            }
            TimeSpan elapsed = DateTime.UtcNow - startTime;
            _logDetail(string.Format("[QCN][Service][Write] done {0} elapsed={1:F2}s",
                result ? "success" : "failed", elapsed.TotalSeconds));
            return result;
        }
        
        /// <summary>
        /// 通过 Diag 切换到下载模式 (EDL)
        /// </summary>
        public async Task<bool> SwitchToEdlModeAsync()
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return false;
            }
            
            _log("[高通] 正在切换到下载模式 (EDL)...");
            var result = await _diagClient.SwitchToDownloadModeAsync();
            _log(result ? "[高通] 切换成功，设备即将进入 EDL" : "[高通] 切换失败");
            return result;
        }
        
        /// <summary>
        /// 通过 Diag 重启设备
        /// </summary>
        public async Task<bool> RebootDeviceAsync()
        {
            if (_diagClient == null || !_diagClient.IsConnected)
            {
                _log("[高通] 诊断端口未连接");
                return false;
            }
            
            _log("[高通] 正在重启设备...");
            var result = await _diagClient.RebootAsync();
            return result;
        }
        
        #endregion

        #region Loader 功能检测
        
        /// <summary>
        /// 获取 Loader 功能特性
        /// </summary>
        public LoaderFeatures LoaderFeatures => _loaderFeatures;
        
        /// <summary>
        /// 检测 Loader 功能
        /// </summary>
        public LoaderFeatures DetectLoaderFeatures(byte[] loaderData)
        {
            if (_loaderDetector == null)
                _loaderDetector = new LoaderFeatureDetector();
            
            _loaderFeatures = _loaderDetector.DetectFeatures(loaderData);
            
            if (_loaderFeatures != null)
            {
                _log("[高通] Loader 功能检测完成:");
                _log($"  芯片: {_loaderFeatures.ChipName ?? "未知"}");
                _log($"  存储: {_loaderFeatures.RecommendedMemoryType}");
                _log($"  受限: {_loaderFeatures.IsRestricted}");
                _log($"  功能: {string.Join(", ", _loaderFeatures.GetSupportedFeatures())}");
                
                if (_loaderFeatures.IsXiaomi)
                {
                    _log($"  [小米] EDL 验证: {_loaderFeatures.XiaomiEdlVerification}");
                    _log($"  [小米] 可利用漏洞: {_loaderFeatures.ExploitPossible}");
                }
            }
            
            return _loaderFeatures;
        }
        
        /// <summary>
        /// 从文件检测 Loader 功能
        /// </summary>
        public LoaderFeatures DetectLoaderFeaturesFromFile(string loaderPath)
        {
            if (!File.Exists(loaderPath))
            {
                _log($"[高通] Loader 文件不存在: {loaderPath}");
                return null;
            }
            
            var loaderData = File.ReadAllBytes(loaderPath);
            return DetectLoaderFeatures(loaderData);
        }
        
        /// <summary>
        /// 验证 Loader 是否有效
        /// </summary>
        public bool IsValidLoader(byte[] loaderData)
        {
            return LoaderFeatureDetector.IsValidLoader(loaderData);
        }
        
        #endregion

        #region Motorola 支持
        
        /// <summary>
        /// 检查是否为 Motorola 固件包
        /// </summary>
        public bool IsMotorolaPackage(string filePath)
        {
            return MotorolaSupport.IsMotorolaPackage(filePath);
        }
        
        /// <summary>
        /// 解析 Motorola 固件包
        /// </summary>
        public async Task<MotorolaPackageInfo> ParseMotorolaPackageAsync(string filePath)
        {
            if (_motorolaSupport == null)
            {
                _motorolaSupport = new MotorolaSupport();
                _motorolaSupport.OnLog += msg => _log($"[Motorola] {msg}");
            }
            
            _log($"[高通] 正在解析 Motorola 固件包: {Path.GetFileName(filePath)}...");
            return await _motorolaSupport.ParsePackageAsync(filePath);
        }
        
        /// <summary>
        /// 提取 Motorola 固件包
        /// </summary>
        public async Task<string> ExtractMotorolaPackageAsync(string filePath, string outputDir = null, IProgress<int> progress = null)
        {
            if (_motorolaSupport == null)
            {
                _motorolaSupport = new MotorolaSupport();
                _motorolaSupport.OnLog += msg => _log($"[Motorola] {msg}");
            }
            
            if (progress != null)
                _motorolaSupport.OnProgress += percent => progress.Report(percent);
            
            _log($"[高通] 正在提取 Motorola 固件包: {Path.GetFileName(filePath)}...");
            var result = await _motorolaSupport.ExtractPackageAsync(filePath, outputDir);
            _log($"[高通] 提取完成: {result}");
            return result;
        }
        
        #endregion
    }
}
