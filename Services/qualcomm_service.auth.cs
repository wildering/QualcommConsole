// ============================================================================
// QualcommService - 自动认证逻辑 (partial class)
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
        #region 自动认证逻辑

        /// <summary>
        /// 自动认证 - 仅对小米设备自动执行
        /// 其他设备 (OnePlus/OPPO/Realme 等) 由用户手动选择认证方式
        /// </summary>
        private async Task<bool> AutoAuthenticateAsync(string programmerPath, CancellationToken ct)
        {
            if (_firehose == null) return true;

            // 只有小米设备自动认证
            if (IsXiaomiDevice())
            {
                _log("[高通] 检测到小米设备，自动执行 MiAuth 认证...");
                try
                {
                    var xiaomi = new XiaomiAuthStrategy(_log);
                    xiaomi.OnAuthTokenRequired += token => XiaomiAuthTokenRequired?.Invoke(token);
                    bool result = await xiaomi.AuthenticateAsync(_firehose, programmerPath, ct);
                    if (result)
                    {
                        _log("[高通] 小米认证成功");
                    }
                    else
                    {
                        _log("[高通] 小米认证失败，设备可能需要官方授权");
                    }
                    return result;
                }
                catch (Exception ex)
                {
                    _log(string.Format("[高通] 小米认证异常: {0}", ex.Message));
                    return false;
                }
            }

            // 其他设备不自动认证，由用户手动选择
            return true;
        }

        /// <summary>
        /// 检测是否为小米设备 (通过 OEM ID 或其他特征)
        /// </summary>
        public bool IsXiaomiDevice()
        {
            if (ChipInfo == null) return false;

            // 通过 OEM ID 检测 (0x0072 = Xiaomi 官方)
            if (ChipInfo.OemId == 0x0072) return true;

            // 通过 PK Hash 前缀检测 (小米常见 PK Hash)
            if (!string.IsNullOrEmpty(ChipInfo.PkHash))
            {
                string pkLower = ChipInfo.PkHash.ToLowerInvariant();
                // 小米设备 PK Hash 前缀列表 (持续更新)
                string[] xiaomiPkHashPrefixes = new[]
                {
                    "c924a35f",  // 常见小米设备
                    "3373d5c8",
                    "e07be28b",
                    "6f5c4e17",
                    "57158eaf",
                    "355d47f9",
                    "a7b8b825",
                    "1c845b80",
                    "58b4add1",
                    "dd0cba2f",
                    "1bebe386"
                };

                foreach (var prefix in xiaomiPkHashPrefixes)
                {
                    if (pkLower.StartsWith(prefix))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// 手动执行 OPLUS O+认证 (基于 Digest 和 Signature)
        /// </summary>
        public async Task<bool> PerformVipAuthManualAsync(string digestPath, string signaturePath, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接设备");
                return false;
            }

            _log("[高通] 启动 OPLUS O+认证 (Digest + Sign)...");
            try
            {
                bool result = await _firehose.PerformVipAuthAsync(digestPath, signaturePath, ct);
                if (result)
                {
                    _log("[高通] O+认证成功，已进入高权限模式");
                    IsVipDevice = true; 
                }
                else
                {
                    _log("[高通] O+认证失败：校验未通过");
                }
                return result;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] O+认证异常: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 手动执行 OPLUS O+认证 (基于 byte[] 数据)
        /// 支持在发送 Digest 后直接写入签名数据
        /// </summary>
        /// <param name="digestData">Digest 数据 (Hash Segment, ~20-30KB)</param>
        /// <param name="signatureData">签名数据 (256 字节 RSA-2048)</param>
        public async Task<bool> PerformVipAuthAsync(byte[] digestData, byte[] signatureData, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接设备");
                return false;
            }

            _log(string.Format("[高通] 启动 O+认证 (Digest={0}B, Sign={1}B)...", 
                digestData?.Length ?? 0, signatureData?.Length ?? 0));
            try
            {
                bool result = await _firehose.PerformVipAuthAsync(digestData, signatureData, ct);
                if (result)
                {
                    _log("[高通] O+认证成功，已进入高权限模式");
                    IsVipDevice = true;
                }
                else
                {
                    _log("[高通] O+认证失败：校验未通过");
                }
                return result;
            }
            catch (Exception ex)
            {
                _log(string.Format("[高通] O+认证异常: {0}", ex.Message));
                return false;
            }
        }

        /// <summary>
        /// 分步执行 O+认证 - Step 1: 发送 Digest
        /// </summary>
        public async Task<bool> SendVipDigestAsync(byte[] digestData, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return false;
            return await _firehose.SendVipDigestAsync(digestData, ct);
        }

        /// <summary>
        /// 分步执行 O+认证 - Step 2-3: 准备 O+认证模式
        /// </summary>
        public async Task<bool> PrepareVipModeAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return false;
            return await _firehose.PrepareVipModeAsync(ct);
        }

        /// <summary>
        /// 分步执行 O+认证 - Step 4: 发送签名 (256 字节)
        /// 这是核心方法：在发送 Digest 后写入签名
        /// </summary>
        public async Task<bool> SendVipSignatureAsync(byte[] signatureData, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return false;
            return await _firehose.SendVipSignatureAsync(signatureData, ct);
        }

        /// <summary>
        /// 分步执行 O+认证 - Step 5: 完成认证
        /// </summary>
        public async Task<bool> FinalizeVipAuthAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return false;
            return await _firehose.FinalizeVipAuthAsync(ct);
        }

        /// <summary>
        /// 使用嵌入的奇美拉签名数据进行 O+认证
        /// </summary>
        /// <param name="platform">平台代号 (如 SM8550, SM8650 等)</param>
        public async Task<bool> PerformChimeraAuthAsync(string platform, CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接设备");
                return false;
            }

            // 从嵌入数据库获取签名数据
            var signData = ChimeraSignDatabase.Get(platform);
            if (signData == null)
            {
                _log(string.Format("[高通] 不支持的平台: {0}", platform));
                _log("[高通] 支持的平台: " + string.Join(", ", ChimeraSignDatabase.GetSupportedPlatforms()));
                return false;
            }

            _log(string.Format("[高通] 使用奇美拉签名: {0} ({1})", signData.Name, signData.Platform));
            _log(string.Format("[高通] Digest: {0} 字节, Signature: {1} 字节", 
                signData.DigestSize, signData.SignatureSize));

            return await PerformVipAuthAsync(signData.Digest, signData.Signature, ct);
        }

        /// <summary>
        /// 自动检测平台并使用奇美拉签名认证
        /// </summary>
        public async Task<bool> PerformChimeraAuthAutoAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null)
            {
                _log("[高通] 未连接设备");
                return false;
            }

            // 尝试从 Sahara 获取的芯片信息
            string platform = null;
            if (_sahara != null && _sahara.ChipInfo != null)
            {
                platform = _sahara.ChipInfo.ChipName;
                if (string.IsNullOrEmpty(platform) || platform == "Unknown")
                {
                    // 尝试从 MSM ID 推断
                    uint msmId = _sahara.ChipInfo.MsmId;
                    platform = QualcommDatabase.GetChipName(msmId);
                }
            }

            if (string.IsNullOrEmpty(platform) || platform == "Unknown")
            {
                _log("[高通] 无法自动检测平台，请手动指定");
                _log("[高通] 支持的平台: " + string.Join(", ", ChimeraSignDatabase.GetSupportedPlatforms()));
                return false;
            }

            _log(string.Format("[高通] 自动检测到平台: {0}", platform));
            return await PerformChimeraAuthAsync(platform, ct);
        }

        /// <summary>
        /// 获取支持的奇美拉平台列表
        /// </summary>
        public string[] GetSupportedChimeraPlatforms()
        {
            return ChimeraSignDatabase.GetSupportedPlatforms();
        }

        /// <summary>
        /// 检查平台是否支持奇美拉签名
        /// </summary>
        public bool IsChimeraSupported(string platform)
        {
            return ChimeraSignDatabase.IsSupported(platform);
        }

        /// <summary>
        /// 获取设备挑战码 (用于在线签名)
        /// </summary>
        public async Task<string> GetVipChallengeAsync(CancellationToken ct = default(CancellationToken))
        {
            if (_firehose == null) return null;
            return await _firehose.GetVipChallengeAsync(ct);
        }

        #endregion
    }
}
