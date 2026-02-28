// ============================================================================
// WackeEdl - New Oplus Auth Strategy | 新 Oplus VIP 签名策略
// ============================================================================
// [ZH] 新签名方法 - 适用于新款 OPLUS 设备 (Digest + Signature VIP 认证)
//      严格按 fh_loader.c 源码实现:
//        1. SendSignedDigestTable (Digest 原始二进制) -> ACK 检查
//        2. SendXmlFiles: <verify EnableVip="1"/>
//        3. SendSignedDigestTable (Signature 原始二进制) -> ACK 检查
//        4. SendXmlFiles: <sha256init/>
//      纯 C# 串口通讯实现，不依赖 fh_loader.exe
// ============================================================================
// Copyright (c) 2025-2026 WackeEdl | MIT License
// ============================================================================

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using WackeEdl.Qualcomm.Protocol;

namespace WackeEdl.Qualcomm.Authentication
{
    /// <summary>
    /// 新 Oplus VIP 签名策略 (NEW) - 适用于新款 OPLUS 设备
    /// 严格按照 fh_loader.c 源码流程:
    ///   Step 1: Digest (完整文件原始二进制) -> ACK/NAK 检查
    ///   Step 2: Verify XML (EnableVip=1)
    ///   Step 3: Signature (完整文件原始二进制) -> ACK/NAK 检查
    ///   Step 4: SHA256Init XML
    /// 注意: 不含 TransferCfg (fh_loader.c 无此步骤)
    /// 注意: Digest/Signature 发送完整文件，不截断 (与 fh_loader.c SendSignedDigestTable 一致)
    /// </summary>
    public class NewOplusAuthStrategy : IAuthStrategy
    {
        private readonly Action<string> _log;
        private string _digestPath;
        private string _signaturePath;
        private byte[] _digestData;
        private byte[] _signatureData;

        public string Name { get { return "Oplus NEW (VIP Digest+Signature)"; } }

        /// <summary>
        /// 使用文件路径构造
        /// </summary>
        public NewOplusAuthStrategy(string digestPath, string signaturePath, Action<string> log = null)
        {
            _log = log ?? delegate { };
            _digestPath = digestPath;
            _signaturePath = signaturePath;
        }

        /// <summary>
        /// 使用 byte[] 数据构造
        /// </summary>
        public NewOplusAuthStrategy(byte[] digestData, byte[] signatureData, Action<string> log = null)
        {
            _log = log ?? delegate { };
            _digestData = digestData;
            _signatureData = signatureData;
        }

        public async Task<bool> AuthenticateAsync(FirehoseClient client, string programmerPath, CancellationToken ct = default(CancellationToken))
        {
            _log("[Oplus] 开始 VIP 认证 (基于 fh_loader.c 流程)...");

            try
            {
                // ===== 加载数据 =====
                byte[] digest = _digestData;
                byte[] signature = _signatureData;

                if (digest == null && !string.IsNullOrEmpty(_digestPath))
                {
                    if (!File.Exists(_digestPath))
                    {
                        _log(string.Format("[Oplus] Digest 文件不存在: {0}", _digestPath));
                        return false;
                    }
                    digest = File.ReadAllBytes(_digestPath);
                }

                if (signature == null && !string.IsNullOrEmpty(_signaturePath))
                {
                    if (!File.Exists(_signaturePath))
                    {
                        _log(string.Format("[Oplus] Signature 文件不存在: {0}", _signaturePath));
                        return false;
                    }
                    signature = File.ReadAllBytes(_signaturePath);
                }

                if (digest == null || digest.Length == 0)
                {
                    _log("[Oplus] Digest 数据为空");
                    return false;
                }
                if (signature == null || signature.Length == 0)
                {
                    _log("[Oplus] Signature 数据为空");
                    return false;
                }

                _log(string.Format("[Oplus] Digest: {0} 字节, Signature: {1} 字节", digest.Length, signature.Length));

                // Step 1/4: 发送 Digest — fh_loader.c: SendSignedDigestTable() 原始二进制, NAK 则终止
                _log("[Oplus] Step 1/4: 发送 Signed Digest Table...");
                var resp1 = await client.SendRawBytesAndGetResponseAsync(digest, ct);
                _log(string.Format("[Oplus] Step 1 响应: {0}", TruncateResponse(resp1)));
                if (resp1 != null && resp1.Contains("NAK"))
                {
                    _log("[Oplus] Digest 被拒绝 (NAK): 文件签名不正确或设备不匹配");
                    return false;
                }

                // Step 2/4: 发送 Verify — fh_loader: --sendxml <verify EnableVip="1"/>
                _log("[Oplus] Step 2/4: 发送 Verify (EnableVip=1)...");
                string verifyXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>" +
                    "<data><verify value=\"ping\" EnableVip=\"1\"/></data>";
                var resp2 = await client.SendRawXmlAsync(verifyXml, ct);
                _log(string.Format("[Oplus] Step 2 响应: {0}", TruncateResponse(resp2)));

                // Step 3/4: 发送 Signature — fh_loader.c: SendSignedDigestTable() 完整文件, NAK 则终止
                _log("[Oplus] Step 3/4: 发送 Signature...");
                var resp3 = await client.SendRawBytesAndGetResponseAsync(signature, ct);
                _log(string.Format("[Oplus] Step 3 响应: {0}", TruncateResponse(resp3)));
                if (resp3 != null && resp3.Contains("NAK"))
                {
                    _log("[Oplus] Signature 被拒绝 (NAK)");
                    return false;
                }

                // Step 4/4: 发送 SHA256Init — fh_loader: --sendxml <sha256init/>
                _log("[Oplus] Step 4/4: 发送 SHA256Init...");
                string sha256Xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?>" +
                    "<data><sha256init Verbose=\"1\"/></data>";
                var resp4 = await client.SendRawXmlAsync(sha256Xml, ct);
                _log(string.Format("[Oplus] Step 4 响应: {0}", TruncateResponse(resp4)));

                _log("[Oplus] VIP 认证完成");
                return true;
            }
            catch (OperationCanceledException)
            {
                _log("[Oplus] 认证被取消");
                throw;
            }
            catch (Exception ex)
            {
                _log(string.Format("[Oplus] 认证异常: {0}", ex.Message));
                return false;
            }
        }

        private static string TruncateResponse(string resp)
        {
            if (string.IsNullOrEmpty(resp)) return "(空)";
            if (resp.Length <= 200) return resp;
            return resp.Substring(0, 200) + "...";
        }
    }
}
