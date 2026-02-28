// ============================================================================
// WackeEdl Console - Main Program | 主程序入口
// [ZH] 高通 EDL 刷机工具 - 控制台版
// [EN] Qualcomm EDL Flash Tool - Console Edition
// ============================================================================
// Copyright (c) 2025-2026 WackeEdl | Licensed under CC BY-NC-SA 4.0
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WackeEdl.Qualcomm.Common;
using WackeEdl.Qualcomm.Common.Runtime;
using WackeEdl.Qualcomm.Console;
using WackeEdl.Qualcomm.Database;
using WackeEdl.Qualcomm.Models;
using System.Globalization;
using System.Xml.Linq;

namespace WackeEdl.Qualcomm
{
    class Program
    {
        private static ConsoleController _controller;
        private static string _selectedPort;
        private static string _programmerPath;
        private static string _storageType = "ufs";
        private static bool _verboseLog = false;
        private static bool _debugSensitiveData = false;
        private static bool _useVipMode = false;
        private static string _diagPort;
        private static int _diagBaudRate = 115200;
        private static int _diagImeiSlot = 1;
        private static StreamWriter _logWriter;
        private static readonly object _logLock = new object();

        static async Task Main(string[] args)
        {
            // 编码策略:
            // 默认跟随系统代码页，避免 UTF-8/GBK 互串造成乱码。
            // 仅在控制台已是 UTF-8 或 WACKEEDL_FORCE_UTF8=1 时启用 UTF-8。
            ConfigureConsoleEncoding();
            System.Console.Title = "WackeEdl - Qualcomm EDL Flash Tool (Console)";

            // --test 参数: 运行单元测试
            if (args.Length > 0 && args[0] == "--test")
            {
                Tests.AuthStrategyTests.RunAll();
                return;
            }

            if (args.Any(IsSensitiveDebugFlag))
                _debugSensitiveData = true;

            PrintBanner();

            // 初始化文件日志
            InitFileLogger();
            if (!QmslRuntimeLoader.EnsureLoaded(LogDetail))
            {
                LogDetail("[QMSL][Bootstrap] preload unavailable, will retry on first Diag operation.");
            }
            SensitiveDataPolicy.AllowSensitiveData = _debugSensitiveData;

            // 初始化控制器
            _controller = new ConsoleController(LogDetail);

            // 命令行参数处理
            if (args.Length > 0)
            {
                await HandleCommandLineArgs(args);
                CloseFileLogger();
                return;
            }

            // 交互式主循环
            await MainMenuLoop();

            // 资源清理
            _controller?.Dispose();
            CloseFileLogger();
            ConsoleController.Log("再见!", ConsoleColor.Cyan);
        }

        static void ConfigureConsoleEncoding()
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    System.Console.OutputEncoding = System.Text.Encoding.UTF8;
                    System.Console.InputEncoding = System.Text.Encoding.UTF8;
                    return;
                }

                bool forceUtf8 = string.Equals(
                    Environment.GetEnvironmentVariable("WACKEEDL_FORCE_UTF8"),
                    "1",
                    StringComparison.Ordinal);
                bool consoleAlreadyUtf8 =
                    System.Console.OutputEncoding.CodePage == 65001 &&
                    System.Console.InputEncoding.CodePage == 65001;

