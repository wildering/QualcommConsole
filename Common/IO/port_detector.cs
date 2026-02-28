// ============================================================================
// WackeEdl - Qualcomm Port Detector | 高通端口检测器
// ============================================================================
// [ZH] 高通端口检测 - 自动识别 9008/9006 EDL 端口
// [EN] Qualcomm Port Detector - Auto-detect 9008/9006 EDL ports
// [JA] Qualcommポート検出 - 9008/9006 EDLポートの自動識別
// [KO] Qualcomm 포트 탐지 - 9008/9006 EDL 포트 자동 식별
// [RU] Детектор портов Qualcomm - Автообнаружение портов 9008/9006 EDL
// [ES] Detector de puertos Qualcomm - Detección automática de puertos EDL
// ============================================================================
// Copyright (c) 2025-2026 WackeEdl | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WackeEdl.Qualcomm.Common
{
    public class DetectedPort
    {
        public string PortName { get; set; }
        public string Description { get; set; }
        public string DeviceId { get; set; }
        public PortType Type { get; set; }
        public bool IsEdl { get { return Type == PortType.Edl9008 || Type == PortType.Dload9006; } }

        public DetectedPort()
        {
            PortName = "";
            Description = "";
            DeviceId = "";
            Type = PortType.Unknown;
        }

        public override string ToString()
        {
            return string.Format("{0} - {1} ({2})", PortName, Description, Type);
        }
    }

    public enum PortType
    {
        Unknown,
        Edl9008,
        Dload9006,
        Diag9091,
        Adb,
        Fastboot,
        Other
    }

    public static class PortDetector
    {
        public static string[] GetAvailablePorts()
        {
            return SerialPort.GetPortNames();
        }

        public static List<DetectedPort> DetectAllPorts()
        {
            var result = new List<DetectedPort>();

            try
            {
                // 通过 Registry 枚举 USB 串口设备 (AOT 兼容，无需 System.Management)
                var availablePorts = new HashSet<string>(SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);
                EnumeratePortsFromRegistry(result, availablePorts);
            }
            catch { }

            // 补充 Registry 未覆盖的端口
            var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in result) found.Add(p.PortName);
            foreach (string portName in SerialPort.GetPortNames())
            {
                if (!found.Contains(portName))
                    result.Add(new DetectedPort { PortName = portName, Description = "串口", Type = PortType.Unknown });
            }

            return result;
        }

        private static void EnumeratePortsFromRegistry(List<DetectedPort> result, HashSet<string> availablePorts)
        {
            // HKLM\SYSTEM\CurrentControlSet\Enum\USB 下枚举所有设备
            string[] rootKeys = { @"SYSTEM\CurrentControlSet\Enum\USB", @"SYSTEM\CurrentControlSet\Enum\ACPI" };
            foreach (var rootPath in rootKeys)
            {
                using (var rootKey = Registry.LocalMachine.OpenSubKey(rootPath))
                {
                    if (rootKey == null) continue;
                    foreach (string vidPid in rootKey.GetSubKeyNames())
                    {
                        using (var vidPidKey = rootKey.OpenSubKey(vidPid))
                        {
                            if (vidPidKey == null) continue;
                            foreach (string serial in vidPidKey.GetSubKeyNames())
                            {
                                using (var instanceKey = vidPidKey.OpenSubKey(serial))
                                {
                                    if (instanceKey == null) continue;
                                    string classGuid = instanceKey.GetValue("ClassGUID")?.ToString() ?? "";
                                    // {4d36e978-e325-11ce-bfc1-08002be10318} = Ports (COM & LPT)
                                    if (!classGuid.Equals("{4d36e978-e325-11ce-bfc1-08002be10318}", StringComparison.OrdinalIgnoreCase))
                                        continue;

                                    string friendlyName = instanceKey.GetValue("FriendlyName")?.ToString() ?? "";
                                    string deviceId = rootPath.Substring(rootPath.LastIndexOf('\\') + 1) + "\\" + vidPid + "\\" + serial;

                                    // 从 Device Parameters 获取端口名
                                    string portName = null;
                                    using (var paramKey = instanceKey.OpenSubKey("Device Parameters"))
                                    {
                                        portName = paramKey?.GetValue("PortName")?.ToString();
                                    }

                                    // 回退：从 FriendlyName 提取
                                    if (string.IsNullOrEmpty(portName))
                                    {
                                        var m = Regex.Match(friendlyName, @"\(COM(\d+)\)");
                                        if (m.Success) portName = "COM" + m.Groups[1].Value;
                                    }

                                    if (string.IsNullOrEmpty(portName) || !availablePorts.Contains(portName))
                                        continue;

                                    result.Add(new DetectedPort
                                    {
                                        PortName = portName,
                                        Description = friendlyName,
                                        DeviceId = deviceId,
                                        Type = IdentifyPortType(deviceId, friendlyName)
                                    });
                                }
                            }
                        }
                    }
                }
            }
        }

        public static List<DetectedPort> DetectEdlPorts()
        {
            var all = DetectAllPorts();
            var edl = new List<DetectedPort>();
            foreach (var port in all)
            {
                if (port.IsEdl) edl.Add(port);
            }
            return edl;
        }

        public static DetectedPort GetFirstEdlPort()
        {
            var ports = DetectEdlPorts();
            return ports.Count > 0 ? ports[0] : null;
        }

        private static PortType IdentifyPortType(string deviceId, string description)
        {
            string upper = (deviceId + " " + description).ToUpperInvariant();

            if (upper.Contains("VID_05C6&PID_9008") || upper.Contains("VID_2A70&PID_9008") ||
                upper.Contains("VID_22D9&PID_9008") || upper.Contains("VID_2717&PID_9008"))
                return PortType.Edl9008;

            if (upper.Contains("VID_05C6&PID_9006") || upper.Contains("VID_05C6&PID_9007"))
                return PortType.Dload9006;

            if (upper.Contains("QDLOADER") || upper.Contains("9008") || upper.Contains("HS-USB"))
                return PortType.Edl9008;

            if (upper.Contains("DLOAD") || upper.Contains("9006"))
                return PortType.Dload9006;

            return PortType.Unknown;
        }

        public static async System.Threading.Tasks.Task<DetectedPort> WaitForEdlPortAsync(
            int timeoutMs = 30000,
            Action<string> log = null,
            System.Threading.CancellationToken ct = default(System.Threading.CancellationToken))
        {
            log = log ?? delegate { };
            int elapsed = 0;
            int interval = 500;

            while (elapsed < timeoutMs && !ct.IsCancellationRequested)
            {
                var port = GetFirstEdlPort();
                if (port != null)
                {
                    log(string.Format("[PortDetector] 检测到 EDL 端口: {0}", port.PortName));
                    return port;
                }

                await System.Threading.Tasks.Task.Delay(interval, ct);
                elapsed += interval;
            }

            return null;
        }
    }
}
