// ============================================================================
// WackeEdl - Xiaomi Auth Strategies | 小米认证策略
// ============================================================================
// MiBypassAuthStrategy - 直接写入内置 sig 绕过 (不读取 blob)
// MiAuthStrategy       - 读取 blob 获取令牌，用户签名后写入 sig
// ============================================================================
// Copyright (c) 2025-2026 WackeEdl | MIT License
// ============================================================================

using System;
using System.Threading;
using System.Threading.Tasks;
using WackeEdl.Qualcomm.Protocol;

namespace WackeEdl.Qualcomm.Authentication
{
    // ==================== 公共工具 ====================

    /// <summary>
    /// 小米认证公共方法
    /// </summary>
    internal static class MiAuthHelper
    {
        /// <summary>
        /// 发送 sig 命令 + 写入签名二进制数据，验证是否认证成功
        /// </summary>
        public static async Task<bool> SendSigAndVerifyAsync(FirehoseClient client, byte[] sigData, Action<string> log, CancellationToken ct)
        {
            string sigCmd = "<?xml version=\"1.0\" ?><data><sig TargetName=\"sig\" size_in_bytes=\"256\" verbose=\"1\"/></data>";
            var sigResp = await client.SendRawXmlAsync(sigCmd, ct);

            if (sigResp == null || sigResp.Contains("NAK"))
                return false;

            var authResp = await client.SendRawBytesAndGetResponseAsync(sigData, ct);
            if (authResp != null && (authResp.ToLower().Contains("authenticated") || authResp.Contains("ACK")))
            {
                await Task.Delay(200, ct);
                if (await client.PingAsync(ct))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// 读取 blob (授权令牌，VQ 开头的 Base64 格式)
        /// </summary>
        public static async Task<string> GetBlobTokenAsync(FirehoseClient client, Action<string> log, CancellationToken ct)
        {
            try
            {
                string reqCmd = "<?xml version=\"1.0\" ?><data><sig TargetName=\"req\" /></data>";
                string response = await client.SendRawXmlAsync(reqCmd, ct);
                if (string.IsNullOrEmpty(response))
                    return null;

                string rawValue = ExtractAttribute(response, "value");
                if (string.IsNullOrEmpty(rawValue))
                    return null;

                if (rawValue.StartsWith("VQ"))
                    return rawValue;

                byte[] tokenBytes = HexToBytes(rawValue);
                if (tokenBytes != null && tokenBytes.Length > 0)
                    return Convert.ToBase64String(tokenBytes);

                return rawValue;
            }
            catch (Exception ex)
            {
                log("[MiAuth] 获取 blob 异常: " + ex.Message);
                return null;
            }
        }

        public static string ExtractAttribute(string xml, string attrName)
        {
            if (string.IsNullOrEmpty(xml)) return null;
            string pattern = attrName + "=\"";
            int start = xml.IndexOf(pattern);
            if (start < 0) return null;
            start += pattern.Length;
            int end = xml.IndexOf("\"", start);
            return end < 0 ? null : xml.Substring(start, end - start);
        }

        public static byte[] HexToBytes(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return null;
            hex = hex.Replace(" ", "").Replace("0x", "").Replace("0X", "");
            if (hex.Length % 2 != 0) return null;
            try
            {
                byte[] bytes = new byte[hex.Length / 2];
                for (int i = 0; i < bytes.Length; i++)
                    bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
                return bytes;
            }
            catch { return null; }
        }
    }

    // ==================== MiBypassAuth: 内置 sig 绕过 ====================

    /// <summary>
    /// 小米内置签名绕过认证 - 直接写入预置 sig，不读取 blob
    /// </summary>
    public class MiBypassAuthStrategy : IAuthStrategy
    {
        private readonly Action<string> _log;

        public string Name { get { return "Xiaomi (MiBypass - 内置签名绕过)"; } }

        private static readonly string[] BuiltInSigs = new[]
        {
            "k246jlc8rQfBZ2RLYSF4Ndha1P3bfYQKK3IlQy/NoTp8GSz6l57RZRfmlwsbB99sUW/sgfaWj89//dvDl6Fiwso" +
            "+XXYSSqF2nxshZLObdpMLTMZ1GffzOYd2d/ToryWChoK8v05ZOlfn4wUyaZJT4LHMXZ0NVUryvUbVbxjW5SkLpKDKwkMfnxnEwaOddmT" +
            "/q0ip4RpVk4aBmDW4TfVnXnDSX9tRI+ewQP4hEI8K5tfZ0mfyycYa0FTGhJPcTTP3TQzy1Krc1DAVLbZ8IqGBrW13YWN" +
            "/cMvaiEzcETNyA4N3kOaEXKWodnkwucJv2nEnJWTKNHY9NS9f5Cq3OPs4pQ==",

            "vzXWATo51hZr4Dh+a5sA/Q4JYoP4Ee3oFZSGbPZ2tBsaMupn" +
            "+6tPbZDkXJRLUzAqHaMtlPMKaOHrEWZysCkgCJqpOPkUZNaSbEKpPQ6uiOVJpJwA" +
            "/PmxuJ72inzSPevriMAdhQrNUqgyu4ATTEsOKnoUIuJTDBmzCeuh/34SOjTdO4Pc+s3ORfMD0TX+WImeUx4c9xVdSL/xirPl" +
            "/BouhfuwFd4qPPyO5RqkU/fevEoJWGHaFjfI302c9k7EpfRUhq1z+wNpZblOHuj0B3/7VOkK8KtSvwLkmVF" +
            "/t9ECiry6G5iVGEOyqMlktNlIAbr2MMYXn6b4Y3GDCkhPJ5LUkQ=="
        };

        public MiBypassAuthStrategy(Action<string> log = null)
        {
            _log = log ?? delegate { };
        }

        public async Task<bool> AuthenticateAsync(FirehoseClient client, string programmerPath, CancellationToken ct = default(CancellationToken))
        {
            _log("[MiBypass] 尝试内置签名绕过...");
            try
            {
                int index = 1;
                foreach (var base64 in BuiltInSigs)
                {
                    if (ct.IsCancellationRequested) break;
                    _log(string.Format("[MiBypass] 尝试签名 #{0}...", index));

                    byte[] sigData = Convert.FromBase64String(base64);
                    if (await MiAuthHelper.SendSigAndVerifyAsync(client, sigData, _log, ct))
                    {
                        _log("[MiBypass] 绕过成功！设备已解锁。");
                        return true;
                    }
                    index++;
                }

                _log("[MiBypass] 所有内置签名均无效。");
                return false;
            }
            catch (Exception ex)
            {
                _log("[MiBypass] 异常: " + ex.Message);
                return false;
            }
        }
    }

    // ==================== MiAuth: 读取 blob + 写入 sig ====================

    /// <summary>
    /// 小米完整认证 - 读取 blob 获取令牌，等待用户签名后写入 sig
    /// </summary>
    public class MiAuthStrategy : IAuthStrategy
    {
        private readonly Action<string> _log;

        public string Name { get { return "Xiaomi (MiAuth - 读取blob/写入sig)"; } }

        /// <summary>
        /// 当获取到 blob 令牌时触发 (Token 为 VQ 开头的 Base64 格式)
        /// </summary>
        public event Action<string> OnAuthTokenRequired;

        /// <summary>
        /// 最后获取的 blob 令牌
        /// </summary>
        public string LastBlobToken { get; private set; }

        public MiAuthStrategy(Action<string> log = null)
        {
            _log = log ?? delegate { };
        }

        public async Task<bool> AuthenticateAsync(FirehoseClient client, string programmerPath, CancellationToken ct = default(CancellationToken))
        {
            _log("[MiAuth] 正在读取 blob 获取授权令牌...");
            LastBlobToken = null;

            try
            {
                string token = await MiAuthHelper.GetBlobTokenAsync(client, _log, ct);
                if (!string.IsNullOrEmpty(token))
                {
                    LastBlobToken = token;
                    _log(string.Format("[MiAuth] blob 令牌: {0}", token));
                    _log("[MiAuth] 请复制令牌进行在线签名或官方申请。");
                    OnAuthTokenRequired?.Invoke(token);
                }
                else
                {
                    _log("[MiAuth] 无法获取 blob 令牌。");
                }

                // MiAuth 本身只负责获取 blob，返回 false 表示仍需用户提供 sig
                return false;
            }
            catch (Exception ex)
            {
                _log("[MiAuth] 异常: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 使用用户签名 (sig) 进行认证
        /// </summary>
        public async Task<bool> AuthenticateWithSignatureAsync(FirehoseClient client, string signatureBase64, CancellationToken ct = default(CancellationToken))
        {
            try
            {
                _log("[MiAuth] 使用签名 (sig) 进行认证...");
                byte[] sigData = Convert.FromBase64String(signatureBase64);
                if (await MiAuthHelper.SendSigAndVerifyAsync(client, sigData, _log, ct))
                {
                    _log("[MiAuth] 签名认证成功！设备已解锁。");
                    return true;
                }
                _log("[MiAuth] 签名验证失败");
                return false;
            }
            catch (Exception ex)
            {
                _log("[MiAuth] 签名认证异常: " + ex.Message);
                return false;
            }
        }
    }

    // ==================== 兼容旧接口 ====================

    /// <summary>
    /// 兼容旧代码的包装类 - 先尝试 Bypass，失败后回退到 MiAuth 读取 blob
    /// </summary>
    public class XiaomiAuthStrategy : IAuthStrategy
    {
        private readonly Action<string> _log;

        public string Name { get { return "Xiaomi (Auto: Bypass -> MiAuth)"; } }

        public event Action<string> OnAuthTokenRequired;
        public string LastAuthToken { get; private set; }

        public XiaomiAuthStrategy(Action<string> log = null)
        {
            _log = log ?? delegate { };
        }

        public async Task<bool> AuthenticateAsync(FirehoseClient client, string programmerPath, CancellationToken ct = default(CancellationToken))
        {
            // 先尝试 Bypass
            var bypass = new MiBypassAuthStrategy(_log);
            if (await bypass.AuthenticateAsync(client, programmerPath, ct))
                return true;

            // Bypass 失败，读取 blob
            var miAuth = new MiAuthStrategy(_log);
            miAuth.OnAuthTokenRequired += token => OnAuthTokenRequired?.Invoke(token);
            await miAuth.AuthenticateAsync(client, programmerPath, ct);
            LastAuthToken = miAuth.LastBlobToken;
            return false;
        }

        public async Task<bool> AuthenticateWithSignatureAsync(FirehoseClient client, string signatureBase64, CancellationToken ct = default(CancellationToken))
        {
            var miAuth = new MiAuthStrategy(_log);
            return await miAuth.AuthenticateWithSignatureAsync(client, signatureBase64, ct);
        }
    }
}