                if (forceUtf8 || consoleAlreadyUtf8)
                {
                    System.Console.OutputEncoding = System.Text.Encoding.UTF8;
                    System.Console.InputEncoding = System.Text.Encoding.UTF8;
                }
            }
            catch
            {
                // 保留系统默认编码
            }
        }

        #region 文件日志

        static void InitFileLogger()
        {
            try
            {
                string logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
                Directory.CreateDirectory(logDir);
                string logFile = Path.Combine(logDir, $"WackeEdl_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                _logWriter = new StreamWriter(logFile, false, System.Text.Encoding.UTF8) { AutoFlush = true };
                _logWriter.WriteLine($"=== WackeEdl Console Log - {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
                _logWriter.WriteLine($"OS: {Environment.OSVersion}");
                _logWriter.WriteLine();
            }
            catch
            {
                // 日志初始化失败不影响主流程
            }
        }

        static void CloseFileLogger()
        {
            try { _logWriter?.Dispose(); } catch { }
        }

        /// <summary>
        /// 详细日志: 始终写文件，`-verbose` 时同步输出到控制台
        /// </summary>
        static void LogDetail(string msg)
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
            lock (_logLock)
            {
                try { _logWriter?.WriteLine(line); } catch { }
            }
            if (_verboseLog)
                System.Console.WriteLine($"[DBG] {msg}");
        }

        #endregion

        static void PrintBanner()
        {
            ConsoleController.Log("", null);
            ConsoleController.Log("╔══════════════════════════════════════════════╗", ConsoleColor.Cyan);
            ConsoleController.Log("║    WackeEdl - Qualcomm EDL Flash Tool        ║", ConsoleColor.Cyan);
            ConsoleController.Log("║              Console Edition                 ║", ConsoleColor.Cyan);
            ConsoleController.Log("║         Copyright (C) 2025 Wildering         ║", ConsoleColor.Cyan);
            ConsoleController.Log("╚══════════════════════════════════════════════╝", ConsoleColor.Cyan);
            ConsoleController.Log("", null);
            ConsoleController.Log("   此工具开发仅供学习交流使用    请在24小时内删除", ConsoleColor.Cyan);
            ConsoleController.Log("", null);
        }

        #region 交互式菜单
        static async Task MainMenuLoop()
        {
            while (true)
            {
                PrintMainMenu();
                string input = ReadInput("选择");
                if (string.IsNullOrEmpty(input)) continue;

                switch (input)
                {
                    case "1": await MenuRefreshPorts(); break;
                    case "2": await MenuConnect(); break;
                    case "3": await MenuReadPartitionTable(); break;
                    case "4": await MenuReadPartition(); break;
                    case "5": await MenuWritePartition(); break;
                    case "6": await MenuErasePartition(); break;
                    case "7": await MenuBatchFlash(); break;
                    case "8": await MenuDeviceInfo(); break;
                    case "9": await MenuDeviceControl(); break;
                    case "10": MenuSettings(); break;
                    case "11": await MenuDiagQcn(); break;
                    case "0": case "q": case "exit":
                        _controller?.Disconnect();
                        return;
                    default:
                        ConsoleController.Log("无效选项", ConsoleColor.Yellow);
                        break;
                }
            }
        }

        static void PrintMainMenu()
        {
            ConsoleController.Log("", null);
            string connStatus = _controller.IsConnected ? "已连接" : "未连接";
            var connColor = _controller.IsConnected ? ConsoleColor.Green : ConsoleColor.Red;
            ConsoleController.Log("状态:", ConsoleColor.White);
            ConsoleController.Log($"  设备: {connStatus}  |  端口: {_selectedPort ?? "未选择"}  |  存储: {_storageType.ToUpper()}", connColor);
            if (_controller.HasPartitions)
                ConsoleController.Log($"  分区: {_controller.Partitions.Count} 个  |  保护: {(_controller.ProtectSensitivePartitions ? "开启" : "关闭")}", ConsoleColor.White);

            ConsoleController.Log("", null);
            ConsoleController.Log("═══════════════ 主菜单 ═══════════════", ConsoleColor.Cyan);
            ConsoleController.Log("  [1]  刷新端口列表", ConsoleColor.White);
            ConsoleController.Log("  [2]  连接设备", ConsoleColor.White);
            ConsoleController.Log("  [3]  读取分区表", ConsoleColor.White);
            ConsoleController.Log("  [4]  读取分区 (备份)", ConsoleColor.White);
            ConsoleController.Log("  [5]  写入分区", ConsoleColor.White);
            ConsoleController.Log("  [6]  擦除分区", ConsoleColor.White);
            ConsoleController.Log("  [7]  批量刷写 (rawprogram)", ConsoleColor.White);
            ConsoleController.Log("  [8]  设备信息", ConsoleColor.White);
            ConsoleController.Log("  [9]  设备控制 (重启/切槽/激活)", ConsoleColor.White);
            ConsoleController.Log("  [10] 设置", ConsoleColor.White);
            ConsoleController.Log("  [11] Diag 模式 (QCN 读写)", ConsoleColor.White);
            ConsoleController.Log("  [0]  退出", ConsoleColor.DarkGray);
            ConsoleController.Log("══════════════════════════════════════", ConsoleColor.Cyan);
        }

        #endregion

        #region 菜单: 端口

        static async Task MenuRefreshPorts()
        {
            var ports = _controller.RefreshPorts();
            if (ports.Count > 0)
            {
                System.Console.Write("选择端口编号 (回车跳过): ");
                string input = System.Console.ReadLine()?.Trim();
                if (int.TryParse(input, out int idx) && idx >= 1 && idx <= ports.Count)
                {
                    _selectedPort = ports[idx - 1].PortName;
                    ConsoleController.Log($"已选择: {_selectedPort}", ConsoleColor.Green);
                }
                else if (string.IsNullOrEmpty(input))
                {
                    // 自动选择 EDL 端口
                    var edl = ports.FirstOrDefault(p => p.IsEdl);
                    if (edl != null)
                    {
                        _selectedPort = edl.PortName;
                        ConsoleController.Log($"自动选择 EDL 端口: {_selectedPort}", ConsoleColor.Green);
                    }
                }
            }
        }

        #endregion

        #region 菜单: 连接

        static async Task MenuConnect()
        {
            if (_controller.IsConnected)
            {
                ConsoleController.Log("设备已连接，是否断开? (y/n): ", ConsoleColor.Yellow);
                if (System.Console.ReadLine()?.Trim().ToLower() == "y")
                {
                    _controller.Disconnect();
                }
                return;
            }

            // ===== Step 1: 搜索设备端口 =====
            ConsoleController.Log("", null);
            ConsoleController.Log("正在搜索 EDL 设备...", ConsoleColor.Cyan);
            var ports = _controller.RefreshPorts();

            if (ports.Count == 0)
            {
                ConsoleController.Log("未检测到任何设备，请连接设备后重试", ConsoleColor.Red);
                return;
            }

            // 自动选择 EDL 端口，或手动选择
            var edlPorts = ports.Where(p => p.IsEdl).ToList();
            if (edlPorts.Count == 1)
            {
                _selectedPort = edlPorts[0].PortName;
                ConsoleController.Log($"自动选择 EDL 端口: {_selectedPort}", ConsoleColor.Green);
            }
            else if (edlPorts.Count > 1)
            {
                ConsoleController.Log("检测到多个 EDL 端口，请选择:", ConsoleColor.Yellow);
                for (int i = 0; i < edlPorts.Count; i++)
                    ConsoleController.Log($"  [{i + 1}] {edlPorts[i].PortName} - {edlPorts[i].Description}", ConsoleColor.White);
                System.Console.Write("端口编号: ");
                if (int.TryParse(System.Console.ReadLine()?.Trim(), out int idx) && idx >= 1 && idx <= edlPorts.Count)
                    _selectedPort = edlPorts[idx - 1].PortName;
                else { ConsoleController.Log("无效选择", ConsoleColor.Red); return; }
            }
            else
            {
                // 无 EDL 端口，让用户从所有端口中选择
                ConsoleController.Log("未检测到 EDL 端口，请手动选择:", ConsoleColor.Yellow);
                System.Console.Write("端口编号: ");
                if (int.TryParse(System.Console.ReadLine()?.Trim(), out int idx) && idx >= 1 && idx <= ports.Count)
                    _selectedPort = ports[idx - 1].PortName;
                else { ConsoleController.Log("无效选择", ConsoleColor.Red); return; }
            }

            // ===== Step 2: 自动探测设备模式 =====
            EdlDeviceMode probedMode = await _controller.ProbeDeviceModeAsync(_selectedPort);
            string modeText = probedMode switch
            {
                EdlDeviceMode.Firehose => "Firehose",
                EdlDeviceMode.Sahara => "Sahara",
                _ => "未知"
            };
            ConsoleController.Log($"检测结果: {modeText}", ConsoleColor.Cyan);

            // ===== Step 3: 按模式选择连接方式 =====
            ConsoleController.Log("", null);
            ConsoleController.Log("连接方式:", ConsoleColor.Cyan);
            if (probedMode == EdlDeviceMode.Firehose)
            {
                ConsoleController.Log("  [1] 直接连接 Firehose (推荐)", ConsoleColor.White);
                ConsoleController.Log("  [2] 使用 Loader 文件连接", ConsoleColor.White);
            }
            else
            {
                ConsoleController.Log("  [1] 使用 Loader 文件连接 (推荐)", ConsoleColor.White);
                ConsoleController.Log("  [2] 跳过 Sahara 直连 Firehose (高级)", ConsoleColor.White);
            }
            ConsoleController.Log("  [0] 返回", ConsoleColor.DarkGray);

            string choice = ReadInput("选择");
            if (choice == "0" || string.IsNullOrEmpty(choice)) return;

            bool useLoaderPath = false;
            bool useDirectPath = false;
            if (probedMode == EdlDeviceMode.Firehose)
            {
                useDirectPath = choice == "1";
                useLoaderPath = choice == "2";
            }
            else
            {
                useLoaderPath = choice == "1";
                useDirectPath = choice == "2";
            }

            if (!useLoaderPath && !useDirectPath)
            {
                ConsoleController.Log("无效选择", ConsoleColor.Red);
                return;
            }

            // Reset per connect attempt
            _useVipMode = false;
            string authMode = "none";
            string digestPath = null;
            string signaturePath = null;

            if (useDirectPath)
            {
                if (probedMode == EdlDeviceMode.Sahara)
                {
                    ConsoleController.Log("当前更像 Sahara 模式，直连失败概率较高。", ConsoleColor.Yellow);
                }
                System.Console.Write("是否探测 Oplus/VIP 欺骗模式? [y/N]: ");
                string vipInput = System.Console.ReadLine()?.Trim().ToLowerInvariant();
                _useVipMode = (vipInput == "y" || vipInput == "yes");

                _controller.SkipSahara = true;
                await _controller.ConnectAsync(_selectedPort, "", _storageType, probeVipSpoof: _useVipMode);
                return;
            }

            // useLoaderPath
            if (string.IsNullOrEmpty(_programmerPath) || !File.Exists(_programmerPath))
            {
                System.Console.Write("Loader 文件路径: ");
                _programmerPath = System.Console.ReadLine()?.Trim().Trim('"');
            }
            if (string.IsNullOrEmpty(_programmerPath) || !File.Exists(_programmerPath))
            {
                ConsoleController.Log("Loader 文件不存在", ConsoleColor.Red);
                return;
            }
            ConsoleController.Log($"Loader: {Path.GetFileName(_programmerPath)}", ConsoleColor.Green);

            ConsoleController.Log("", null);
            ConsoleController.Log("严格认证模式:", ConsoleColor.Cyan);
            ConsoleController.Log("  [0] none       - 无认证 (严格无认证)", ConsoleColor.White);
            ConsoleController.Log("  [1] oplus_new  - Oplus 新签名 (Digest+Signature)", ConsoleColor.White);
            ConsoleController.Log("  [2] oplus_old  - Oplus 旧签名 (demacia/setprojmodel)", ConsoleColor.White);
            ConsoleController.Log("  [3] xiaomi     - 小米认证", ConsoleColor.White);
            System.Console.Write("选择认证模式 [0]: ");
            string authInput = System.Console.ReadLine()?.Trim();
            switch (authInput)
            {
                case "1": authMode = "oplus_new"; break;
                case "2": authMode = "oplus_old"; break;
                case "3": authMode = "xiaomi"; break;
                default: authMode = "none"; break;
            }

            if (authMode == "oplus_new")
            {
                System.Console.Write("Digest 文件路径: ");
                digestPath = System.Console.ReadLine()?.Trim().Trim('"');
                if (string.IsNullOrEmpty(digestPath) || !File.Exists(digestPath))
                {
                    ConsoleController.Log("Digest 文件不存在", ConsoleColor.Red);
                    return;
                }

                System.Console.Write("Signature 文件路径: ");
                signaturePath = System.Console.ReadLine()?.Trim().Trim('"');
                if (string.IsNullOrEmpty(signaturePath) || !File.Exists(signaturePath))
                {
                    ConsoleController.Log("Signature 文件不存在", ConsoleColor.Red);
                    return;
                }

                ConsoleController.Log($"Digest: {Path.GetFileName(digestPath)}", ConsoleColor.Green);
                ConsoleController.Log($"Signature: {Path.GetFileName(signaturePath)}", ConsoleColor.Green);
            }

            byte[] data = File.ReadAllBytes(_programmerPath);
            string name = Path.GetFileName(_programmerPath);
            bool connected;
            if (authMode == "oplus_new")
            {
                connected = await _controller.ConnectWithNewOplusAsync(_selectedPort, _storageType, data, digestPath, signaturePath);
            }
            else
            {
                connected = await _controller.ConnectWithLoaderDataAsync(_selectedPort, data, name, _storageType, authMode);
            }

            if (connected && authMode == "oplus_new")
            {
                ConsoleController.Log("Oplus 新设备刷写模式连接成功", ConsoleColor.Green);
            }
        }
        #endregion

        #region 菜单: 分区表
        static async Task MenuReadPartitionTable()
        {
            if (!EnsureConnected()) return;
            await _controller.ReadPartitionTableAsync();
        }

        #endregion

        #region 菜单: 读取分区

        static async Task MenuReadPartition()
        {
            if (!EnsureConnected() || !EnsurePartitions()) return;

            _controller.PrintPartitionTable();
            string partInput = ReadInput("分区名/序号 (多个用逗号分隔)");
            if (string.IsNullOrWhiteSpace(partInput)) return;

            List<PartitionInfo> selectedPartitions = ResolvePartitions(partInput);
            if (selectedPartitions.Count == 0)
            {
                ConsoleController.Log("未选择有效分区", ConsoleColor.Red);
                return;
            }

            string defaultFolder = "backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            System.Console.Write($"保存文件夹 [{defaultFolder}]: ");
            string outputFolder = System.Console.ReadLine()?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(outputFolder))
                outputFolder = defaultFolder;

            string fullOutputFolder = Path.GetFullPath(outputFolder);
            if (!Directory.Exists(fullOutputFolder))
                Directory.CreateDirectory(fullOutputFolder);

            System.Console.Write("是否生成 rawprogram*.xml (按LUN)? [Y/n]: ");
            string genXmlInput = System.Console.ReadLine()?.Trim().ToLowerInvariant();
            bool generateRawprogramXml = string.IsNullOrEmpty(genXmlInput) || genXmlInput == "y" || genXmlInput == "yes";

            long totalPartitionBytes = selectedPartitions.Sum(GetPartitionSizeBytes);
            string rootPath = Path.GetPathRoot(fullOutputFolder);
            DriveInfo drive = new DriveInfo(rootPath);
            long freeBytes = drive.AvailableFreeSpace;

            if (freeBytes < totalPartitionBytes)
            {
                ConsoleController.Log("所选文件夹磁盘剩余空间不足", ConsoleColor.Red);
                return;
            }

            ConsoleController.Log(
                $"准备读取 {selectedPartitions.Count} 个分区到: {fullOutputFolder} (总计: {QualcommConsole.Common.SizeFormatter.FormatSize(totalPartitionBytes)})",
                ConsoleColor.Cyan);

            int success = 0;
            var successPartitions = new List<PartitionInfo>();
            for (int i = 0; i < selectedPartitions.Count; i++)
            {
                PartitionInfo partition = selectedPartitions[i];
                string outputPath = Path.Combine(fullOutputFolder, partition.Name + ".img");
                ConsoleController.Log($"[{i + 1}/{selectedPartitions.Count}] 读取 {partition.Name} -> {outputPath}", ConsoleColor.White);
                bool ok = await _controller.ReadPartitionAsync(partition.Name, outputPath);
                if (ok)
                {
                    success++;
                    successPartitions.Add(partition);
                }
            }

            ConsoleController.Log(
                success == selectedPartitions.Count
                    ? $"读取完成: {success}/{selectedPartitions.Count}"
                    : $"读取完成: {success}/{selectedPartitions.Count} (部分失败)",
                success == selectedPartitions.Count ? ConsoleColor.Green : ConsoleColor.Yellow);

            if (generateRawprogramXml && successPartitions.Count > 0)
            {
                List<string> rawprogramPaths = GenerateRawprogramXmlByLun(fullOutputFolder, successPartitions);
                foreach (string rawprogramPath in rawprogramPaths)
                {
                    ConsoleController.Log($"已生成: {rawprogramPath}", ConsoleColor.Cyan);
                }
            }
        }

        #endregion

        #region 菜单: 写入分区

        static async Task MenuWritePartition()
        {
            if (!EnsureConnected() || !EnsurePartitions()) return;

            _controller.PrintPartitionTable();
            string partName = ReadInput("分区名 (或序号)");
            if (string.IsNullOrEmpty(partName)) return;

            PartitionInfo partition = ResolvePartition(partName);
            if (partition == null) return;

            System.Console.Write("写入文件路径: ");
            string filePath = System.Console.ReadLine()?.Trim().Trim('"');
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                ConsoleController.Log("文件不存在", ConsoleColor.Red);
                return;
            }

            // 确认
            long fileSize = new FileInfo(filePath).Length;
            ConsoleController.Log($"即将写入: {partition.Name} <- {Path.GetFileName(filePath)} ({QualcommConsole.Common.SizeFormatter.FormatSize(fileSize)})", ConsoleColor.Yellow);
            System.Console.Write("确认? (y/n): ");
            if (System.Console.ReadLine()?.Trim().ToLower() != "y") return;

            await _controller.WritePartitionAsync(partition.Name, filePath);
        }

        #endregion

        #region 菜单: 擦除分区

        static async Task MenuErasePartition()
        {
            if (!EnsureConnected() || !EnsurePartitions()) return;

            _controller.PrintPartitionTable();
            string partName = ReadInput("分区名 (或序号)");
            if (string.IsNullOrEmpty(partName)) return;

            PartitionInfo partition = ResolvePartition(partName);
            if (partition == null) return;

            ConsoleController.Log($"即将擦除: {partition.Name} ({partition.FormattedSize})", ConsoleColor.Red);
            System.Console.Write("确认? (y/n): ");
            if (System.Console.ReadLine()?.Trim().ToLower() != "y") return;

            await _controller.ErasePartitionAsync(partition.Name);
        }

        #endregion

        #region 菜单: 批量刷写

        static async Task MenuBatchFlash()
        {
            if (!EnsureConnected() || !EnsurePartitions()) return;

            ConsoleController.Log("", null);
            ConsoleController.Log("批量刷写方式:", ConsoleColor.Cyan);
            ConsoleController.Log("  [1] 从 rawprogram XML 刷写", ConsoleColor.White);
            ConsoleController.Log("  [2] 从文件夹自动匹配刷写", ConsoleColor.White);
            ConsoleController.Log("  [0] 返回", ConsoleColor.DarkGray);

            string choice = ReadInput("选择");
            switch (choice)
            {
                case "1": await BatchFlashFromRawprogram(); break;
                case "2": await BatchFlashFromFolder(); break;
            }
        }

        static async Task BatchFlashFromRawprogram()
        {
            System.Console.Write("rawprogram XML 文件路径: ");
            string xmlPath = System.Console.ReadLine()?.Trim().Trim('"');
            if (string.IsNullOrEmpty(xmlPath) || !File.Exists(xmlPath))
            {
                ConsoleController.Log("文件不存在", ConsoleColor.Red);
                return;
            }

            string baseDir = Path.GetDirectoryName(xmlPath);
            ConsoleController.Log("解析 rawprogram...", ConsoleColor.Cyan);

            var parser = new RawprogramParser(baseDir, msg => ConsoleController.Log(msg));
            var packageInfo = parser.LoadPackage();

            if (packageInfo.Tasks.Count == 0)
            {
                ConsoleController.Log("未找到可刷写任务", ConsoleColor.Yellow);
                return;
            }

            // 显示任务列表
            ConsoleController.Log($"找到 {packageInfo.Tasks.Count} 个刷写任务:", ConsoleColor.Cyan);
            int displayCount = Math.Min(packageInfo.Tasks.Count, 30);
            for (int i = 0; i < displayCount; i++)
            {
                var t = packageInfo.Tasks[i];
                ConsoleController.Log($"  {t.Label,-24} {t.Filename,-30} {t.FormattedSize}", ConsoleColor.White);
            }
            if (packageInfo.Tasks.Count > 30)
                ConsoleController.Log($"  ... 还有 {packageInfo.Tasks.Count - 30} 个任务", ConsoleColor.DarkGray);

            if (packageInfo.PatchFiles.Count > 0)
                ConsoleController.Log($"Patch 文件: {packageInfo.PatchFiles.Count} 个", ConsoleColor.Cyan);

            System.Console.Write("确认开始刷写? (y/n): ");
            if (System.Console.ReadLine()?.Trim().ToLower() != "y") return;

            var writeTasks = BuildWriteTasksFromPackage(packageInfo, baseDir);
            await _controller.WritePartitionsBatchAsync(writeTasks, packageInfo.PatchFiles, _storageType == "ufs");
        }

        static async Task BatchFlashFromFolder()
        {
            System.Console.Write("固件文件夹路径: ");
            string folder = System.Console.ReadLine()?.Trim().Trim('"');
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                ConsoleController.Log("文件夹不存在", ConsoleColor.Red);
                return;
            }

            // 扫描文件夹中与分区名匹配的文件
            var matchedTasks = new List<Tuple<string, string, int, long>>();
            var files = new[] { "*.img", "*.bin", "*.mbn", "*.elf" }
                .SelectMany(ext => Directory.GetFiles(folder, ext))
                .ToList();

            foreach (var filePath in files)
            {
                string fileName = Path.GetFileNameWithoutExtension(filePath);
                var partition = _controller.Partitions.FirstOrDefault(p =>
                    p.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase));

                if (partition != null)
                {
                    matchedTasks.Add(Tuple.Create(partition.Name, filePath, partition.Lun, (long)partition.StartSector));
                }
            }

            if (matchedTasks.Count == 0)
            {
                ConsoleController.Log("未找到匹配的文件", ConsoleColor.Yellow);
                return;
            }

            ConsoleController.Log($"匹配到 {matchedTasks.Count} 个分区:", ConsoleColor.Cyan);
            foreach (var t in matchedTasks)
            {
                long size = new FileInfo(t.Item2).Length;
                ConsoleController.Log($"  {t.Item1,-24} <- {Path.GetFileName(t.Item2)} ({QualcommConsole.Common.SizeFormatter.FormatSize(size)})", ConsoleColor.White);
            }

            System.Console.Write("确认开始刷写? (y/n): ");
            if (System.Console.ReadLine()?.Trim().ToLower() != "y") return;

            await _controller.WritePartitionsBatchAsync(matchedTasks, null, _storageType == "ufs");
        }

        #endregion

        #region 菜单: 设备信息

        static async Task MenuDeviceInfo()
        {
            if (!EnsureConnected()) return;

            ConsoleController.Log("", null);
            ConsoleController.Log("设备信息:", ConsoleColor.Cyan);
            ConsoleController.Log("  [1] 显示基本信息", ConsoleColor.White);
            ConsoleController.Log("  [2] 深度扫描 (读取 build.prop)", ConsoleColor.White);
            ConsoleController.Log("  [3] 显示分区表", ConsoleColor.White);
            ConsoleController.Log("  [4] 读取存储信息 (getstorageinfo)", ConsoleColor.White);
            ConsoleController.Log("  [0] 返回", ConsoleColor.DarkGray);

            string choice = ReadInput("选择");
            switch (choice)
            {
                case "1":
                    _controller.PrintDeviceInfo();
                    break;
                case "2":
                    if (!EnsurePartitions()) return;
                    await _controller.ReadBuildPropFromDeviceAsync();
                    break;
                case "3":
                    _controller.PrintPartitionTable();
                    break;
                case "4":
                    await _controller.ReadStorageInfoAsync();
                    break;
            }
        }

        #endregion

        #region 菜单: 设备控制

        static async Task MenuDeviceControl()
        {
            ConsoleController.Log("", null);
            ConsoleController.Log("设备控制:", ConsoleColor.Cyan);
            ConsoleController.Log("  [1] 重启到系统", ConsoleColor.White);
            ConsoleController.Log("  [2] 重启到 EDL", ConsoleColor.White);
            ConsoleController.Log("  [3] 切换 A/B 槽位", ConsoleColor.White);
            ConsoleController.Log("  [4] 激活启动 LUN", ConsoleColor.White);
            ConsoleController.Log("  [5] 重置 Sahara", ConsoleColor.White);
            ConsoleController.Log("  [0] 返回", ConsoleColor.DarkGray);

            string choice = ReadInput("选择");
            switch (choice)
            {
                case "1":
                    if (!EnsureConnected()) return;
                    await _controller.RebootToSystemAsync();
                    break;
                case "2":
                    if (!EnsureConnected()) return;
                    await _controller.RebootToEdlAsync();
                    break;
                case "3":
                    if (!EnsureConnected()) return;
                    System.Console.Write("槽位 (a/b): ");
                    string slot = System.Console.ReadLine()?.Trim().ToLower();
                    if (slot == "a" || slot == "b")
                        await _controller.SwitchSlotAsync(slot);
                    break;
                case "4":
                    if (!EnsureConnected()) return;
                    System.Console.Write("LUN 编号 (1/2): ");
                    if (int.TryParse(System.Console.ReadLine()?.Trim(), out int lun))
                        await _controller.SetBootLunAsync(lun);
                    break;
                case "5":
                    if (string.IsNullOrEmpty(_selectedPort))
                    {
                        ConsoleController.Log("请先选择端口", ConsoleColor.Yellow);
                        await MenuRefreshPorts();
                    }
                    if (!string.IsNullOrEmpty(_selectedPort))
                        await _controller.ResetSaharaAsync(_selectedPort);
                    break;
            }
        }

        #endregion

        #region 菜单: Diag / QCN

        static async Task MenuDiagQcn()
        {
            ConsoleController.Log("", null);
            ConsoleController.Log("Diag / QCN / IMEI:", ConsoleColor.Cyan);
            ConsoleController.Log($"  当前端口: {_diagPort ?? "未设置"}", ConsoleColor.White);
            ConsoleController.Log("  [1] 连接 Diag 端口", ConsoleColor.White);
            ConsoleController.Log("  [2] 读取 QCN (备份)", ConsoleColor.White);
            ConsoleController.Log("  [3] 写入 QCN (恢复)", ConsoleColor.White);
            ConsoleController.Log("  [4] 断开 Diag", ConsoleColor.White);
            ConsoleController.Log("  [5] 读取 IMEI", ConsoleColor.White);
            ConsoleController.Log("  [6] 写入 IMEI", ConsoleColor.White);
            ConsoleController.Log("  [7] 读取所有 IMEI", ConsoleColor.White);
            ConsoleController.Log("  [0] 返回", ConsoleColor.DarkGray);

            string choice = ReadInput("选择");
            switch (choice)
            {
                case "1":
                    await MenuDiagConnect();
                    break;
                case "2":
                    await MenuReadQcn();
                    break;
                case "3":
                    await MenuWriteQcn();
                    break;
                case "4":
                    _controller.DisconnectDiag();
                    break;
                case "5":
                    await MenuReadImei();
                    break;
                case "6":
                    await MenuWriteImei();
                    break;
                case "7":
                    await MenuReadAllImei();
                    break;
            }
        }

        static async Task MenuDiagConnect()
        {
            var ports = _controller.RefreshPorts(silent: true);
            if (ports.Count > 0)
            {
                ConsoleController.Log("可用端口:", ConsoleColor.Cyan);
                for (int i = 0; i < ports.Count; i++)
                    ConsoleController.Log($"  [{i + 1}] {ports[i].PortName} - {ports[i].Description}", ConsoleColor.White);
            }

            System.Console.Write($"Diag 端口 [{_diagPort ?? _selectedPort ?? "COM1"}]: ");
            string portInput = System.Console.ReadLine()?.Trim();
            if (!string.IsNullOrEmpty(portInput))
            {
                if (int.TryParse(portInput, out int index) && index >= 1 && index <= ports.Count)
                    _diagPort = ports[index - 1].PortName;
                else
                    _diagPort = portInput;
            }

            if (string.IsNullOrWhiteSpace(_diagPort))
                _diagPort = _selectedPort;

            if (string.IsNullOrWhiteSpace(_diagPort))
            {
                ConsoleController.Log("未指定 Diag 端口", ConsoleColor.Red);
                return;
            }

            await _controller.ConnectDiagAsync(_diagPort, _diagBaudRate);
        }

        static async Task MenuReadQcn()
        {
            if (!await EnsureDiagConnected()) return;

            string defaultDir = Directory.GetCurrentDirectory();
            System.Console.Write($"保存目录 [{defaultDir}]: ");
            string outputDir = System.Console.ReadLine()?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(outputDir))
                outputDir = defaultDir;

            string outputPath;
            try
            {
                outputPath = BuildQcnBackupOutputPath(outputDir);
            }
            catch (Exception ex)
            {
                ConsoleController.Log("保存目录无效: " + ex.Message, ConsoleColor.Red);
                return;
            }

            ConsoleController.Log($"将保存为: {outputPath}", ConsoleColor.Yellow);

            await _controller.ReadQcnAsync(outputPath);
        }

        static async Task MenuWriteQcn()
        {
            if (!await EnsureDiagConnected()) return;

            System.Console.Write("QCN 文件路径: ");
            string filePath = System.Console.ReadLine()?.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                ConsoleController.Log("QCN 文件不存在", ConsoleColor.Red);
                return;
            }

            ConsoleController.Log($"即将写入 QCN: {filePath}", ConsoleColor.Yellow);
            System.Console.Write("确认? (y/n): ");
            if (System.Console.ReadLine()?.Trim().ToLower() != "y") return;

            await _controller.WriteQcnAsync(filePath);
        }

        static async Task MenuReadImei()
        {
            if (!await EnsureDiagConnected()) return;

            int slot = PromptImeiSlot(_diagImeiSlot);
            _diagImeiSlot = slot;
            await _controller.ReadDiagImeiAsync(slot);
        }

        static async Task MenuWriteImei()
        {
            if (!await EnsureDiagConnected()) return;

            int slot = PromptImeiSlot(_diagImeiSlot);
            _diagImeiSlot = slot;
            System.Console.Write("IMEI (15位数字): ");
            string imei = System.Console.ReadLine()?.Trim();
            if (!IsValidImeiInput(imei))
            {
                ConsoleController.Log("IMEI 格式错误，必须为 15 位数字", ConsoleColor.Red);
                return;
            }

            ConsoleController.Log($"即将写入 IMEI{slot}: {SensitiveDataPolicy.DisplayImei(imei)}", ConsoleColor.Yellow);
            System.Console.Write("确认? (y/n): ");
            if (System.Console.ReadLine()?.Trim().ToLower() != "y") return;

            await _controller.WriteDiagImeiAsync(imei, slot);
        }

        static async Task MenuReadAllImei()
        {
            if (!await EnsureDiagConnected()) return;
            await _controller.ReadAllDiagImeiAsync();
        }

        static int PromptImeiSlot(int defaultSlot = 1)
        {
            int safeDefault = ClampImeiSlot(defaultSlot);
            System.Console.Write($"IMEI 槽位 [1-4] [{safeDefault}]: ");
            string input = System.Console.ReadLine()?.Trim();
            if (string.IsNullOrWhiteSpace(input))
                return safeDefault;
            if (int.TryParse(input, out int slot) && slot >= 1 && slot <= 4)
                return slot;
            ConsoleController.Log("槽位无效，已使用默认槽位", ConsoleColor.Yellow);
            return safeDefault;
        }

        static async Task<bool> EnsureDiagConnected()
        {
            if (string.IsNullOrWhiteSpace(_diagPort))
            {
                _diagPort = _selectedPort;
            }

            if (string.IsNullOrWhiteSpace(_diagPort))
            {
                ConsoleController.Log("请先在 Diag 菜单中连接端口", ConsoleColor.Yellow);
                return false;
            }

            if (_controller != null && _controller.IsDiagConnected)
            {
                return true;
            }

            bool connected = await _controller.ConnectDiagAsync(_diagPort, _diagBaudRate);
            if (!connected)
            {
                ConsoleController.Log("Diag 未连接，请检查端口/驱动后重试", ConsoleColor.Red);
                return false;
            }
            return true;
        }

        static bool IsValidImeiInput(string imei)
        {
            return !string.IsNullOrWhiteSpace(imei) && imei.Length == 15 && imei.All(char.IsDigit);
        }

        static int ClampImeiSlot(int slot)
        {
            if (slot < 1) return 1;
            if (slot > 4) return 4;
            return slot;
        }

        #endregion

        #region 菜单: 设置

        static void MenuSettings()
        {
            ConsoleController.Log("", null);
            ConsoleController.Log("当前设置:", ConsoleColor.Cyan);
            ConsoleController.Log($"  [1] 存储类型    : {_storageType.ToUpper()}", ConsoleColor.White);
            ConsoleController.Log($"  [2] 敏感分区保护: {(_controller.ProtectSensitivePartitions ? "开启" : "关闭")}", ConsoleColor.White);
            ConsoleController.Log($"  [3] 跳过 Sahara : {(_controller.SkipSahara ? "是" : "否")}", ConsoleColor.White);
            ConsoleController.Log($"  [4] 详细日志    : {(_verboseLog ? "开启" : "关闭")}", ConsoleColor.White);
            ConsoleController.Log($"  [5] Loader 路径 : {(_programmerPath ?? "未设置")}", ConsoleColor.White);
            if (_controller.IsConnected)
            {
                bool isVip = _controller.Service?.IsVipDevice ?? false;
                ConsoleController.Log($"  [6] 欧加新授权  : {(isVip ? "开启" : "关闭")}", isVip ? ConsoleColor.Green : ConsoleColor.White);
                ConsoleController.Log("  [7] 断开连接", ConsoleColor.Yellow);
            }
            ConsoleController.Log($"  [8] 敏感调试日志: {(_debugSensitiveData ? "开启" : "关闭")}", _debugSensitiveData ? ConsoleColor.Yellow : ConsoleColor.White);
            ConsoleController.Log("  [0] 返回", ConsoleColor.DarkGray);

            string choice = ReadInput("修改项");
            switch (choice)
            {
                case "1":
                    _storageType = _storageType == "ufs" ? "emmc" : "ufs";
                    ConsoleController.Log($"存储类型已切换为: {_storageType.ToUpper()}", ConsoleColor.Green);
                    break;
                case "2":
                    _controller.ProtectSensitivePartitions = !_controller.ProtectSensitivePartitions;
                    ConsoleController.Log($"敏感分区保护: {(_controller.ProtectSensitivePartitions ? "已开启" : "已关闭")}", ConsoleColor.Green);
                    break;
                case "3":
                    _controller.SkipSahara = !_controller.SkipSahara;
                    ConsoleController.Log($"跳过 Sahara: {(_controller.SkipSahara ? "是" : "否")}", ConsoleColor.Green);
                    break;
                case "4":
                    _verboseLog = !_verboseLog;
                    ConsoleController.Log($"详细日志: {(_verboseLog ? "已开启" : "已关闭")}", ConsoleColor.Green);
                    break;
                case "5":
                    System.Console.Write("Loader 文件路径: ");
                    string path = System.Console.ReadLine()?.Trim().Trim('"');
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        _programmerPath = path;
                        ConsoleController.Log($"已设置: {_programmerPath}", ConsoleColor.Green);
                    }
                    else if (!string.IsNullOrEmpty(path))
                    {
                        ConsoleController.Log("文件不存在", ConsoleColor.Red);
                    }
                    break;
                case "6":
                    if (_controller.IsConnected && _controller.Service != null)
                    {
                        _controller.Service.IsVipDevice = !_controller.Service.IsVipDevice;
                        ConsoleController.Log($"欧加新授权签名模式: {(_controller.Service.IsVipDevice ? "已开启" : "已关闭")}", ConsoleColor.Green);
                    }
                    break;
                case "7":
                    if (_controller.IsConnected)
                    {
                        _controller.Disconnect();
                        ConsoleController.Log("已断开连接", ConsoleColor.Green);
                    }
                    break;
                case "8":
                    _debugSensitiveData = !_debugSensitiveData;
                    SensitiveDataPolicy.AllowSensitiveData = _debugSensitiveData;
                    ConsoleController.Log(
                        $"敏感调试日志: {(_debugSensitiveData ? "已开启(显示完整IMEI)" : "已关闭(IMEI脱敏)")}",
                        ConsoleColor.Green);
                    break;
            }
        }

        #endregion

        #region 命令行模式
        static async Task HandleCommandLineArgs(string[] args)
        {
            string command = args[0].ToLowerInvariant();
            bool isDiagCommand =
                command == "diag-connect" ||
                command == "diag-readqcn" ||
                command == "diag-writeqcn" ||
                command == "diag-readimei" ||
                command == "diag-writeimei" ||
                command == "diag-readallimei";
            List<string> positionalArgs = new List<string>();

            // 解析通用参数
            for (int i = 1; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg.ToLowerInvariant())
                {
                    case "--port": case "-p":
                        if (i + 1 < args.Length) _selectedPort = args[++i];
                        break;
                    case "--loader": case "-l":
                        if (i + 1 < args.Length) _programmerPath = args[++i];
                        break;
                    case "--storage": case "-s":
                        if (i + 1 < args.Length) _storageType = args[++i];
                        break;
                    case "--diag-port":
                    case "--dport":
                        if (i + 1 < args.Length) _diagPort = args[++i];
                        break;
                    case "--baud":
                    case "--diag-baud":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out int baud) && baud > 0)
                            _diagBaudRate = baud;
                        break;
                    case "--imei-slot":
                    case "--diag-slot":
                        if (i + 1 < args.Length && int.TryParse(args[++i], out int imeiSlot))
                            _diagImeiSlot = ClampImeiSlot(imeiSlot);
                        break;
                    case "--verbose": case "-v":
                        _verboseLog = true;
                        break;
                    case "--debug":
                    case "--debug-sensitive":
                    case "--debug-imei":
                        _debugSensitiveData = true;
                        SensitiveDataPolicy.AllowSensitiveData = true;
                        break;
                    case "--skip-sahara":
                        _controller.SkipSahara = true;
                        break;
                    case "--no-protect":
                        _controller.ProtectSensitivePartitions = false;
                        break;
                    default:
                        positionalArgs.Add(arg);
                        break;
                }
            }

            // 非 Diag 命令才自动检测 EDL 端口
            if (!isDiagCommand && string.IsNullOrEmpty(_selectedPort))
            {
                var edlPorts = PortDetector.DetectEdlPorts();
                if (edlPorts.Count == 1)
                {
                    _selectedPort = edlPorts[0].PortName;
                    ConsoleController.Log($"自动选择 EDL 端口: {_selectedPort}", ConsoleColor.Green);
                }
                else if (edlPorts.Count == 0)
                {
                    ConsoleController.Log("未检测到 EDL 端口", ConsoleColor.Red);
                    PrintUsage();
                    return;
                }
                else
                {
                    ConsoleController.Log("检测到多个 EDL 端口，请用 --port 指定", ConsoleColor.Red);
                    PrintUsage();
                    return;
                }
            }

            switch (command)
            {
                case "list":
                    _controller.RefreshPorts();
                    break;

                case "connect":
                    await DoConnect();
                    break;

                case "gpt":
                    await DoConnect();
                    if (_controller.IsConnected)
                        await _controller.ReadPartitionTableAsync();
                    break;

                case "storageinfo":
                    await DoConnect();
                    if (_controller.IsConnected)
                        await _controller.ReadStorageInfoAsync();
                    break;

                case "read":
                    if (positionalArgs.Count < 2) { ConsoleController.Log("用法: read <分区名> <输出路径>", ConsoleColor.Red); return; }
                    await DoConnect();
                    if (_controller.IsConnected)
                    {
                        await _controller.ReadPartitionTableAsync();
                        await _controller.ReadPartitionAsync(positionalArgs[0], positionalArgs[1]);
                    }
                    break;

                case "write":
                    if (positionalArgs.Count < 2) { ConsoleController.Log("用法: write <分区名> <文件路径>", ConsoleColor.Red); return; }
                    await DoConnect();
                    if (_controller.IsConnected)
                    {
                        await _controller.ReadPartitionTableAsync();
                        await _controller.WritePartitionAsync(positionalArgs[0], positionalArgs[1]);
                    }
                    break;

                case "flash":
                    if (positionalArgs.Count < 1) { ConsoleController.Log("用法: flash <rawprogram目录>", ConsoleColor.Red); return; }
                    await DoConnect();
                    if (_controller.IsConnected)
                    {
                        await _controller.ReadPartitionTableAsync();
                        string dir = positionalArgs[0];
                        if (Directory.Exists(dir))
                        {
                            var parser = new RawprogramParser(dir, msg => ConsoleController.Log(msg));
                            var pkg = parser.LoadPackage();
                            if (pkg.Tasks.Count > 0)
                            {
                                var tasks = BuildWriteTasksFromPackage(pkg, dir);
                                await _controller.WritePartitionsBatchAsync(tasks, pkg.PatchFiles, _storageType == "ufs");
                            }
                        }
                    }
                    break;

                case "reboot":
                    await DoConnect();
                    if (_controller.IsConnected)
                        await _controller.RebootToSystemAsync();
                    break;

                case "disconnect":
                case "disc":
                    _controller.DisconnectDevice();
                    break;

                case "diag-connect":
                    await DoDiagConnect();
                    break;

                case "diag-readqcn":
                    if (positionalArgs.Count < 1)
                    {
                        ConsoleController.Log("用法: diag-readqcn <输出目录|输出路径.qcn> [--diag-port COMx] [--baud 115200]", ConsoleColor.Red);
                        return;
                    }
                    await DoDiagReadQcn(positionalArgs[0]);
                    break;

                case "diag-writeqcn":
                    if (positionalArgs.Count < 1)
                    {
                        ConsoleController.Log("用法: diag-writeqcn <输入文件.qcn> [--diag-port COMx] [--baud 115200]", ConsoleColor.Red);
                        return;
                    }
                    await DoDiagWriteQcn(positionalArgs[0]);
                    break;

                case "diag-readimei":
                    if (positionalArgs.Count >= 1 && int.TryParse(positionalArgs[0], out int readSlot))
                        _diagImeiSlot = ClampImeiSlot(readSlot);
                    await DoDiagReadImei(_diagImeiSlot);
                    break;

                case "diag-writeimei":
                    if (positionalArgs.Count < 1)
                    {
                        ConsoleController.Log("用法: diag-writeimei <15位IMEI> [slot] [--diag-port COMx] [--imei-slot N]", ConsoleColor.Red);
                        return;
                    }
                    if (positionalArgs.Count >= 2 && int.TryParse(positionalArgs[1], out int writeSlot))
                        _diagImeiSlot = ClampImeiSlot(writeSlot);
                    await DoDiagWriteImei(positionalArgs[0], _diagImeiSlot);
                    break;

                case "diag-readallimei":
                    await DoDiagReadAllImei();
                    break;

                case "help":
                default:
                    PrintUsage();
                    break;
            }

            _controller?.Dispose();
        }

        static async Task DoConnect()
        {
            if (!string.IsNullOrEmpty(_programmerPath) && File.Exists(_programmerPath))
            {
                var mode = await _controller.ProbeDeviceModeAsync(_selectedPort);
                if (mode == EdlDeviceMode.Firehose)
                {
                    ConsoleController.Log("检测到设备已在 Firehose 模式，优先尝试直连...", ConsoleColor.Cyan);
                    _controller.SkipSahara = true;
                    bool directOk = await _controller.ConnectAsync(_selectedPort, "", _storageType, probeVipSpoof: _useVipMode);
                    if (directOk)
                        return;

                    ConsoleController.Log("Firehose 直连失败，回退 Loader 连接...", ConsoleColor.Yellow);
                    _controller.SkipSahara = false;
                }

                byte[] data = File.ReadAllBytes(_programmerPath);
                await _controller.ConnectWithLoaderDataAsync(_selectedPort, data, Path.GetFileName(_programmerPath), _storageType);
            }
            else if (_controller.SkipSahara)
            {
                await _controller.ConnectAsync(_selectedPort, "", _storageType);
            }
            else
            {
                ConsoleController.Log("请指定 --loader 或 --skip-sahara", ConsoleColor.Red);
            }
        }

        static async Task<bool> DoDiagConnect()
        {
            if (string.IsNullOrWhiteSpace(_diagPort))
                _diagPort = _selectedPort;

            if (string.IsNullOrWhiteSpace(_diagPort))
            {
                ConsoleController.Log("请指定 Diag 端口: --diag-port COMx 或 --port COMx", ConsoleColor.Red);
                return false;
            }

            return await _controller.ConnectDiagAsync(_diagPort, _diagBaudRate);
        }

        static async Task DoDiagReadQcn(string outputPath)
        {
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                ConsoleController.Log("输出路径不能为空", ConsoleColor.Red);
                return;
            }

            string resolvedPath;
            try
            {
                resolvedPath = BuildQcnBackupOutputPath(outputPath);
            }
            catch (Exception ex)
            {
                ConsoleController.Log("输出路径无效: " + ex.Message, ConsoleColor.Red);
                return;
            }

            ConsoleController.Log($"QCN 输出文件: {resolvedPath}", ConsoleColor.Cyan);

            if (!await DoDiagConnect())
                return;

            await _controller.ReadQcnAsync(resolvedPath);
        }

        static async Task DoDiagWriteQcn(string inputPath)
        {
            if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            {
                ConsoleController.Log("QCN 文件不存在", ConsoleColor.Red);
                return;
            }

            if (!await DoDiagConnect())
                return;

            await _controller.WriteQcnAsync(inputPath);
        }

        static async Task DoDiagReadImei(int slot)
        {
            int safeSlot = ClampImeiSlot(slot);
            if (!await DoDiagConnect())
                return;

            await _controller.ReadDiagImeiAsync(safeSlot);
        }

        static async Task DoDiagWriteImei(string imei, int slot)
        {
            if (!IsValidImeiInput(imei))
            {
                ConsoleController.Log("IMEI 格式错误，必须为 15 位数字", ConsoleColor.Red);
                return;
            }

            int safeSlot = ClampImeiSlot(slot);
            if (!await DoDiagConnect())
                return;

            await _controller.WriteDiagImeiAsync(imei, safeSlot);
        }

        static async Task DoDiagReadAllImei()
        {
            if (!await DoDiagConnect())
                return;

            await _controller.ReadAllDiagImeiAsync();
        }

        static void PrintUsage()
        {
            ConsoleController.Log("", null);
            ConsoleController.Log("用法: QualcommConsole [命令] [选项]", ConsoleColor.Cyan);
            ConsoleController.Log("", null);
            ConsoleController.Log("命令:", ConsoleColor.White);
            ConsoleController.Log("  list                          列出所有端口", ConsoleColor.DarkGray);
            ConsoleController.Log("  connect                       连接设备", ConsoleColor.DarkGray);
            ConsoleController.Log("  gpt                           读取分区表", ConsoleColor.DarkGray);
            ConsoleController.Log("  storageinfo                   读取 Firehose 存储信息", ConsoleColor.DarkGray);
            ConsoleController.Log("  read <分区> <输出>            读取分区", ConsoleColor.DarkGray);
            ConsoleController.Log("  write <分区> <文件>           写入分区", ConsoleColor.DarkGray);
            ConsoleController.Log("  flash <目录>                  批量刷写 (rawprogram)", ConsoleColor.DarkGray);
            ConsoleController.Log("  reboot                        重启设备", ConsoleColor.DarkGray);
            ConsoleController.Log("  disconnect                    断开连接", ConsoleColor.DarkGray);
            ConsoleController.Log("  diag-connect                  连接 Diag 端口", ConsoleColor.DarkGray);
            ConsoleController.Log("  diag-readqcn <输出目录|输出.qcn>  读取 QCN 备份", ConsoleColor.DarkGray);
            ConsoleController.Log("  diag-writeqcn <输入.qcn>      写入 QCN 恢复", ConsoleColor.DarkGray);
            ConsoleController.Log("  diag-readimei [slot]          读取 IMEI", ConsoleColor.DarkGray);
            ConsoleController.Log("  diag-writeimei <imei> [slot]  写入 IMEI", ConsoleColor.DarkGray);
            ConsoleController.Log("  diag-readallimei              读取所有 IMEI", ConsoleColor.DarkGray);
            ConsoleController.Log("", null);
            ConsoleController.Log("选项:", ConsoleColor.White);
            ConsoleController.Log("  --port, -p <端口>             指定 COM 端口", ConsoleColor.DarkGray);
            ConsoleController.Log("  --diag-port, --dport <端口>   指定 Diag COM 端口", ConsoleColor.DarkGray);
            ConsoleController.Log("  --baud, --diag-baud <波特率>  指定 Diag 波特率 (默认 115200)", ConsoleColor.DarkGray);
            ConsoleController.Log("  --imei-slot, --diag-slot <N>  指定 IMEI 槽位 (1-4)", ConsoleColor.DarkGray);
            ConsoleController.Log("  --loader, -l <路径>           指定 Loader 文件", ConsoleColor.DarkGray);
            ConsoleController.Log("  --storage, -s <ufs|emmc>      存储类型 (默认 ufs)", ConsoleColor.DarkGray);
            ConsoleController.Log("  --skip-sahara                 跳过 Sahara 直连", ConsoleColor.DarkGray);
            ConsoleController.Log("  --no-protect                  关闭敏感分区保护", ConsoleColor.DarkGray);
            ConsoleController.Log("  --verbose, -v                 详细日志", ConsoleColor.DarkGray);
            ConsoleController.Log("  --debug-sensitive, --debug    显示完整 IMEI (默认脱敏)", ConsoleColor.DarkGray);
            ConsoleController.Log("", null);
            ConsoleController.Log("示例:", ConsoleColor.White);
            ConsoleController.Log("  QualcommConsole list", ConsoleColor.DarkGray);
            ConsoleController.Log("  QualcommConsole gpt -l loader.mbn", ConsoleColor.DarkGray);
            ConsoleController.Log("  QualcommConsole storageinfo -l loader.mbn", ConsoleColor.DarkGray);
            ConsoleController.Log("  QualcommConsole read boot_a boot_a.img -l loader.mbn", ConsoleColor.DarkGray);
            ConsoleController.Log("  QualcommConsole flash ./firmware -l loader.mbn", ConsoleColor.DarkGray);
            ConsoleController.Log("  QualcommConsole connect --skip-sahara -p COM8", ConsoleColor.DarkGray);
            ConsoleController.Log("  QualcommConsole diag-connect --diag-port COM7 --baud 115200", ConsoleColor.DarkGray);
            ConsoleController.Log("  QualcommConsole diag-readqcn D:\\Backup\\QCN --diag-port COM7", ConsoleColor.DarkGray);
            ConsoleController.Log("  QualcommConsole diag-writeqcn backup.qcn --diag-port COM7", ConsoleColor.DarkGray);
            ConsoleController.Log("  QualcommConsole diag-readimei 1 --diag-port COM7", ConsoleColor.DarkGray);
            ConsoleController.Log("  QualcommConsole diag-writeimei 123456789012345 1 --diag-port COM7 --debug-sensitive", ConsoleColor.DarkGray);
            ConsoleController.Log("", null);
            ConsoleController.Log("无参数运行进入交互模式。", ConsoleColor.White);
        }

        #endregion

        #region 辅助方法

        static bool IsSensitiveDebugFlag(string arg)
        {
            if (string.IsNullOrWhiteSpace(arg))
                return false;

            return string.Equals(arg, "--debug-sensitive", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(arg, "--debug", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(arg, "--debug-imei", StringComparison.OrdinalIgnoreCase);
        }

        static string ReadInput(string prompt)
        {
            System.Console.Write($"{prompt} > ");
            return System.Console.ReadLine()?.Trim();
        }

        static string BuildQcnBackupOutputPath(string outputPathOrDirectory)
        {
            if (string.IsNullOrWhiteSpace(outputPathOrDirectory))
                throw new ArgumentException("输出路径为空");

            string input = outputPathOrDirectory.Trim().Trim('"');
            bool isQcnFile = string.Equals(Path.GetExtension(input), ".qcn", StringComparison.OrdinalIgnoreCase);

            string fileName = $"QCN_{DateTime.Now:yyyy_MM_dd_HHmmss}.qcn";
            string targetPath = isQcnFile ? input : Path.Combine(input, fileName);

            string dir = Path.GetDirectoryName(targetPath);
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = Directory.GetCurrentDirectory();
                targetPath = Path.Combine(dir, Path.GetFileName(targetPath));
            }

            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            return Path.GetFullPath(targetPath);
        }

        static bool EnsureConnected()
        {
            if (!_controller.IsConnected)
            {
                ConsoleController.Log("设备未连接，请先连接设备", ConsoleColor.Red);
                return false;
            }
            return true;
        }

        static bool EnsurePartitions()
        {
            if (!_controller.HasPartitions)
            {
                ConsoleController.Log("未读取分区表，请先读取分区表", ConsoleColor.Red);
                return false;
            }
            return true;
        }

        static List<Tuple<string, string, int, long>> BuildWriteTasksFromPackage(FlashPackageInfo packageInfo, string baseDir)
        {
            var tasks = new List<Tuple<string, string, int, long>>();
            foreach (var t in packageInfo.Tasks)
            {
                if (t.Type == TaskType.Program && !string.IsNullOrEmpty(t.Filename))
                {
                    string fp = !string.IsNullOrEmpty(t.FilePath) ? t.FilePath : Path.Combine(baseDir, t.Filename);
                    if (File.Exists(fp))
                        tasks.Add(Tuple.Create(t.Label, fp, t.Lun, t.StartSector));
                }
            }
            return tasks;
        }

        static PartitionInfo ResolvePartition(string input)
        {
            // 尝试按序号
            if (int.TryParse(input, out int idx) && idx >= 1 && idx <= _controller.Partitions.Count)
            {
                return _controller.Partitions[idx - 1];
            }

            // 按名称查找
            var p = _controller.FindPartition(input);
            if (p == null)
            {
                ConsoleController.Log($"未找到分区: {input}", ConsoleColor.Red);
            }
            return p;
        }

        static List<PartitionInfo> ResolvePartitions(string input)
        {
            var result = new List<PartitionInfo>();
            if (string.IsNullOrWhiteSpace(input))
                return result;

            var tokens = input
                .Split(new[] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t));

            foreach (var token in tokens)
            {
                PartitionInfo partition = ResolvePartition(token);
                if (partition == null)
                    continue;

                bool exists = result.Any(p => p.Name.Equals(partition.Name, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                    result.Add(partition);
            }

            return result;
        }

        static long GetPartitionSizeBytes(PartitionInfo partition)
        {
            if (partition == null)
                return 0;

            if (partition.Size > 0)
                return partition.Size;

            long sectorSize = partition.SectorSize > 0 ? partition.SectorSize : 512;
            return partition.NumSectors > 0 ? partition.NumSectors * sectorSize : 0;
        }

        static List<string> GenerateRawprogramXmlByLun(string outputFolder, IEnumerable<PartitionInfo> partitions)
        {
            var partitionList = partitions?
                .Where(p => p != null)
                .OrderBy(p => p.Lun)
                .ThenBy(p => p.StartSector)
                .ToList() ?? new List<PartitionInfo>();

            var generatedPaths = new List<string>();
            if (partitionList.Count == 0)
            {
                return generatedPaths;
            }

            var lunGroups = partitionList
                .GroupBy(p => p.Lun)
                .OrderBy(g => g.Key);

            foreach (var lunGroup in lunGroups)
            {
                int commonSectorSize = lunGroup
                    .Select(p => p.SectorSize > 0 ? p.SectorSize : 4096)
                    .GroupBy(x => x)
                    .OrderByDescending(g => g.Count())
                    .ThenByDescending(g => g.Key)
                    .Select(g => g.Key)
                    .FirstOrDefault();

                var root = new XElement("data",
                    new XComment("NOTE: This is an ** Autogenerated file **"),
                    new XComment($"NOTE: Sector size is {commonSectorSize}bytes"));

                foreach (var partition in lunGroup.OrderBy(p => p.StartSector))
                {
                    long sectorSize = partition.SectorSize > 0 ? partition.SectorSize : commonSectorSize;
                    long numSectors = partition.NumSectors > 0
                        ? partition.NumSectors
                        : (partition.Size > 0 ? partition.Size / sectorSize : 0);
                    long sizeBytes = numSectors * sectorSize;
                    double sizeKb = sizeBytes / 1024.0;
                    long startByte = partition.StartSector * sectorSize;

                    var program = new XElement("program",
                        new XAttribute("SECTOR_SIZE_IN_BYTES", sectorSize.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("file_sector_offset", "0"),
                        new XAttribute("filename", partition.Name + ".img"),
                        new XAttribute("label", partition.Name),
                        new XAttribute("num_partition_sectors", numSectors.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("partofsingleimage", "false"),
                        new XAttribute("physical_partition_number", partition.Lun.ToString(CultureInfo.InvariantCulture)),
                        new XAttribute("readbackverify", "false"),
                        new XAttribute("size_in_KB", sizeKb.ToString("F1", CultureInfo.InvariantCulture)),
                        new XAttribute("sparse", "false"),
                        new XAttribute("start_byte_hex", "0x" + startByte.ToString("x", CultureInfo.InvariantCulture)),
                        new XAttribute("start_sector", partition.StartSector.ToString(CultureInfo.InvariantCulture)));

                    root.Add(program);
                }

                string rawprogramPath = Path.Combine(outputFolder, $"rawprogram{lunGroup.Key}.xml");
                var doc = new XDocument(new XDeclaration("1.0", null, null), root);
                doc.Save(rawprogramPath);
                generatedPaths.Add(rawprogramPath);
            }

            return generatedPaths;
        }

        #endregion
    }
}



