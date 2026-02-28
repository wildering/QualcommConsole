using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using WackeEdl.Qualcomm.Common;
using WackeEdl.Qualcomm.Database;
using WackeEdl.Qualcomm.Models;
using WackeEdl.Qualcomm.Protocol;
using WackeEdl.Qualcomm.Services;

namespace WackeEdl.Qualcomm.Console;

public class ConsoleController : IDisposable
{
	private QualcommService _service;

	private CancellationTokenSource _cts;

	private bool _disposed;

	private DeviceInfoService _deviceInfoService;

	private DeviceFullInfo _currentDeviceInfo;

	private Stopwatch _operationStopwatch;

	private string _currentOperationName;

	private int _lastProgressTenths = -1;

	private DateTime _lastProgressRenderTime = DateTime.MinValue;

	private static readonly TimeSpan ProgressRenderInterval = TimeSpan.FromMilliseconds(120L, 0L);

	private static readonly object ConsoleRenderLock = new object();

	private static bool _progressLineActive;

	private static readonly bool UseUnicodeProgressChars = DetectUnicodeProgressChars();

	private static readonly TimeSpan ProgressSpeedSampleInterval = TimeSpan.FromMilliseconds(280.0);

	private long _operationTotalBytes;

	private bool _progressTelemetryEnabled;

	private bool _progressShowRateEta = true;

	private Func<long> _progressBytesProvider;

	private DateTime _progressSpeedLastSampleTime = DateTime.MinValue;

	private double _progressSpeedLastPercent = -1.0;

	private long _progressSpeedLastBytes = -1L;

	private double _progressSmoothedBytesPerSec;

	private readonly Action<string> _logDetail;

	private byte[] _cachedProbeSaharaHello;

	private string _cachedProbeSaharaHelloPort;

	public bool IsBusy { get; private set; }

	public bool IsConnected
	{
		get
		{
			if (_service != null)
			{
				return _service.IsConnectedFast;
			}
			return false;
		}
	}

	public bool CanQuickReconnect
	{
		get
		{
			if (_service != null)
			{
				return _service.IsPortReleased;
			}
			return false;
		}
	}

	public List<PartitionInfo> Partitions { get; set; } = new List<PartitionInfo>();

	public bool HasPartitions
	{
		get
		{
			if (Partitions != null)
			{
				return Partitions.Count > 0;
			}
			return false;
		}
	}

	public bool ProtectSensitivePartitions { get; set; } = true;

	public bool SkipSahara { get; set; }

	public QualcommChipInfo ChipInfo => _service?.ChipInfo;

	public DeviceFullInfo CurrentDeviceInfo => _currentDeviceInfo;

	public QualcommService Service => _service;

	private CancellationToken Token => _cts?.Token ?? CancellationToken.None;

	public event EventHandler<bool> ConnectionStateChanged;

	public event EventHandler<List<PartitionInfo>> PartitionsLoaded;

	public ConsoleController(Action<string> logDetail = null)
	{
		_logDetail = logDetail ?? ((Action<string>)delegate
		{
		});
	}

	public static void Log(string message, ConsoleColor? color = null)
	{
		string value = message;
		if (!string.IsNullOrEmpty(message) && !message.StartsWith("╔") && !message.StartsWith("╚") && !message.StartsWith("║"))
		{
			value = $"[{DateTime.Now:HH:mm:ss}] {message}";
		}
		lock (ConsoleRenderLock)
		{
			if (_progressLineActive)
			{
				System.Console.WriteLine();
				_progressLineActive = false;
			}
			if (color.HasValue)
			{
				ConsoleColor foregroundColor = System.Console.ForegroundColor;
				System.Console.ForegroundColor = color.Value;
				System.Console.WriteLine(value);
				System.Console.ForegroundColor = foregroundColor;
			}
			else
			{
				System.Console.WriteLine(value);
			}
		}
	}

	public bool IsDiagConnected
	{
		get
		{
			if (_service != null)
			{
				return _service.IsDiagConnected;
			}
			return false;
		}
	}

	public List<DetectedPort> RefreshPorts(bool silent = false)
	{
		try
		{
			List<DetectedPort> list = PortDetector.DetectAllPorts();
			List<DetectedPort> list2 = PortDetector.DetectEdlPorts();
			if (!silent)
			{
				if (list.Count == 0)
				{
					Log("未检测到任何设备端口", ConsoleColor.Yellow);
				}
				else
				{
					Log($"检测到 {list.Count} 个端口 (EDL: {list2.Count}):", ConsoleColor.Cyan);
					for (int i = 0; i < list.Count; i++)
					{
						DetectedPort detectedPort = list[i];
						string value = (detectedPort.IsEdl ? " [EDL]" : "");
						ConsoleColor value2 = (detectedPort.IsEdl ? ConsoleColor.Green : ConsoleColor.White);
						Log($"  [{i + 1}] {detectedPort.PortName} - {detectedPort.Description}{value}", value2);
					}
				}
			}
			return list;
		}
		catch (Exception ex)
		{
			if (!silent)
			{
				Log("刷新端口失败: " + ex.Message, ConsoleColor.Red);
			}
			return new List<DetectedPort>();
		}
	}

	public string SelectPort()
	{
		List<DetectedPort> list = RefreshPorts();
		if (list.Count == 0)
		{
			return null;
		}
		List<DetectedPort> list2 = list.Where((DetectedPort p) => p.IsEdl).ToList();
		if (list2.Count == 1)
		{
			Log("自动选择 EDL 端口: " + list2[0].PortName, ConsoleColor.Green);
			return list2[0].PortName;
		}
		System.Console.Write("请输入端口编号: ");
		if (int.TryParse(System.Console.ReadLine()?.Trim(), out var result) && result >= 1 && result <= list.Count)
		{
			return list[result - 1].PortName;
		}
		Log("无效选择", ConsoleColor.Red);
		return null;
	}

	public async Task<EdlDeviceMode> ProbeDeviceModeAsync(string portName)
	{
		if (string.IsNullOrWhiteSpace(portName))
		{
			return EdlDeviceMode.Unknown;
		}
		QualcommService qualcommService = null;
		try
		{
			QualcommService qualcommService2 = _service;
			if (qualcommService2 == null)
			{
				qualcommService = CreateService();
				qualcommService2 = qualcommService;
			}
			EdlDeviceMode edlDeviceMode = await qualcommService2.ProbeDeviceModeAsync(portName, CancellationToken.None);
			CacheProbeHelloFromService(portName, qualcommService2, edlDeviceMode);
			string text = edlDeviceMode switch
			{
				EdlDeviceMode.Firehose => "Firehose",
				EdlDeviceMode.Sahara => "Sahara",
				_ => "未知"
			};
			Log("端口模式探测: " + text, edlDeviceMode == EdlDeviceMode.Unknown ? ConsoleColor.Yellow : ConsoleColor.Cyan);
			return edlDeviceMode;
		}
		catch (Exception ex)
		{
			ClearCachedProbeHello();
			Log("端口模式探测失败: " + ex.Message, ConsoleColor.Yellow);
			return EdlDeviceMode.Unknown;
		}
		finally
		{
			qualcommService?.Dispose();
		}
	}

	public async Task<bool> ConnectAsync(string portName, string programmerPath, string storageType = "ufs", string authMode = "none", bool probeVipSpoof = true)
	{
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			return false;
		}
		if (string.IsNullOrEmpty(portName))
		{
			Log("未指定端口", ConsoleColor.Red);
			return false;
		}
		if (!SkipSahara && string.IsNullOrEmpty(programmerPath))
		{
			Log("请指定引导文件 (Loader) 路径", ConsoleColor.Red);
			return false;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			StartOperation("连接设备");
			RecreateService();
			if (!SkipSahara)
			{
				ApplyCachedProbeHelloToCurrentService(portName);
			}
			bool flag;
			if (SkipSahara)
			{
				Log("跳过 Sahara，直接连接 Firehose...", ConsoleColor.Cyan);
				flag = await _service.ConnectFirehoseDirectAsync(portName, storageType, _cts.Token, probeVipSpoof);
			}
			else
			{
				Log($"连接设备 (存储: {storageType}, 认证: {authMode})...", ConsoleColor.Cyan);
				flag = await _service.ConnectAsync(portName, programmerPath, storageType, authMode, "", "", _cts.Token);
				if (flag)
				{
					SkipSahara = true;
				}
			}
			if (flag)
			{
				Log("连接成功！", ConsoleColor.Green);
				PrintDeviceInfo();
				this.ConnectionStateChanged?.Invoke(this, e: true);
			}
			else
			{
				Log("连接失败", ConsoleColor.Red);
			}
			return flag;
		}
		catch (Exception ex)
		{
			Log("连接异常: " + ex.Message, ConsoleColor.Red);
			return false;
		}
		finally
		{
			IsBusy = false;
		}
	}

	public async Task<bool> ConnectWithLoaderDataAsync(string portName, byte[] loaderData, string loaderName, string storageType = "ufs", string authMode = "none")
	{
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			return false;
		}
		if (string.IsNullOrEmpty(portName))
		{
			Log("未指定端口", ConsoleColor.Red);
			return false;
		}
		if (loaderData == null || loaderData.Length < 100)
		{
			Log("Loader 数据无效", ConsoleColor.Red);
			return false;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			StartOperation("EDL 连接");
			Log($"[EDL] Loader: {loaderName} ({loaderData.Length / 1024} KB)", ConsoleColor.Cyan);
			RecreateService();
			ApplyCachedProbeHelloToCurrentService(portName);
			if (!(await _service.ConnectWithLoaderDataAsync(portName, loaderData, storageType, _cts.Token)))
			{
				Log("[EDL] Loader 上传失败", ConsoleColor.Red);
				return false;
			}
			Log("[EDL] Loader 上传成功", ConsoleColor.Green);
			string authModeLower = (authMode ?? "none").ToLowerInvariant();
			switch (authModeLower)
			{
			case "oneplus":
			case "oplus_old":
			{
				Log("[EDL] 执行 Oplus 旧签名认证 (OLD - demacia/setprojmodel)...", ConsoleColor.Cyan);
				bool flag2 = await _service.PerformOnePlusAuthAsync(_cts.Token);
				Log(flag2 ? "[EDL] Oplus 旧签名认证成功" : "[EDL] Oplus 旧签名认证失败", flag2 ? ConsoleColor.Green : ConsoleColor.Yellow);
				if (!flag2)
				{
					Log("[EDL] 认证失败，按严格模式中止连接", ConsoleColor.Red);
					ResetServiceAfterAuthFailure();
					return false;
				}
				break;
			}
			case "xiaomi":
			{
				Log("[EDL] 执行小米认证 (自动: Bypass -> MiAuth)...", ConsoleColor.Cyan);
				bool flag3 = await _service.PerformXiaomiAuthAsync(_cts.Token);
				Log(flag3 ? "[EDL] 小米认证成功" : "[EDL] 小米认证失败", flag3 ? ConsoleColor.Green : ConsoleColor.Yellow);
				if (!flag3)
				{
					Log("[EDL] 认证失败，按严格模式中止连接", ConsoleColor.Red);
					ResetServiceAfterAuthFailure();
					return false;
				}
				break;
			}
			case "mi_bypass":
			{
				Log("[EDL] 执行小米内置签名绕过 (MiBypass)...", ConsoleColor.Cyan);
				bool flag4 = await _service.PerformMiBypassAuthAsync(_cts.Token);
				Log(flag4 ? "[EDL] MiBypass 绕过成功" : "[EDL] MiBypass 绕过失败", flag4 ? ConsoleColor.Green : ConsoleColor.Yellow);
				if (!flag4)
				{
					Log("[EDL] 认证失败，按严格模式中止连接", ConsoleColor.Red);
					ResetServiceAfterAuthFailure();
					return false;
				}
				break;
			}
			case "mi_auth":
			{
				Log("[EDL] 执行小米 MiAuth (读取blob)...", ConsoleColor.Cyan);
				await _service.PerformMiAuthAsync(_cts.Token);
				Log("[EDL] MiAuth blob 已读取，等待用户提供签名", ConsoleColor.Yellow);
				break;
			}
			case "none":
				Log("[EDL] 已选择无认证模式，跳过所有认证流程", ConsoleColor.DarkGray);
				break;
			default:
				Log("[EDL] 未知认证模式: " + authMode, ConsoleColor.Red);
				ResetServiceAfterAuthFailure();
				return false;
			}
			Log("[EDL] 连接成功！", ConsoleColor.Green);
			PrintDeviceInfo();
			SkipSahara = true;
			this.ConnectionStateChanged?.Invoke(this, e: true);
			return true;
		}
		catch (Exception ex)
		{
			Log("[EDL] 连接异常: " + ex.Message, ConsoleColor.Red);
			return false;
		}
		finally
		{
			IsBusy = false;
		}
	}

	private void ResetServiceAfterAuthFailure()
	{
		try
		{
			_service?.Disconnect();
		}
		catch
		{
		}
		try
		{
			_service?.Dispose();
		}
		catch
		{
		}
		_service = null;
		Partitions?.Clear();
		_currentDeviceInfo = null;
		SkipSahara = false;
		this.ConnectionStateChanged?.Invoke(this, e: false);
	}

	private void RecreateService()
	{
		if (_service != null)
		{
			try
			{
				_service.DisconnectDiag();
			}
			catch
			{
			}
			try
			{
				_service.Disconnect();
			}
			catch
			{
			}
			try
			{
				_service.Dispose();
			}
			catch
			{
			}
			_service = null;
		}

		_service = CreateService();
	}

	private void CacheProbeHelloFromService(string portName, QualcommService service, EdlDeviceMode mode)
	{
		if (service == null || mode != EdlDeviceMode.Sahara)
		{
			ClearCachedProbeHello();
			return;
		}

		byte[] pendingHello = service.ExportPendingSaharaHello();
		if (pendingHello == null || pendingHello.Length < 8)
		{
			ClearCachedProbeHello();
			_logDetail("[ProbeMode] probe result is Sahara, but no reusable hello packet.");
			return;
		}

		_cachedProbeSaharaHello = pendingHello;
		_cachedProbeSaharaHelloPort = portName;
		_logDetail(string.Format("[ProbeMode] controller cached Sahara hello ({0} bytes) for {1}.", pendingHello.Length, portName));
	}

	private void ApplyCachedProbeHelloToCurrentService(string portName)
	{
		if (_service == null)
		{
			return;
		}
		if (_cachedProbeSaharaHello == null || _cachedProbeSaharaHello.Length < 8 || string.IsNullOrWhiteSpace(_cachedProbeSaharaHelloPort))
		{
			return;
		}
		if (!string.Equals(_cachedProbeSaharaHelloPort, portName, StringComparison.OrdinalIgnoreCase))
		{
			_logDetail(string.Format("[ProbeMode] cached hello ignored (probePort={0}, connectPort={1}).", _cachedProbeSaharaHelloPort, portName));
			return;
		}

		_service.ImportPendingSaharaHello(_cachedProbeSaharaHello);
		_logDetail(string.Format("[ProbeMode] controller replayed Sahara hello ({0} bytes) for {1}.", _cachedProbeSaharaHello.Length, portName));
	}

	private void ClearCachedProbeHello()
	{
		_cachedProbeSaharaHello = null;
		_cachedProbeSaharaHelloPort = null;
	}

	public async Task<bool> ConnectWithNewOplusAsync(string portName, string storageType, byte[] loaderData, string digestPath, string signaturePath)
	{
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			return false;
		}
		if (string.IsNullOrEmpty(portName))
		{
			Log("未指定端口", ConsoleColor.Red);
			return false;
		}
		if (loaderData == null)
		{
			Log("Loader 数据无效", ConsoleColor.Red);
			return false;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			StartOperation("NEW-Oplus VIP 认证连接");
			RecreateService();
			ApplyCachedProbeHelloToCurrentService(portName);
			bool num = await _service.ConnectWithNewOplusAuthAsync(portName, loaderData, digestPath, signaturePath, storageType, _cts.Token);
			if (num)
			{
				Log("[Oplus] 连接成功！VIP 高权限模式已激活", ConsoleColor.Green);
				PrintDeviceInfo();
				SkipSahara = true;
				this.ConnectionStateChanged?.Invoke(this, e: true);
			}
			else
			{
				Log("[Oplus] 连接失败", ConsoleColor.Red);
			}
			return num;
		}
		catch (Exception ex)
		{
			Log("[Oplus] 连接异常: " + ex.Message, ConsoleColor.Red);
			return false;
		}
		finally
		{
			IsBusy = false;
		}
	}

	public async Task<bool> ConnectWithVipDataAsync(string portName, string storageType, byte[] loaderData, string digestPath, string signaturePath)
	{
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			return false;
		}
		if (string.IsNullOrEmpty(portName))
		{
			Log("未指定端口", ConsoleColor.Red);
			return false;
		}
		if (loaderData == null)
		{
			Log("Loader 数据无效", ConsoleColor.Red);
			return false;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			StartOperation("O+认证连接");
			RecreateService();
			ApplyCachedProbeHelloToCurrentService(portName);
			bool num = await _service.ConnectWithOldOplusAuthAsync(portName, loaderData, storageType, _cts.Token);
			if (num)
			{
				Log("[O+] 连接成功！高权限模式已激活", ConsoleColor.Green);
				PrintDeviceInfo();
				SkipSahara = true;
				this.ConnectionStateChanged?.Invoke(this, e: true);
			}
			else
			{
				Log("[O+] 连接失败", ConsoleColor.Red);
			}
			return num;
		}
		catch (Exception ex)
		{
			Log("[O+] 连接异常: " + ex.Message, ConsoleColor.Red);
			return false;
		}
		finally
		{
			IsBusy = false;
		}
	}

	public void Disconnect()
	{
		if (_service != null)
		{
			_service.Disconnect();
			_service.Dispose();
			_service = null;
		}
		CancelOperation();
		Partitions?.Clear();
		_currentDeviceInfo = null;
		ClearCachedProbeHello();
		this.ConnectionStateChanged?.Invoke(this, e: false);
		Log("已断开连接", ConsoleColor.Gray);
	}

	public void PrintDeviceInfo()
	{
		if (_service == null)
		{
			return;
		}
		if (_deviceInfoService == null)
		{
			_deviceInfoService = new DeviceInfoService(delegate(string msg)
			{
				Log(msg);
			}, _logDetail);
		}
		_currentDeviceInfo = _deviceInfoService.GetInfoFromQualcommService(_service);
		QualcommChipInfo chipInfo = _service.ChipInfo;
		Log("================================================", ConsoleColor.DarkGray);
		Log("设备信息", ConsoleColor.Cyan);
		Log("================================================", ConsoleColor.DarkGray);
		if (chipInfo != null)
		{
			string vendorByPkHash = QualcommDatabase.GetVendorByPkHash(chipInfo.PkHash);
			string vendorByOem = ((chipInfo.OemId > 0) ? QualcommDatabase.GetVendorName(chipInfo.OemId) : "Unknown");
			if (!QualcommDatabase.IsUnknownVendor(vendorByPkHash) && !QualcommDatabase.IsUnknownVendor(vendorByOem) && NormalizeVendorName(vendorByPkHash) != NormalizeVendorName(vendorByOem))
			{
				_logDetail($"[Vendor] 厂商判定冲突: OEM={vendorByOem}, PKHash={vendorByPkHash}, 采用 PKHash");
			}
			string text = QualcommDatabase.ResolveVendor(chipInfo.OemId, chipInfo.PkHash);
			if (QualcommDatabase.IsUnknownVendor(text) && _currentDeviceInfo != null && !QualcommDatabase.IsUnknownVendor(_currentDeviceInfo.Vendor))
			{
				text = _currentDeviceInfo.Vendor;
			}
			string text2 = QualcommDatabase.GetChipCodename(chipInfo.MsmId);
			if (string.IsNullOrEmpty(text2))
			{
				text2 = ((!string.IsNullOrEmpty(chipInfo.ChipName) && chipInfo.ChipName != "Unknown") ? chipInfo.ChipName : $"0x{chipInfo.MsmId:X8}");
			}
			Log("  品牌      : " + text, ConsoleColor.White);
			Log("  芯片      : " + text2, ConsoleColor.White);
			Log($"  MSM ID    : 0x{chipInfo.MsmId:X8}", ConsoleColor.White);
			if (chipInfo.OemId > 0)
				Log($"  OEM ID    : 0x{chipInfo.OemId:X4}", ConsoleColor.White);
			if (chipInfo.ModelId > 0)
				Log($"  Model ID  : 0x{chipInfo.ModelId:X4}", ConsoleColor.White);
			if (!string.IsNullOrEmpty(chipInfo.HwIdHex))
				Log("  HW ID     : " + chipInfo.HwIdHex, ConsoleColor.White);
			Log("  序列号    : " + (string.IsNullOrEmpty(chipInfo.SerialHex) ? "未获取" : chipInfo.SerialHex), ConsoleColor.White);
			if (chipInfo.SerialDec > 0)
				Log($"  序列号(十): {chipInfo.SerialDec}", ConsoleColor.DarkGray);
			if (!string.IsNullOrEmpty(chipInfo.PkHash))
			{
				string pkDisplay = chipInfo.PkHash.Length > 16 ? chipInfo.PkHash.Substring(0, 16) + "..." : chipInfo.PkHash;
				Log("  PK Hash   : " + pkDisplay, ConsoleColor.DarkGray);
				if (!string.IsNullOrEmpty(chipInfo.PkHashInfo))
					Log("  PK 信息   : " + chipInfo.PkHashInfo, ConsoleColor.DarkGray);
			}
		}

		// ── 存储 & 连接 ──
		Log("------------------------------------------------", ConsoleColor.DarkGray);
		string text3 = _service.StorageType ?? "UFS";
		Log($"  存储类型  : {text3.ToUpper()}", ConsoleColor.White);
		Log($"  扇区大小  : {_service.SectorSize} B", ConsoleColor.White);
		string slot = _service.CurrentSlot;
		if (!string.IsNullOrEmpty(slot) && slot != "nonexistent")
			Log("  当前槽位  : " + slot, ConsoleColor.White);
		if (!string.IsNullOrEmpty(_service.LastPortName))
			Log("  端口      : " + _service.LastPortName, ConsoleColor.DarkGray);
		if (_service.SaharaProtocolVersion > 0)
			Log($"  Sahara版本: {_service.SaharaProtocolVersion}", ConsoleColor.DarkGray);

		// ── VIP / 认证状态 ──
		if (_service.IsVipDevice)
			Log("  VIP 模式  : 已激活", ConsoleColor.Green);

		// ── 已读取的 build.prop 信息 ──
		if (_currentDeviceInfo != null)
		{
			bool hasExtra = !string.IsNullOrEmpty(_currentDeviceInfo.MarketName)
				|| !string.IsNullOrEmpty(_currentDeviceInfo.Model)
				|| !string.IsNullOrEmpty(_currentDeviceInfo.AndroidVersion);
			if (hasExtra)
			{
				Log("------------------------------------------------", ConsoleColor.DarkGray);
				if (!string.IsNullOrEmpty(_currentDeviceInfo.MarketName))
					Log("  市场名称  : " + _currentDeviceInfo.MarketName, ConsoleColor.Cyan);
				if (!string.IsNullOrEmpty(_currentDeviceInfo.Model))
					Log("  设备型号  : " + _currentDeviceInfo.Model, ConsoleColor.White);
				if (!string.IsNullOrEmpty(_currentDeviceInfo.Brand) && _currentDeviceInfo.Brand != "oplus")
					Log("  设备品牌  : " + _currentDeviceInfo.Brand, ConsoleColor.White);
				if (!string.IsNullOrEmpty(_currentDeviceInfo.DeviceCodename))
					Log("  设备代号  : " + _currentDeviceInfo.DeviceCodename, ConsoleColor.White);
				if (!string.IsNullOrEmpty(_currentDeviceInfo.AndroidVersion))
				{
					string androidDisplay = _currentDeviceInfo.AndroidVersion;
					if (!string.IsNullOrEmpty(_currentDeviceInfo.SdkVersion))
						androidDisplay += " [SDK:" + _currentDeviceInfo.SdkVersion + "]";
					Log("  安卓版本  : " + androidDisplay, ConsoleColor.White);
				}
				if (!string.IsNullOrEmpty(_currentDeviceInfo.SecurityPatch))
					Log("  安全补丁  : " + _currentDeviceInfo.SecurityPatch, ConsoleColor.White);
				if (!string.IsNullOrEmpty(_currentDeviceInfo.BuildId))
					Log("  构建 ID   : " + _currentDeviceInfo.BuildId, ConsoleColor.DarkGray);
				if (!string.IsNullOrEmpty(_currentDeviceInfo.OtaVersion))
					Log("  OTA 版本  : " + _currentDeviceInfo.OtaVersion, ConsoleColor.Green);
				if (!string.IsNullOrEmpty(_currentDeviceInfo.Fingerprint))
					Log("  构建指纹  : " + _currentDeviceInfo.Fingerprint, ConsoleColor.DarkGray);
				if (!string.IsNullOrEmpty(_currentDeviceInfo.Region))
					Log("  区域代码  : " + _currentDeviceInfo.Region, ConsoleColor.White);
				if (!string.IsNullOrEmpty(_currentDeviceInfo.BuiltDate))
					Log("  编译日期  : " + _currentDeviceInfo.BuiltDate, ConsoleColor.DarkGray);
				if (!string.IsNullOrEmpty(_currentDeviceInfo.OplusProject))
					Log("  OPLUS项目 : " + _currentDeviceInfo.OplusProject, ConsoleColor.White);
				if (!string.IsNullOrEmpty(_currentDeviceInfo.OplusNvId))
					Log("  NV ID     : " + _currentDeviceInfo.OplusNvId, ConsoleColor.White);
				if (!string.IsNullOrEmpty(_currentDeviceInfo.LenovoSeries))
					Log("  联想系列  : " + _currentDeviceInfo.LenovoSeries, ConsoleColor.White);
				if (!string.IsNullOrEmpty(_currentDeviceInfo.HardwareSn))
					Log("  硬件序列号: " + _currentDeviceInfo.HardwareSn, ConsoleColor.White);
			}
			if (_currentDeviceInfo.IsAbDevice)
				Log("  A/B 分区  : 是", ConsoleColor.DarkGray);
		}

		Log("================================================", ConsoleColor.DarkGray);
	}

	public void UpdateDeviceInfoFromPartitions()
	{
		if (_service == null || Partitions == null || Partitions.Count == 0)
		{
			return;
		}
		if (_currentDeviceInfo == null)
		{
			_currentDeviceInfo = new DeviceFullInfo();
		}
		bool isAbDevice = Partitions.Exists((PartitionInfo p) => p.Name.EndsWith("_a") || p.Name.EndsWith("_b"));
		_currentDeviceInfo.IsAbDevice = isAbDevice;
		if (string.IsNullOrEmpty(_currentDeviceInfo.Brand) || _currentDeviceInfo.Brand == "Unknown")
		{
			if (Partitions.Exists((PartitionInfo p) => p.Name.StartsWith("my_") || p.Name.Contains("oplus")))
			{
				_currentDeviceInfo.Brand = "OPPO/Realme";
			}
			else if (Partitions.Exists((PartitionInfo p) => p.Name == "cust" || p.Name == "persist"))
			{
				_currentDeviceInfo.Brand = "Xiaomi/Redmi";
			}
			else if (Partitions.Exists((PartitionInfo p) => p.Name.Contains("lenovo") || p.Name == "proinfo"))
			{
				_currentDeviceInfo.Brand = "Lenovo";
			}
		}
	}

	private string DetectDeviceVendor()
	{
		QualcommChipInfo qualcommChipInfo = _service?.ChipInfo;
		if (_currentDeviceInfo != null && !string.IsNullOrEmpty(_currentDeviceInfo.Vendor) && _currentDeviceInfo.Vendor != "Unknown")
		{
			if (qualcommChipInfo != null && !string.IsNullOrEmpty(qualcommChipInfo.PkHash))
			{
				string vendorByPkHash = QualcommDatabase.GetVendorByPkHash(qualcommChipInfo.PkHash);
				if (!string.IsNullOrEmpty(vendorByPkHash) && vendorByPkHash != "Unknown")
				{
					string normalizedCurrentVendor = NormalizeVendorName(_currentDeviceInfo.Vendor);
					string normalizedPkHashVendor = NormalizeVendorName(vendorByPkHash);
					if (normalizedCurrentVendor != normalizedPkHashVendor)
					{
						_logDetail($"[Vendor] 厂商判定冲突: DeviceInfo={normalizedCurrentVendor}, PKHash={normalizedPkHashVendor}, 采用 PKHash");
						return normalizedPkHashVendor;
					}
				}
			}
			return NormalizeVendorName(_currentDeviceInfo.Vendor);
		}
		if (Partitions != null && Partitions.Count > 0)
		{
			if (Partitions.Exists((PartitionInfo p) => p.Name == "proinfo" || p.Name == "lenovocust"))
			{
				return "Lenovo";
			}
			bool num = Partitions.Exists((PartitionInfo p) => p.Name.Contains("oplus") || p.Name.Contains("oppo"));
			int num2 = Partitions.Count((PartitionInfo p) => p.Name.StartsWith("my_engineering") || p.Name.StartsWith("my_carrier") || p.Name == "my_stock" || p.Name == "my_region" || p.Name == "my_custom" || p.Name == "my_bigball");
			if (num || num2 >= 2)
			{
				return "OPLUS";
			}
			if (Partitions.Exists((PartitionInfo p) => p.Name.Contains("xiaomi") || p.Name.Contains("miui")) || (Partitions.Exists((PartitionInfo p) => p.Name == "cust") && Partitions.Exists((PartitionInfo p) => p.Name == "persist")))
			{
				return "Xiaomi";
			}
			if (Partitions.Exists((PartitionInfo p) => p.Name.Contains("zte") || p.Name.Contains("nubia")))
			{
				return "ZTE";
			}
		}
		if (qualcommChipInfo != null && !string.IsNullOrEmpty(qualcommChipInfo.PkHash))
		{
			string vendorByPkHash = QualcommDatabase.GetVendorByPkHash(qualcommChipInfo.PkHash);
			if (!string.IsNullOrEmpty(vendorByPkHash) && vendorByPkHash != "Unknown")
			{
				if (qualcommChipInfo.OemId > 0)
				{
					string vendorName = QualcommDatabase.GetVendorName(qualcommChipInfo.OemId);
					if (!string.IsNullOrEmpty(vendorName) && !vendorName.Contains("Unknown"))
					{
						string normalizedVendorName = NormalizeVendorName(vendorName);
						string normalizedPkHashVendor = NormalizeVendorName(vendorByPkHash);
						if (normalizedVendorName != normalizedPkHashVendor)
						{
							_logDetail($"[Vendor] 厂商判定冲突: OEM={normalizedVendorName}, PKHash={normalizedPkHashVendor}, 采用 PKHash");
						}
					}
				}
				return NormalizeVendorName(vendorByPkHash);
			}
		}
		if (qualcommChipInfo != null && qualcommChipInfo.OemId > 0)
		{
			string vendorName2 = QualcommDatabase.GetVendorName(qualcommChipInfo.OemId);
			if (!string.IsNullOrEmpty(vendorName2) && !vendorName2.Contains("Unknown"))
			{
				return NormalizeVendorName(vendorName2);
			}
		}
		return "Unknown";
	}

	private string NormalizeVendorName(string vendor)
	{
		if (string.IsNullOrEmpty(vendor))
		{
			return "Unknown";
		}
		string text = vendor.ToLower();
		if (text.Contains("oppo") || text.Contains("realme") || text.Contains("oneplus") || text.Contains("oplus"))
		{
			return "OPLUS";
		}
		if (text.Contains("xiaomi") || text.Contains("redmi") || text.Contains("poco"))
		{
			return "Xiaomi";
		}
		if (text.Contains("lenovo") || text.Contains("motorola"))
		{
			return "Lenovo";
		}
		if (text.Contains("zte") || text.Contains("nubia"))
		{
			return "ZTE";
		}
		return vendor;
	}

	private static string MapBuildPropVendorHint(string vendor)
	{
		if (string.IsNullOrWhiteSpace(vendor))
		{
			return string.Empty;
		}
		switch (vendor.ToLowerInvariant())
		{
		case "oppo":
		case "realme":
		case "oplus":
		case "oneplus":
			return "OnePlus";
		case "poco":
		case "xiaomi":
		case "redmi":
			return "Xiaomi";
		case "lenovo":
		case "motorola":
			return "Lenovo";
		case "nubia":
		case "zte":
			return "ZTE";
		default:
			return string.Empty;
		}
	}

	public async Task<bool> ReadBuildPropFromDeviceAsync()
	{
		if (!(await EnsureConnectedAsync()))
		{
			return false;
		}
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			return false;
		}
		bool num = Partitions != null && Partitions.Exists((PartitionInfo p) => p.Name == "super");
		bool flag = Partitions != null && Partitions.Exists((PartitionInfo p) => p.Name == "system" || p.Name.StartsWith("system_"));
		bool flag2 = Partitions != null && Partitions.Exists((PartitionInfo p) => p.Name == "vendor" || p.Name.StartsWith("vendor_"));
		if (!num && !flag && !flag2)
		{
			Log("无 super/system/vendor 分区，跳过", ConsoleColor.Yellow);
			return false;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			StartOperation("读取设备信息");
			Log("正在从设备读取 build.prop...", ConsoleColor.Cyan);
			await TryReadBuildPropInternalAsync();
			bool num2 = _currentDeviceInfo != null && !string.IsNullOrEmpty(_currentDeviceInfo.MarketName);
			if (num2)
			{
				PrintFullDeviceLog();
			}
			return num2;
		}
		catch (Exception ex)
		{
			Log("读取 build.prop 失败: " + ex.Message, ConsoleColor.Red);
			return false;
		}
		finally
		{
			IsBusy = false;
		}
	}

	private async Task TryReadBuildPropInternalAsync()
	{
		using CancellationTokenSource totalTimeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(90L));
		using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, totalTimeoutCts.Token);
		try
		{
			await TryReadBuildPropCoreAsync(linkedCts.Token);
		}
		catch (OperationCanceledException)
		{
			Log(totalTimeoutCts.IsCancellationRequested ? "设备信息解析超时 (90秒)" : "设备信息解析已取消", ConsoleColor.Yellow);
		}
		catch (Exception ex2)
		{
			Log("设备信息解析失败: " + ex2.Message, ConsoleColor.Yellow);
		}
	}

	private async Task TryReadBuildPropCoreAsync(CancellationToken ct)
	{
		if (_deviceInfoService == null)
		{
			_deviceInfoService = new DeviceInfoService(delegate(string msg)
			{
				Log(msg);
			}, _logDetail);
		}
		Func<string, long, int, Task<byte[]>> readPartition = async delegate(string partName, long offset, int size)
		{
			if (ct.IsCancellationRequested)
			{
				return (byte[])null;
			}
			if (Partitions == null || !Partitions.Exists((PartitionInfo p) => p.Name == partName || p.Name.StartsWith(partName + "_")))
			{
				return (byte[])null;
			}
			try
			{
				using CancellationTokenSource timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10L));
				using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
				return await _service.ReadPartitionDataAsync(partName, offset, size, linked.Token);
			}
			catch
			{
				return (byte[])null;
			}
		};
		string currentSlot = _service.CurrentSlot;
		bool hasSuper = Partitions.Exists((PartitionInfo p) => p.Name == "super");
		long superStartSector = 0L;
		if (hasSuper)
		{
			PartitionInfo partitionInfo = Partitions.Find((PartitionInfo p) => p.Name == "super");
			if (partitionInfo != null)
			{
				superStartSector = partitionInfo.StartSector;
			}
		}
		int physicalSectorSize = ((_service.SectorSize > 0) ? _service.SectorSize : 512);
		string vendorName = DetectDeviceVendor();
		string vendorHint = MapBuildPropVendorHint(vendorName);
		Log("检测到设备厂商: " + vendorName, ConsoleColor.Cyan);
		BuildPropInfo buildPropInfo = null;
		if (hasSuper || !string.IsNullOrEmpty(vendorHint))
		{
			try
			{
				buildPropInfo = await _deviceInfoService.ReadBuildPropFromDevice(
					readPartition, currentSlot, hasSuper, superStartSector, physicalSectorSize, vendorHint);
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				_logDetail("[BuildProp] 读取异常: " + ex.Message);
			}
		}
		else
		{
			_logDetail("[BuildProp] 跳过读取: 无 super 且厂商未命中提示列表");
		}
		if (buildPropInfo != null)
		{
			Log("成功读取 build.prop", ConsoleColor.Green);
			ApplyBuildPropInfo(buildPropInfo);
		}
		else
		{
			Log("未能读取到设备信息", ConsoleColor.Yellow);
		}

		// ── 读取 devinfo / config 分区获取 BL 解锁状态等 (参考 edl 项目) ──
		await TryReadDevInfoPartitionsAsync(readPartition, ct);
	}

	/// <summary>
	/// 从 devinfo / config 分区读取设备信息 (参考 edl 项目 Modules/generic.py)
	/// - devinfo: ANDROID-BOOT! 结构体 → BL解锁、引导版本、基带版本、显示面板
	/// - config:  OEM unlock 标志
	/// </summary>
	private async Task TryReadDevInfoPartitionsAsync(
		Func<string, long, int, Task<byte[]>> readPartition, CancellationToken ct)
	{
		if (_currentDeviceInfo == null)
			_currentDeviceInfo = new DeviceFullInfo();

		// ── devinfo 分区 ──
		PartitionInfo devinfoPart = FindPartitionByName("devinfo");
		if (devinfoPart != null)
		{
			try
			{
				// 计算实际需要读取的大小:
				// - 标准设备: ANDROID-BOOT! 在偏移 0x00, 只需 0xE4 字节
				// - ZTE 设备: ANDROID-BOOT! 在偏移 0x7FFE00, 需要读到 0x7FFE00+0xE4
				// 先读头部 0x200 快速检测, 若无魔数且分区够大则尝试 ZTE 偏移
				long partSize = devinfoPart.NumSectors * devinfoPart.SectorSize;
				int readSize = (int)Math.Min(partSize, 0x8000);
				byte[] devInfoData = await readPartition("devinfo", 0, readSize);
				if (devInfoData != null && devInfoData.Length > 0)
				{
					_deviceInfoService.ParseDevInfo(devInfoData, _currentDeviceInfo);

					// 若头部未找到 ANDROID-BOOT!, 且分区足够大, 尝试 ZTE 偏移
					if (!_currentDeviceInfo.BootloaderUnlocked.HasValue && partSize > 0x7FFE00 + 0xE4)
					{
						_logDetail("[DevInfo] 头部未发现 ANDROID-BOOT!, 尝试 ZTE 偏移 0x7FFE00");
						byte[] zteData = await readPartition("devinfo", 0x7FFE00, 0xE4);
						if (zteData != null && zteData.Length > 0)
						{
							_deviceInfoService.ParseDevInfoAtOffset(zteData, 0, _currentDeviceInfo, "ANDROID-BOOT@ZTE");
						}
					}
				}

				if (_currentDeviceInfo.BootloaderUnlocked.HasValue)
				{
					Log("  BL解锁    : " + (_currentDeviceInfo.BootloaderUnlocked.Value ? "是" : "否"),
						_currentDeviceInfo.BootloaderUnlocked.Value ? ConsoleColor.Green : ConsoleColor.Red);
				}
				if (!string.IsNullOrEmpty(_currentDeviceInfo.BootloaderVersion))
					Log("  引导版本  : " + _currentDeviceInfo.BootloaderVersion, ConsoleColor.White);
				if (!string.IsNullOrEmpty(_currentDeviceInfo.RadioVersion))
					Log("  基带版本  : " + _currentDeviceInfo.RadioVersion, ConsoleColor.White);
			}
			catch (Exception ex)
			{
				_logDetail("[DevInfo] 读取 devinfo 分区异常: " + ex.Message);
			}
		}
		else
		{
			_logDetail("[DevInfo] 未找到 devinfo 分区, 跳过");
		}

		// ── config 分区 (OEM unlock) ──
		if (!_currentDeviceInfo.BootloaderUnlocked.HasValue)
		{
			PartitionInfo configPart = FindPartitionByName("config");
			if (configPart != null)
			{
				try
				{
					// edl 项目根据分区扇区数决定偏移: 0x7FFF 或 0x7FFFF
					// 只读取包含目标偏移的那个扇区即可，无需整段读取
					int sectorSize = configPart.SectorSize > 0 ? configPart.SectorSize : _service.SectorSize;
					long partSectors = configPart.NumSectors;
					int targetOffset;
					if (partSectors <= (0x8000 / sectorSize))
						targetOffset = 0x7FFF;
					else
						targetOffset = 0x7FFFF;

					// 只读 1 字节 (ReadPartitionDataAsync 内部按扇区对齐)
					byte[] configByte = await readPartition("config", targetOffset, 1);
					if (configByte != null && configByte.Length > 0)
					{
						_currentDeviceInfo.ConfigOemUnlocked = configByte[0] != 0;
						_logDetail($"[Config] OEM解锁标志 @ 0x{targetOffset:X} = 0x{configByte[0]:X2} ({(configByte[0] != 0 ? "已解锁" : "未解锁")})");
						_currentDeviceInfo.Sources["config"] = "OEM-Unlock";
						Log("  OEM解锁   : " + (_currentDeviceInfo.ConfigOemUnlocked.Value ? "是" : "否"),
							_currentDeviceInfo.ConfigOemUnlocked.Value ? ConsoleColor.Green : ConsoleColor.Red);
					}
				}
				catch (Exception ex)
				{
					_logDetail("[DevInfo] 读取 config 分区异常: " + ex.Message);
				}
			}
			else
			{
				_logDetail("[DevInfo] 未找到 config 分区, 跳过");
			}
		}
	}

	/// <summary>
	/// 按名称查找分区 (支持 A/B 槽位后缀)
	/// </summary>
	private PartitionInfo FindPartitionByName(string name)
	{
		if (Partitions == null || Partitions.Count == 0)
			return null;
		// 精确匹配
		var p = Partitions.Find(x => x.Name == name);
		if (p != null) return p;
		// 尝试带槽位后缀
		string slot = _service?.CurrentSlot;
		if (!string.IsNullOrEmpty(slot) && slot != "nonexistent")
		{
			p = Partitions.Find(x => x.Name == name + "_" + slot);
			if (p != null) return p;
		}
		// 尝试 _a / _b
		p = Partitions.Find(x => x.Name == name + "_a");
		if (p != null) return p;
		p = Partitions.Find(x => x.Name == name + "_b");
		return p;
	}

	private void ApplyBuildPropInfo(BuildPropInfo bp)
	{
		if (bp != null)
		{
			if (_currentDeviceInfo == null)
			{
				_currentDeviceInfo = new DeviceFullInfo();
			}
			if (!string.IsNullOrEmpty(bp.Brand))
			{
				_currentDeviceInfo.Brand = bp.Brand;
			}
			if (!string.IsNullOrEmpty(bp.MarketName))
			{
				_currentDeviceInfo.MarketName = bp.MarketName;
			}
			else if (!string.IsNullOrEmpty(bp.Model))
			{
				_currentDeviceInfo.Model = bp.Model;
			}
			if (!string.IsNullOrEmpty(bp.AndroidVersion))
			{
				_currentDeviceInfo.AndroidVersion = bp.AndroidVersion;
			}
			if (!string.IsNullOrEmpty(bp.SdkVersion))
			{
				_currentDeviceInfo.SdkVersion = bp.SdkVersion;
			}
			if (!string.IsNullOrEmpty(bp.OtaVersion))
			{
				_currentDeviceInfo.OtaVersion = bp.OtaVersion;
			}
			if (!string.IsNullOrEmpty(bp.OtaVersionFull))
			{
				_currentDeviceInfo.OtaVersionFull = bp.OtaVersionFull;
			}
			if (!string.IsNullOrEmpty(bp.Codename))
			{
				_currentDeviceInfo.DeviceCodename = bp.Codename;
			}
			else if (!string.IsNullOrEmpty(bp.Device))
			{
				_currentDeviceInfo.DeviceCodename = bp.Device;
			}
			if (!string.IsNullOrEmpty(bp.Fingerprint))
			{
				_currentDeviceInfo.Fingerprint = bp.Fingerprint;
			}
			if (!string.IsNullOrEmpty(bp.SecurityPatch))
			{
				_currentDeviceInfo.SecurityPatch = bp.SecurityPatch;
			}
			if (!string.IsNullOrEmpty(bp.BuildId))
			{
				_currentDeviceInfo.BuildId = bp.BuildId;
			}
			if (!string.IsNullOrEmpty(bp.DisplayId))
			{
				_currentDeviceInfo.DisplayId = bp.DisplayId;
			}
			if (!string.IsNullOrEmpty(bp.BuildDate))
			{
				_currentDeviceInfo.BuiltDate = bp.BuildDate;
			}
			if (!string.IsNullOrEmpty(bp.Region))
			{
				_currentDeviceInfo.Region = bp.Region;
			}
			if (!string.IsNullOrEmpty(bp.MarketRegion))
			{
				_currentDeviceInfo.MarketRegion = bp.MarketRegion;
			}
			if (!string.IsNullOrEmpty(bp.OplusProject))
			{
				_currentDeviceInfo.OplusProject = bp.OplusProject;
			}
			if (!string.IsNullOrEmpty(bp.OplusNvId))
			{
				_currentDeviceInfo.OplusNvId = bp.OplusNvId;
			}
			if (!string.IsNullOrEmpty(bp.SourceEngine))
			{
				_currentDeviceInfo.BuildPropEngine = bp.SourceEngine;
			}
			if (!string.IsNullOrEmpty(bp.SourcePartition))
			{
				_currentDeviceInfo.BuildPropSourcePartition = bp.SourcePartition;
			}
			if (!string.IsNullOrEmpty(bp.SourcePath))
			{
				_currentDeviceInfo.BuildPropSourcePath = bp.SourcePath;
			}
			if (bp.SourceConfidence > 0)
			{
				_currentDeviceInfo.BuildPropConfidence = bp.SourceConfidence;
			}
		}
	}

	public void PrintFullDeviceLog()
	{
		if (_currentDeviceInfo != null)
		{
			DeviceFullInfo currentDeviceInfo = _currentDeviceInfo;
			Log("================================================", ConsoleColor.DarkGray);
			Log("完整设备信息", ConsoleColor.Green);
			Log("================================================", ConsoleColor.DarkGray);
			string value = ((!string.IsNullOrEmpty(currentDeviceInfo.MarketName)) ? currentDeviceInfo.MarketName : ((!string.IsNullOrEmpty(currentDeviceInfo.Brand) && !string.IsNullOrEmpty(currentDeviceInfo.Model)) ? (currentDeviceInfo.Brand + " " + currentDeviceInfo.Model) : "未知"));
			PrintField("市场名称", value);
			PrintField("设备型号", currentDeviceInfo.Model);
			PrintField("生产厂家", currentDeviceInfo.Brand);
			PrintField("安卓版本", currentDeviceInfo.AndroidVersion + ((!string.IsNullOrEmpty(currentDeviceInfo.SdkVersion)) ? (" [SDK:" + currentDeviceInfo.SdkVersion + "]") : ""));
			PrintField("安全补丁", currentDeviceInfo.SecurityPatch);
			PrintField("设备代号", currentDeviceInfo.DeviceCodename);
			PrintField("区域代码", currentDeviceInfo.Region);
			PrintField("构建 ID", currentDeviceInfo.BuildId);
			PrintField("编译日期", currentDeviceInfo.BuiltDate);
			if (!string.IsNullOrEmpty(currentDeviceInfo.OtaVersion))
			{
				Log($"  {"OTA 版本",-10} : {currentDeviceInfo.OtaVersion}", ConsoleColor.Green);
			}
			if (!string.IsNullOrEmpty(currentDeviceInfo.Fingerprint))
			{
				Log($"  {"构建指纹",-10} : {currentDeviceInfo.Fingerprint}", ConsoleColor.DarkGray);
			}
			if (!string.IsNullOrEmpty(currentDeviceInfo.OplusProject))
			{
				PrintField("OPLUS项目", currentDeviceInfo.OplusProject);
			}
			if (!string.IsNullOrEmpty(currentDeviceInfo.BuildPropEngine))
			{
				PrintField("信息来源", currentDeviceInfo.BuildPropEngine);
			}
			if (!string.IsNullOrEmpty(currentDeviceInfo.BuildPropSourcePartition))
			{
				PrintField("来源分区", currentDeviceInfo.BuildPropSourcePartition);
			}
			if (!string.IsNullOrEmpty(currentDeviceInfo.BuildPropSourcePath))
			{
				PrintField("来源路径", currentDeviceInfo.BuildPropSourcePath);
			}
			if (currentDeviceInfo.BuildPropConfidence > 0)
			{
				PrintField("来源置信度", currentDeviceInfo.BuildPropConfidence + "%");
			}
			Log("================================================", ConsoleColor.DarkGray);
		}
		static void PrintField(string label, string value2)
		{
			if (!string.IsNullOrEmpty(value2))
			{
				Log($"  {label,-10} : {value2}", ConsoleColor.White);
			}
		}
	}

	public async Task<bool> ReadPartitionTableAsync()
	{
		if (!(await EnsureConnectedAsync()))
		{
			return false;
		}
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			return false;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			StartOperation("读取分区表");
			Log("正在读取分区表 (GPT)...", ConsoleColor.Cyan);
			int maxLuns = 6;
			Progress<Tuple<int, int>> totalProgress = new Progress<Tuple<int, int>>(delegate(Tuple<int, int> t)
			{
				ShowProgress("GPT", 80.0 * (double)t.Item1 / (double)t.Item2);
			});
			Progress<double> subProgress = new Progress<double>(delegate
			{
			});
			List<PartitionInfo> list = await _service.ReadAllGptAsync(maxLuns, totalProgress, subProgress, _cts.Token);
			if (list != null && list.Count > 0)
			{
				Partitions = list;
				UpdateDeviceInfoFromPartitions();
				this.PartitionsLoaded?.Invoke(this, list);
				Log($"成功读取 {list.Count} 个分区", ConsoleColor.Green);
				SkipSahara = true;
				PrintPartitionTable(list);
				return true;
			}
			Log("未读取到分区", ConsoleColor.Yellow);
			return false;
		}
		catch (Exception ex)
		{
			Log("读取分区表失败: " + ex.Message, ConsoleColor.Red);
			return false;
		}
		finally
		{
			EndOperation();
		}
	}

	public void PrintPartitionTable(List<PartitionInfo> partitions = null)
	{
		List<PartitionInfo> list = partitions ?? Partitions;
		if (list == null || list.Count == 0)
		{
			Log("无分区数据", ConsoleColor.Yellow);
			return;
		}
		Log("");
		Log($"{"序号",-5} {"分区名",-24} {"LUN",-5} {"字节",-14} {"大小",-12} {"起始扇区",-14} {"扇区数",-12}", ConsoleColor.Cyan);
		Log(new string('-', 110), ConsoleColor.DarkGray);
		for (int i = 0; i < list.Count; i++)
		{
			PartitionInfo partitionInfo = list[i];
			ConsoleColor value = (RawprogramParser.IsSensitivePartition(partitionInfo.Name) ? ConsoleColor.DarkYellow : ConsoleColor.White);
			long num = ((partitionInfo.Size > 0) ? partitionInfo.Size : (partitionInfo.NumSectors * partitionInfo.SectorSize));
			Log($"{i + 1,-5} {partitionInfo.Name,-24} {partitionInfo.Lun,-5} {num,-14} {partitionInfo.FormattedSize,-12} {partitionInfo.StartSector,-14} {partitionInfo.NumSectors,-12}", value);
		}
		Log("");
	}

	public async Task<bool> ReadPartitionAsync(string partitionName)
	{
		string backupFolder = Path.Combine(
			Directory.GetCurrentDirectory(),
			"Backup_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
		string outputPath = Path.Combine(backupFolder, partitionName + ".img");
		return await ReadPartitionAsync(partitionName, outputPath);
	}

	public async Task<bool> ReadPartitionAsync(string partitionName, string outputPath)
	{
		if (!(await EnsureConnectedAsync()))
		{
			return false;
		}
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			return false;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			PartitionInfo partitionInfo = Partitions?.FirstOrDefault((PartitionInfo p) => p.Name == partitionName || p.Name.StartsWith(partitionName + "_"));
			_operationTotalBytes = ((partitionInfo != null) ? (partitionInfo.NumSectors * partitionInfo.SectorSize) : 0);
			StartOperation("读取 " + partitionName);
			Log("正在读取分区 " + partitionName + "...", ConsoleColor.Cyan);
			Progress<double> progress = new Progress<double>(delegate(double p)
			{
				ShowProgress("读取 " + partitionName, p);
			});
			bool num = await _service.ReadPartitionAsync(partitionName, outputPath, progress, _cts.Token);
			if (num)
			{
				Log("分区 " + partitionName + " 已保存到 " + outputPath, ConsoleColor.Green);
			}
			else
			{
				Log("读取 " + partitionName + " 失败", ConsoleColor.Red);
			}
			return num;
		}
		catch (Exception ex)
		{
			Log("读取分区失败: " + ex.Message, ConsoleColor.Red);
			return false;
		}
		finally
		{
			EndOperation();
		}
	}

	public async Task<bool> WritePartitionAsync(string partitionName, string filePath)
	{
		if (!(await EnsureConnectedAsync()))
		{
			return false;
		}
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			return false;
		}
		if (!File.Exists(filePath))
		{
			Log("文件不存在: " + filePath, ConsoleColor.Red);
			return false;
		}
		if (ProtectSensitivePartitions && RawprogramParser.IsSensitivePartition(partitionName))
		{
			Log("跳过敏感分区: " + partitionName, ConsoleColor.Yellow);
			return false;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			_operationTotalBytes = new FileInfo(filePath).Length;
			StartOperation("写入 " + partitionName);
			Log("正在写入分区 " + partitionName + "...", ConsoleColor.Cyan);
			Progress<double> progress = new Progress<double>(delegate(double p)
			{
				ShowProgress("写入 " + partitionName, p);
			});
			bool num = await _service.WritePartitionAsync(partitionName, filePath, progress, _cts.Token);
			if (num)
			{
				Log("分区 " + partitionName + " 写入成功", ConsoleColor.Green);
			}
			else
			{
				Log("写入 " + partitionName + " 失败", ConsoleColor.Red);
			}
			return num;
		}
		catch (Exception ex)
		{
			Log("写入分区失败: " + ex.Message, ConsoleColor.Red);
			return false;
		}
		finally
		{
			EndOperation();
		}
	}

	public async Task<bool> ErasePartitionAsync(string partitionName)
	{
		if (!(await EnsureConnectedAsync()))
		{
			return false;
		}
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			return false;
		}
		if (ProtectSensitivePartitions && RawprogramParser.IsSensitivePartition(partitionName))
		{
			Log("跳过敏感分区: " + partitionName, ConsoleColor.Yellow);
			return false;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			StartOperation("擦除 " + partitionName);
			Log("正在擦除分区 " + partitionName + "...", ConsoleColor.Cyan);
			bool num = await _service.ErasePartitionAsync(partitionName, _cts.Token);
			if (num)
			{
				Log("分区 " + partitionName + " 已擦除", ConsoleColor.Green);
			}
			else
			{
				Log("擦除 " + partitionName + " 失败", ConsoleColor.Red);
			}
			return num;
		}
		catch (Exception ex)
		{
			Log("擦除分区失败: " + ex.Message, ConsoleColor.Red);
			return false;
		}
		finally
		{
			EndOperation();
		}
	}

	public async Task<bool> ReadStorageInfoAsync()
	{
		if (!(await EnsureConnectedAsync()))
		{
			Log("设备未连接，请先连接设备", ConsoleColor.Red);
			return false;
		}
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			return false;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			StartOperation("读取存储信息");
			Log("正在读取 Firehose 存储信息...", ConsoleColor.Cyan);
			FirehoseStorageInfo firehoseStorageInfo = await _service.GetFirehoseStorageInfoAsync(Token);
			if (firehoseStorageInfo == null)
			{
				Log("存储信息读取失败: Firehose 未连接", ConsoleColor.Red);
				return false;
			}
			Log(firehoseStorageInfo.Success ? "存储信息读取成功" : "存储信息读取失败", firehoseStorageInfo.Success ? ConsoleColor.Green : ConsoleColor.Yellow);
			if (!string.IsNullOrEmpty(firehoseStorageInfo.ErrorMessage))
			{
				Log("失败原因: " + firehoseStorageInfo.ErrorMessage, ConsoleColor.Yellow);
			}
			if (firehoseStorageInfo.Attributes.Count > 0)
			{
				Log("响应属性:", ConsoleColor.White);
				foreach (KeyValuePair<string, string> attribute in firehoseStorageInfo.Attributes.OrderBy((KeyValuePair<string, string> x) => x.Key))
				{
					Log($"  {attribute.Key}: {attribute.Value}", ConsoleColor.White);
				}
			}
			if (firehoseStorageInfo.Logs.Count > 0)
			{
				Log("设备日志:", ConsoleColor.White);
				int num = Math.Min(12, firehoseStorageInfo.Logs.Count);
				for (int i = 0; i < num; i++)
				{
					Log("  " + firehoseStorageInfo.Logs[i], ConsoleColor.DarkGray);
				}
				if (firehoseStorageInfo.Logs.Count > num)
				{
					Log($"  ... 还有 {firehoseStorageInfo.Logs.Count - num} 行", ConsoleColor.DarkGray);
				}
			}
			return firehoseStorageInfo.Success;
		}
		catch (Exception ex)
		{
			Log("读取存储信息失败: " + ex.Message, ConsoleColor.Red);
			return false;
		}
		finally
		{
			EndOperation();
		}
	}

	public async Task<bool> ConnectDiagAsync(string portName, int baudRate = 115200)
	{
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			return false;
		}
		if (string.IsNullOrWhiteSpace(portName))
		{
			Log("未指定 Diag 端口", ConsoleColor.Red);
			return false;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			StartOperation("连接 Diag");
			if (_service == null)
			{
				_service = CreateService();
			}
			bool flag = await _service.ConnectDiagAsync(portName, baudRate);
			Log(flag ? $"Diag 连接成功: {portName}" : $"Diag 连接失败: {portName}", flag ? ConsoleColor.Green : ConsoleColor.Red);
			return flag;
		}
		catch (Exception ex)
		{
			Log("Diag 连接异常: " + ex.Message, ConsoleColor.Red);
			return false;
		}
		finally
		{
			EndOperation();
		}
	}

	public void DisconnectDiag()
	{
		try
		{
			_service?.DisconnectDiag();
			Log("Diag 已断开", ConsoleColor.Gray);
		}
		catch (Exception ex)
		{
			Log("Diag 断开异常: " + ex.Message, ConsoleColor.Yellow);
		}
	}

	public async Task<string> ReadDiagImeiAsync(int slot = 1)
	{
		_logDetail(string.Format("[QCN][Controller][IMEI][Read] request slot={0}", slot));
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			_logDetail("[QCN][Controller][IMEI][Read] abort: controller busy");
			return null;
		}
		if (slot < 1 || slot > 4)
		{
			Log("IMEI 槽位仅支持 1-4", ConsoleColor.Red);
			_logDetail(string.Format("[QCN][Controller][IMEI][Read] abort: invalid slot={0}", slot));
			return null;
		}
		if (_service == null || !_service.IsDiagConnected)
		{
			Log("请先连接 Diag 端口", ConsoleColor.Red);
			_logDetail("[QCN][Controller][IMEI][Read] abort: service is null / diag not connected");
			return null;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			StartOperation("读取 IMEI");
			string text = await _service.ReadDiagImeiAsync(slot);
			_logDetail(string.Format("[QCN][Controller][IMEI][Read] result={0}", string.IsNullOrEmpty(text) ? "failed" : "success"));
			return text;
		}
		catch (Exception ex)
		{
			Log("读取 IMEI 失败: " + ex.Message, ConsoleColor.Red);
			_logDetail(string.Format("[QCN][Controller][IMEI][Read] exception: {0}: {1}", ex.GetType().Name, ex.Message));
			return null;
		}
		finally
		{
			EndOperation();
		}
	}

	public async Task<bool> WriteDiagImeiAsync(string imei, int slot = 1)
	{
		_logDetail(string.Format("[QCN][Controller][IMEI][Write] request slot={0}", slot));
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			_logDetail("[QCN][Controller][IMEI][Write] abort: controller busy");
			return false;
		}
		if (slot < 1 || slot > 4)
		{
			Log("IMEI 槽位仅支持 1-4", ConsoleColor.Red);
			_logDetail(string.Format("[QCN][Controller][IMEI][Write] abort: invalid slot={0}", slot));
			return false;
		}
		if (string.IsNullOrWhiteSpace(imei) || imei.Length != 15 || imei.Any((char ch) => ch < '0' || ch > '9'))
		{
			Log("IMEI 格式错误，必须为 15 位数字", ConsoleColor.Red);
			_logDetail("[QCN][Controller][IMEI][Write] abort: invalid imei format");
			return false;
		}
		if (_service == null || !_service.IsDiagConnected)
		{
			Log("请先连接 Diag 端口", ConsoleColor.Red);
			_logDetail("[QCN][Controller][IMEI][Write] abort: service is null / diag not connected");
			return false;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			StartOperation("写入 IMEI");
			bool flag = await _service.WriteDiagImeiAsync(imei, slot);
			_logDetail(string.Format("[QCN][Controller][IMEI][Write] result={0}", flag ? "success" : "failed"));
			return flag;
		}
		catch (Exception ex)
		{
			Log("写入 IMEI 失败: " + ex.Message, ConsoleColor.Red);
			_logDetail(string.Format("[QCN][Controller][IMEI][Write] exception: {0}: {1}", ex.GetType().Name, ex.Message));
			return false;
		}
		finally
		{
			EndOperation();
		}
	}

	public async Task<ImeiInfo> ReadAllDiagImeiAsync()
	{
		_logDetail("[QCN][Controller][IMEI][ReadAll] request");
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			_logDetail("[QCN][Controller][IMEI][ReadAll] abort: controller busy");
			return null;
		}
		if (_service == null || !_service.IsDiagConnected)
		{
			Log("请先连接 Diag 端口", ConsoleColor.Red);
			_logDetail("[QCN][Controller][IMEI][ReadAll] abort: service is null / diag not connected");
			return null;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			StartOperation("读取全部 IMEI");
			ImeiInfo imeiInfo = await _service.ReadAllDiagImeiAsync();
			_logDetail(string.Format("[QCN][Controller][IMEI][ReadAll] result={0}",
				(imeiInfo != null && (!string.IsNullOrEmpty(imeiInfo.Imei1) || !string.IsNullOrEmpty(imeiInfo.Imei2) || !string.IsNullOrEmpty(imeiInfo.Imei3) || !string.IsNullOrEmpty(imeiInfo.Imei4))) ? "success" : "failed"));
			return imeiInfo;
		}
		catch (Exception ex)
		{
			Log("读取全部 IMEI 失败: " + ex.Message, ConsoleColor.Red);
			_logDetail(string.Format("[QCN][Controller][IMEI][ReadAll] exception: {0}: {1}", ex.GetType().Name, ex.Message));
			return null;
		}
		finally
		{
			EndOperation();
		}
	}

	public async Task<bool> ReadQcnAsync(string outputPath)
	{
		_logDetail(string.Format("[QCN][Controller][Read] request output={0}", outputPath ?? "<null>"));
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			_logDetail("[QCN][Controller][Read] abort: controller busy");
			return false;
		}
		if (string.IsNullOrWhiteSpace(outputPath))
		{
			Log("未指定输出路径", ConsoleColor.Red);
			_logDetail("[QCN][Controller][Read] abort: output path is empty");
			return false;
		}
		if (_service == null)
		{
			Log("请先连接 Diag 端口", ConsoleColor.Red);
			_logDetail("[QCN][Controller][Read] abort: service is null / diag not connected");
			return false;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			StartOperation("读取 QCN");
			_progressShowRateEta = false;
			Log("正在读取 QCN: " + outputPath, ConsoleColor.Cyan);
			Stopwatch stopwatch = Stopwatch.StartNew();
			int lastProgressBucket = -1;
			Progress<int> progress = new Progress<int>(delegate(int p)
			{
				ShowProgress("读取 QCN", p);
				int num = Math.Max(0, Math.Min(100, p));
				int num2 = num / 5;
				if (num2 != lastProgressBucket)
				{
					lastProgressBucket = num2;
					_logDetail(string.Format("[QCN][Controller][Read] progress={0}%", num));
				}
			});
			bool flag = await _service.ReadQcnAsync(outputPath, progress, Token);
			stopwatch.Stop();
			Log(flag ? "QCN 读取成功" : "QCN 读取失败", flag ? ConsoleColor.Green : ConsoleColor.Red);
			if (flag && File.Exists(outputPath))
			{
				long length = new FileInfo(outputPath).Length;
				_operationTotalBytes = length;
				_logDetail(string.Format("[QCN][Controller][Read] result=success elapsed={0:F2}s size={1} bytes",
					stopwatch.Elapsed.TotalSeconds, length));
			}
			else
			{
				_logDetail(string.Format("[QCN][Controller][Read] result={0} elapsed={1:F2}s",
					flag ? "success" : "failed", stopwatch.Elapsed.TotalSeconds));
			}
			return flag;
		}
		catch (Exception ex)
		{
			Log("读取 QCN 失败: " + ex.Message, ConsoleColor.Red);
			_logDetail(string.Format("[QCN][Controller][Read] exception: {0}: {1}", ex.GetType().Name, ex.Message));
			return false;
		}
		finally
		{
			EndOperation();
		}
	}

	public async Task<bool> WriteQcnAsync(string filePath)
	{
		_logDetail(string.Format("[QCN][Controller][Write] request input={0}", filePath ?? "<null>"));
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			_logDetail("[QCN][Controller][Write] abort: controller busy");
			return false;
		}
		if (string.IsNullOrWhiteSpace(filePath))
		{
			Log("未指定 QCN 文件路径", ConsoleColor.Red);
			_logDetail("[QCN][Controller][Write] abort: input path is empty");
			return false;
		}
		if (!File.Exists(filePath))
		{
			Log("QCN 文件不存在: " + filePath, ConsoleColor.Red);
			_logDetail("[QCN][Controller][Write] abort: input file not found");
			return false;
		}
		if (_service == null)
		{
			Log("请先连接 Diag 端口", ConsoleColor.Red);
			_logDetail("[QCN][Controller][Write] abort: service is null / diag not connected");
			return false;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			long length = new FileInfo(filePath).Length;
			_operationTotalBytes = length;
			_logDetail(string.Format("[QCN][Controller][Write] source size={0} bytes", length));
			StartOperation("写入 QCN");
			ConfigureProgressTelemetry(length, null);
			Log("正在写入 QCN: " + filePath, ConsoleColor.Cyan);
			Stopwatch stopwatch = Stopwatch.StartNew();
			int lastProgressBucket = -1;
			Progress<int> progress = new Progress<int>(delegate(int p)
			{
				ShowProgress("写入 QCN", p);
				int num = Math.Max(0, Math.Min(100, p));
				int num2 = num / 5;
				if (num2 != lastProgressBucket)
				{
					lastProgressBucket = num2;
					_logDetail(string.Format("[QCN][Controller][Write] progress={0}%", num));
				}
			});
			bool flag = await _service.WriteQcnAsync(filePath, progress, Token);
			stopwatch.Stop();
			Log(flag ? "QCN 写入成功" : "QCN 写入失败", flag ? ConsoleColor.Green : ConsoleColor.Red);
			_logDetail(string.Format("[QCN][Controller][Write] result={0} elapsed={1:F2}s",
				flag ? "success" : "failed", stopwatch.Elapsed.TotalSeconds));
			return flag;
		}
		catch (Exception ex)
		{
			Log("写入 QCN 失败: " + ex.Message, ConsoleColor.Red);
			_logDetail(string.Format("[QCN][Controller][Write] exception: {0}: {1}", ex.GetType().Name, ex.Message));
			return false;
		}
		finally
		{
			EndOperation();
		}
	}

	public async Task<int> WritePartitionsBatchAsync(List<Tuple<string, string, int, long>> tasks, List<string> patchFiles, bool activateBootLun)
	{
		if (!(await EnsureConnectedAsync()))
		{
			return 0;
		}
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			return 0;
		}
		int total = tasks.Count;
		int success = 0;
		bool hasPatch = patchFiles != null && patchFiles.Count > 0;
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			StartOperation("批量写入");
			long num = 0L;
			foreach (Tuple<string, string, int, long> task in tasks)
			{
				try
				{
					if (File.Exists(task.Item2))
					{
						num += new FileInfo(task.Item2).Length;
					}
				}
				catch
				{
				}
			}
			_operationTotalBytes = num;
			Log($"开始批量写入 {total} 个分区 (约 {QualcommConsole.Common.SizeFormatter.FormatSize(num)})...", ConsoleColor.Cyan);
			for (int i = 0; i < total; i++)
			{
				if (_cts.Token.IsCancellationRequested)
				{
					break;
				}
				Tuple<string, string, int, long> tuple = tasks[i];
				string partName = tuple.Item1;
				string item = tuple.Item2;
				if (ProtectSensitivePartitions && RawprogramParser.IsSensitivePartition(partName))
				{
					Log($"  [{i + 1}/{total}] 跳过敏感分区: {partName}", ConsoleColor.Yellow);
					continue;
				}
				ShowProgress($"写入 {partName} ({i + 1}/{total})", (double)i / (double)total * 100.0);
				bool flag = partName == "PrimaryGPT" || partName == "BackupGPT" || partName.StartsWith("gpt_main") || partName.StartsWith("gpt_backup") || tuple.Item4 != 0;
				bool flag2;
				try
				{
					Progress<double> progress = new Progress<double>(delegate(double p)
					{
						ShowProgress("写入 " + partName, p);
					});
					flag2 = ((!flag) ? (await _service.WritePartitionAsync(partName, item, progress, _cts.Token)) : (await _service.WriteDirectAsync(partName, item, tuple.Item3, tuple.Item4, progress, _cts.Token)));
				}
				catch (Exception ex)
				{
					flag2 = false;
					Log($"  [{i + 1}/{total}] 失败 {partName}: {ex.Message}", ConsoleColor.Red);
				}
				if (flag2)
				{
					success++;
					Log($"  [{i + 1}/{total}] 成功 {partName}", ConsoleColor.Green);
				}
				else
				{
					Log($"  [{i + 1}/{total}] 失败 {partName}", ConsoleColor.Red);
				}
			}
			if (hasPatch && !_cts.Token.IsCancellationRequested)
			{
				Log($"应用 {patchFiles.Count} 个 Patch 文件...", ConsoleColor.Cyan);
				int value = await _service.ApplyPatchFilesAsync(patchFiles, _cts.Token);
				Log($"成功应用 {value} 个补丁", ConsoleColor.Green);
			}
			if (!_cts.Token.IsCancellationRequested)
			{
				Log("修复 GPT 分区表...", ConsoleColor.Cyan);
				bool flag3 = await _service.FixGptAsync(-1, _cts.Token);
				Log(flag3 ? "GPT 修复成功" : "GPT 修复失败", flag3 ? ConsoleColor.Green : ConsoleColor.Yellow);
			}
			if (activateBootLun && !_cts.Token.IsCancellationRequested)
			{
				Log("回读 GPT 检测槽位...", ConsoleColor.Cyan);
				await _service.ReadAllGptAsync(6, _cts.Token);
				string currentSlot = _service.CurrentSlot;
				int i = ((currentSlot == "a") ? 1 : ((currentSlot == "b") ? 2 : (-1)));
				if (i > 0)
				{
					Log($"激活 LUN{i} (slot_{currentSlot})...", ConsoleColor.Cyan);
					bool flag4 = await _service.SetBootLunAsync(i, _cts.Token);
					Log(flag4 ? $"LUN{i} 激活成功" : $"LUN{i} 激活失败", flag4 ? ConsoleColor.Green : ConsoleColor.Yellow);
				}
			}
			Log("------------------------------------------------", ConsoleColor.DarkGray);
			Log((success == total) ? $"批量写入完成: {total} 个分区全部成功!" : $"批量写入完成: {success}/{total} 成功", (success == total) ? ConsoleColor.Green : ConsoleColor.Yellow);
			return success;
		}
		catch (Exception ex2)
		{
			Log("批量写入失败: " + ex2.Message, ConsoleColor.Red);
			return success;
		}
		finally
		{
			EndOperation();
		}
	}

	public async Task<bool> RebootToEdlAsync()
	{
		if (!(await EnsureConnectedAsync()))
		{
			return false;
		}
		try
		{
			bool num = await _service.RebootToEdlAsync(Token);
			if (num)
			{
				Log("已发送重启到 EDL 命令", ConsoleColor.Green);
				AfterDeviceRebootDisconnected();
			}
			return num;
		}
		catch (Exception ex)
		{
			Log("重启到 EDL 失败: " + ex.Message, ConsoleColor.Red);
			return false;
		}
	}

	public async Task<bool> RebootToSystemAsync()
	{
		if (!(await EnsureConnectedAsync()))
		{
			return false;
		}
		try
		{
			bool num = await _service.RebootAsync(Token);
			if (num)
			{
				Log("设备正在重启到系统", ConsoleColor.Green);
				AfterDeviceRebootDisconnected();
			}
			return num;
		}
		catch (Exception ex)
		{
			Log("重启失败: " + ex.Message, ConsoleColor.Red);
			return false;
		}
	}

	public async Task<bool> SwitchSlotAsync(string slot)
	{
		if (!(await EnsureConnectedAsync()))
		{
			return false;
		}
		try
		{
			bool flag = await _service.SetActiveSlotAsync(slot, Token);
			Log(flag ? ("已切换到槽位 " + slot) : "切换槽位失败", flag ? ConsoleColor.Green : ConsoleColor.Red);
			return flag;
		}
		catch (Exception ex)
		{
			Log("切换槽位失败: " + ex.Message, ConsoleColor.Red);
			return false;
		}
	}

	public async Task<bool> SetBootLunAsync(int lun)
	{
		if (!(await EnsureConnectedAsync()))
		{
			return false;
		}
		try
		{
			bool flag = await _service.SetBootLunAsync(lun, Token);
			Log(flag ? $"LUN {lun} 已激活" : "激活 LUN 失败", flag ? ConsoleColor.Green : ConsoleColor.Red);
			return flag;
		}
		catch (Exception ex)
		{
			Log("激活 LUN 失败: " + ex.Message, ConsoleColor.Red);
			return false;
		}
	}

	public async Task<bool> ResetSaharaAsync(string portName)
	{
		if (IsBusy)
		{
			Log("操作进行中", ConsoleColor.Yellow);
			return false;
		}
		try
		{
			IsBusy = true;
			ResetCancellationToken();
			Log("正在重置 Sahara 状态...", ConsoleColor.Cyan);
			if (_service == null)
			{
				_service = new QualcommService(delegate(string msg)
				{
					Log(msg);
				}, null, _logDetail);
			}
			bool num = await _service.ResetSaharaAsync(portName, _cts.Token);
			if (num)
			{
				Log("Sahara 状态重置成功！", ConsoleColor.Green);
				SkipSahara = false;
			}
			else
			{
				Log("Sahara 重置失败", ConsoleColor.Red);
			}
			return num;
		}
		catch (Exception ex)
		{
			Log("重置异常: " + ex.Message, ConsoleColor.Red);
			return false;
		}
		finally
		{
			IsBusy = false;
		}
	}

	private async Task<bool> EnsureConnectedAsync()
	{
		if (_service == null)
		{
			return false;
		}
		if (_service.IsPortReleased && !(await _service.EnsurePortOpenAsync(CancellationToken.None)))
		{
			return false;
		}
		return _service.IsConnectedFast;
	}

	public void CancelOperation(bool silent = false)
	{
		if (_cts != null)
		{
			if (!silent)
			{
				Log("正在取消操作...", ConsoleColor.Yellow);
			}
			_cts.Cancel();
			_cts.Dispose();
			_cts = null;
		}
	}

	private void ResetCancellationToken()
	{
		if (_cts != null)
		{
			try
			{
				_cts.Cancel();
			}
			catch
			{
			}
			try
			{
				_cts.Dispose();
			}
			catch
			{
			}
		}
		_cts = new CancellationTokenSource();
	}

	private void StartOperation(string name)
	{
		_operationStopwatch = Stopwatch.StartNew();
		_currentOperationName = name;
		_lastProgressTenths = -1;
		_lastProgressRenderTime = DateTime.MinValue;
		_progressShowRateEta = true;
		ResetProgressTelemetry();
	}

	private void AfterDeviceRebootDisconnected()
	{
		try
		{
			_service?.Dispose();
		}
		catch
		{
		}
		_service = null;
		CancelOperation(silent: true);
		Partitions?.Clear();
		_currentDeviceInfo = null;
		ClearCachedProbeHello();
		this.ConnectionStateChanged?.Invoke(this, e: false);
	}

	private void EndOperation()
	{
		IsBusy = false;
		if (_operationStopwatch != null)
		{
			_operationStopwatch.Stop();
			TimeSpan elapsed = _operationStopwatch.Elapsed;
			if (_operationTotalBytes > 0 && elapsed.TotalSeconds > 0.1)
			{
				double avgSpeed = (double)_operationTotalBytes / elapsed.TotalSeconds;
				if (avgSpeed > 10.0)
				{
					Log($"平均速度: {QualcommConsole.Common.SizeFormatter.FormatSpeed(avgSpeed)} ({QualcommConsole.Common.SizeFormatter.FormatSize(_operationTotalBytes)})", ConsoleColor.DarkGray);
				}
			}
			Log($"耗时: {(int)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}", ConsoleColor.DarkGray);
			_operationStopwatch = null;
		}
		_operationTotalBytes = 0L;
	}

	public void DisconnectDevice()
	{
		if (_service != null)
		{
			_service.Disconnect();
			Log("已断开设备连接", ConsoleColor.Yellow);
			this.ConnectionStateChanged?.Invoke(this, e: false);
		}
	}

	private void ShowProgress(string label, double percent)
	{
		double num = Math.Max(0.0, Math.Min(100.0, percent));
		int num2 = (int)Math.Round(num * 10.0, MidpointRounding.AwayFromZero);
		if (num2 > 1000)
		{
			num2 = 1000;
		}
		DateTime utcNow = DateTime.UtcNow;
		bool flag = _lastProgressRenderTime == DateTime.MinValue || utcNow - _lastProgressRenderTime >= ProgressRenderInterval;
		if (num2 == _lastProgressTenths)
		{
			if (num2 == 1000)
			{
				return;
			}
			if (!flag)
			{
				return;
			}
		}
		_lastProgressTenths = num2;
		_lastProgressRenderTime = utcNow;
		int num3 = 25;
		int num4 = (int)Math.Round((double)num3 * num / 100.0, MidpointRounding.AwayFromZero);
		if (num4 < 0)
		{
			num4 = 0;
		}
		if (num4 > num3)
		{
			num4 = num3;
		}
		char c = UseUnicodeProgressChars ? '█' : '#';
		char c2 = UseUnicodeProgressChars ? '░' : '-';
		string value = new string(c, num4) + new string(c2, num3 - num4);
		string value2 = BuildProgressSuffix(num, utcNow);
		lock (ConsoleRenderLock)
		{
			System.Console.Write($"\r  [{value}] {num,5:F1}%{value2}    ");
			if (num >= 100.0)
			{
				System.Console.WriteLine();
				_progressLineActive = false;
			}
			else
			{
				_progressLineActive = true;
			}
		}
	}

	private void ConfigureProgressTelemetry(long totalBytes = 0L, Func<long> bytesProvider = null)
	{
		_progressTelemetryEnabled = true;
		_progressBytesProvider = bytesProvider;
		if (totalBytes > 0)
		{
			_operationTotalBytes = totalBytes;
		}
		_progressSpeedLastSampleTime = DateTime.MinValue;
		_progressSpeedLastPercent = -1.0;
		_progressSpeedLastBytes = -1L;
		_progressSmoothedBytesPerSec = 0.0;
	}

	private void ResetProgressTelemetry()
	{
		_progressTelemetryEnabled = false;
		_progressBytesProvider = null;
		_progressSpeedLastSampleTime = DateTime.MinValue;
		_progressSpeedLastPercent = -1.0;
		_progressSpeedLastBytes = -1L;
		_progressSmoothedBytesPerSec = 0.0;
	}

	private string BuildProgressSuffix(double percent, DateTime nowUtc)
	{
		if (!_progressShowRateEta)
		{
			return "";
		}

		double valueOrDefault = (_service?.GetFirehoseClient()?.CurrentSpeedMBps).GetValueOrDefault();
		if (valueOrDefault > 0.01)
		{
			double firehoseBps = valueOrDefault * 1024.0 * 1024.0;
			if (_progressSmoothedBytesPerSec <= 0.01)
				_progressSmoothedBytesPerSec = firehoseBps;
			else
				_progressSmoothedBytesPerSec = _progressSmoothedBytesPerSec * 0.7 + firehoseBps * 0.3;
			return BuildSpeedEtaSuffix(_progressSmoothedBytesPerSec, percent, -1L);
		}

		if (!_progressTelemetryEnabled)
		{
			return "";
		}

		long num = -1L;
		if (_progressBytesProvider != null)
		{
			try
			{
				num = _progressBytesProvider();
			}
			catch
			{
				num = -1L;
			}
		}
		if (num < 0 && _operationTotalBytes > 0)
		{
			num = (long)((double)_operationTotalBytes * percent / 100.0);
		}
		if (_operationTotalBytes <= 0 && num > 0 && percent > 1.0)
		{
			long num2 = (long)Math.Round((double)num * 100.0 / percent, MidpointRounding.AwayFromZero);
			if (num2 > 0)
			{
				_operationTotalBytes = num2;
			}
		}
		if (_progressSpeedLastSampleTime == DateTime.MinValue)
		{
			_progressSpeedLastSampleTime = nowUtc;
			_progressSpeedLastPercent = percent;
			_progressSpeedLastBytes = num;
			return "";
		}
		if (nowUtc - _progressSpeedLastSampleTime >= ProgressSpeedSampleInterval)
		{
			double totalSeconds = (nowUtc - _progressSpeedLastSampleTime).TotalSeconds;
			if (totalSeconds > 0.05)
			{
				double num3 = -1.0;
				if (num >= 0 && _progressSpeedLastBytes >= 0 && num >= _progressSpeedLastBytes)
				{
					num3 = num - _progressSpeedLastBytes;
				}
				else if (_operationTotalBytes > 0 && percent >= _progressSpeedLastPercent)
				{
					num3 = (double)_operationTotalBytes * (percent - _progressSpeedLastPercent) / 100.0;
				}
				if (num3 > 1.0)
				{
					double num4 = num3 / totalSeconds;
					if (_progressSmoothedBytesPerSec <= 0.01)
					{
						_progressSmoothedBytesPerSec = num4;
					}
					else
					{
						_progressSmoothedBytesPerSec = _progressSmoothedBytesPerSec * 0.7 + num4 * 0.3;
					}
				}
			}
			_progressSpeedLastSampleTime = nowUtc;
			_progressSpeedLastPercent = percent;
			_progressSpeedLastBytes = num;
		}
		if (_progressSmoothedBytesPerSec > 0.01)
		{
			return BuildSpeedEtaSuffix(_progressSmoothedBytesPerSec, percent, num);
		}
		return "";
	}

	private string BuildSpeedEtaSuffix(double bytesPerSecond, double percent, long currentBytes)
	{
		if (bytesPerSecond <= 0.01)
		{
			return "";
		}
		string text = $" {QualcommConsole.Common.SizeFormatter.FormatSpeed(bytesPerSecond)}";
		if (_operationTotalBytes > 0 && percent > 0.001)
		{
			long num;
			if (currentBytes >= 0)
			{
				num = Math.Min(_operationTotalBytes, Math.Max(0L, currentBytes));
			}
			else
			{
				num = (long)((double)_operationTotalBytes * percent / 100.0);
			}
			long num2 = _operationTotalBytes - num;
			if (num2 > 0)
			{
				double seconds = (double)num2 / bytesPerSecond;
				text += " ETA:" + FormatEta(seconds);
			}
		}
		return text;
	}

	private static string FormatEta(double seconds)
	{
		if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0.0)
		{
			return "--";
		}
		int totalSec = (int)Math.Ceiling(seconds);
		if (totalSec < 60)
		{
			return $"{totalSec}s";
		}
		if (totalSec < 3600)
		{
			int m = totalSec / 60;
			int s = totalSec % 60;
			return $"{m}m{s:D2}s";
		}
		int h = totalSec / 3600;
		int rm = (totalSec % 3600) / 60;
		int rs = totalSec % 60;
		return $"{h}h{rm:D2}m{rs:D2}s";
	}

	private static bool DetectUnicodeProgressChars()
	{
		try
		{
			return System.Console.OutputEncoding.CodePage == 65001;
		}
		catch
		{
			return false;
		}
	}

	public PartitionInfo FindPartition(string keyword)
	{
		if (string.IsNullOrWhiteSpace(keyword))
		{
			return null;
		}
		return Partitions?.FirstOrDefault((PartitionInfo p) => p.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) >= 0);
	}

	public string ReadInput(string prompt)
	{
		System.Console.Write($"{prompt} > ");
		return System.Console.ReadLine()?.Trim();
	}

	public bool EnsureConnected()
	{
		if (!IsConnected)
		{
			Log("设备未连接，请先连接设备", ConsoleColor.Red);
			return false;
		}
		return true;
	}

	public bool EnsurePartitions()
	{
		if (!HasPartitions)
		{
			Log("未读取分区表，请先读取分区表", ConsoleColor.Red);
			return false;
		}
		return true;
	}

	public List<Tuple<string, string, int, long>> BuildWriteTasksFromPackage(FlashPackageInfo packageInfo, string baseDir)
	{
		List<Tuple<string, string, int, long>> list = new List<Tuple<string, string, int, long>>();
		if (packageInfo?.Tasks == null)
		{
			return list;
		}
		foreach (FlashTask task in packageInfo.Tasks)
		{
			if (task.Type == TaskType.Program && !string.IsNullOrEmpty(task.Filename))
			{
				string path = (!string.IsNullOrEmpty(task.FilePath)) ? task.FilePath : Path.Combine(baseDir, task.Filename);
				if (File.Exists(path))
				{
					list.Add(Tuple.Create(task.Label, path, task.Lun, task.StartSector));
				}
			}
		}
		return list;
	}

	public PartitionInfo ResolvePartition(string input)
	{
		if (int.TryParse(input, out int result) && result >= 1 && result <= Partitions.Count)
		{
			return Partitions[result - 1];
		}
		PartitionInfo partitionInfo = FindPartition(input);
		if (partitionInfo == null)
		{
			Log($"未找到分区: {input}", ConsoleColor.Red);
		}
		return partitionInfo;
	}

	public List<PartitionInfo> ResolvePartitions(string input)
	{
		List<PartitionInfo> list = new List<PartitionInfo>();
		if (string.IsNullOrWhiteSpace(input))
		{
			return list;
		}
		IEnumerable<string> enumerable = from t in input.Split(new char[4] { ',', '，', ';', '；' }, StringSplitOptions.RemoveEmptyEntries)
			select t.Trim() into t
			where !string.IsNullOrWhiteSpace(t)
			select t;
		foreach (string item in enumerable)
		{
			PartitionInfo partitionInfo = ResolvePartition(item);
			if (partitionInfo != null && !list.Any((PartitionInfo p) => p.Name.Equals(partitionInfo.Name, StringComparison.OrdinalIgnoreCase)))
			{
				list.Add(partitionInfo);
			}
		}
		return list;
	}

	public static long GetPartitionSizeBytes(PartitionInfo partition)
	{
		if (partition == null)
		{
			return 0L;
		}
		if (partition.Size > 0)
		{
			return partition.Size;
		}
		long num = ((partition.SectorSize > 0) ? partition.SectorSize : 512);
		if (partition.NumSectors > 0)
		{
			return partition.NumSectors * num;
		}
		return 0L;
	}

	public List<string> GenerateRawprogramXmlByLun(string outputFolder, IEnumerable<PartitionInfo> partitions)
	{
		List<PartitionInfo> list = (from p in partitions
			where p != null
			orderby p.Lun, p.StartSector
			select p).ToList();
		List<string> list2 = new List<string>();
		if (list.Count == 0)
		{
			return list2;
		}
		foreach (IGrouping<int, PartitionInfo> item in from p in list
			group p by p.Lun into g
			orderby g.Key
			select g)
		{
			int num = (from p in item
				select (p.SectorSize > 0) ? p.SectorSize : 4096 into x
				group x by x into g
				orderby g.Count() descending, g.Key descending
				select g.Key).FirstOrDefault();
			XElement xElement = new XElement("data", new XComment("NOTE: This is an ** Autogenerated file **"), new XComment($"NOTE: Sector size is {num}bytes"));
			foreach (PartitionInfo item2 in item.OrderBy((PartitionInfo p) => p.StartSector))
			{
				long num2 = ((item2.SectorSize > 0) ? item2.SectorSize : num);
				long num3 = ((item2.NumSectors > 0) ? item2.NumSectors : ((item2.Size > 0) ? (item2.Size / num2) : 0));
				long num4 = num3 * num2;
				double num5 = (double)num4 / 1024.0;
				long value = item2.StartSector * num2;
				XElement content = new XElement("program", new XAttribute("SECTOR_SIZE_IN_BYTES", num2.ToString(CultureInfo.InvariantCulture)), new XAttribute("file_sector_offset", "0"), new XAttribute("filename", item2.Name + ".img"), new XAttribute("label", item2.Name), new XAttribute("num_partition_sectors", num3.ToString(CultureInfo.InvariantCulture)), new XAttribute("partofsingleimage", "false"), new XAttribute("physical_partition_number", item2.Lun.ToString(CultureInfo.InvariantCulture)), new XAttribute("readbackverify", "false"), new XAttribute("size_in_KB", num5.ToString("F1", CultureInfo.InvariantCulture)), new XAttribute("sparse", "false"), new XAttribute("start_byte_hex", "0x" + value.ToString("x", CultureInfo.InvariantCulture)), new XAttribute("start_sector", item2.StartSector.ToString(CultureInfo.InvariantCulture)));
				xElement.Add(content);
			}
			string text = Path.Combine(outputFolder, $"rawprogram{item.Key}.xml");
			XDocument xDocument = new XDocument(new XDeclaration("1.0", null, null), xElement);
			xDocument.Save(text);
			list2.Add(text);
		}
		return list2;
	}

	private QualcommService CreateService()
	{
		return new QualcommService(delegate(string msg)
		{
			Log(msg);
		}, delegate(long current, long total)
		{
			if (total > 0)
			{
				ShowProgress("Sahara", (double)current / (double)total * 100.0);
			}
		}, _logDetail);
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			CancelOperation(silent: true);
			Disconnect();
			_disposed = true;
		}
	}
}
