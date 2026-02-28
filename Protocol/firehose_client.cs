using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using WackeEdl.Qualcomm.Common;
using WackeEdl.Qualcomm.Models;

namespace WackeEdl.Qualcomm.Protocol;

public class FirehoseClient : IDisposable
{
	private readonly SerialPortManager _port;

	private readonly Action<string> _log;

	private readonly Action<string> _logDetail;

	private readonly Action<long, long> _progress;

	private bool _disposed;

	private readonly StringBuilder _rxBuffer = new StringBuilder();

	private int _sectorSize = 4096;

	private int _maxPayloadSize = 16777216;

	private int _lastSuccessfulGptStrategy = -1;

	private string _vipSpoofLabel;

	private string _vipSpoofFilenameFormat;

	private const int ACK_TIMEOUT_MS = 15000;

	private const int FILE_BUFFER_SIZE = 4194304;

	private const int OPTIMAL_PAYLOAD_REQUEST = 16777216;

	private int _customChunkSize;

	private Dictionary<int, GptHeaderInfo> _lunHeaders = new Dictionary<int, GptHeaderInfo>();

	private List<PartitionInfo> _cachedPartitions;

	private Stopwatch _transferStopwatch;

	private long _transferTotalBytes;

	private long _lastSpeedBytes;

	private DateTime _lastSpeedTime = DateTime.MinValue;

	private double _currentSpeedMBps;

	private string _mergedSlot = "nonexistent";

	private int _slotACount;

	private int _slotBCount;

	private DateTime _lastReadIoLogUtc = DateTime.MinValue;

	private DateTime _lastWriteIoLogUtc = DateTime.MinValue;

	private static readonly TimeSpan IoDetailLogInterval = TimeSpan.FromMilliseconds(800.0);

	public string StorageType { get; private set; }

	public int SectorSize => _sectorSize;

	public int MaxPayloadSize => _maxPayloadSize;

	public int EffectiveChunkSize
	{
		get
		{
			if (_customChunkSize > 0)
			{
				return Math.Min(_customChunkSize, _maxPayloadSize);
			}
			return _maxPayloadSize;
		}
	}

	public List<string> SupportedFunctions { get; private set; }

	public string ChipSerial { get; set; }

	public string ChipHwId { get; set; }

	public string ChipPkHash { get; set; }

	public string OnePlusProgramToken { get; set; }

	public string OnePlusProgramPk { get; set; }

	public string OnePlusProjId { get; set; }

	public bool IsOnePlusAuthenticated => !string.IsNullOrEmpty(OnePlusProgramToken);

	public double CurrentSpeedMBps => _currentSpeedMBps;

	public bool IsConnected => _port.IsOpen;

	public string VipSpoofLabel => _vipSpoofLabel;

	/// <summary>
	/// VIP 探测成功后记录的欺骗分区名 (如 "BackupGPT" / "PrimaryGPT")
	/// VipMode=true 时所有读写操作强制使用此名称，保证非空
	/// </summary>
	public string VipPartitionName => _vipSpoofLabel;

	/// <summary>
	/// true 时所有扇区读取强制使用 VIP 欺骗模式（new_oplus 连接后设置）
	/// </summary>
	public bool VipMode { get; set; }

	public bool EnableProvision { get; set; }

	public GptParseResult LastGptResult { get; private set; }

	public string CurrentSlot => _mergedSlot;

	public bool IsOplusDevice => _vipSpoofLabel != null;

	public void SetChunkSize(int chunkSize)
	{
		if (chunkSize < 0)
		{
			throw new ArgumentException("分段大小不能为负数");
		}
		if (chunkSize > 0)
		{
			chunkSize = chunkSize / _sectorSize * _sectorSize;
			if (chunkSize < _sectorSize)
			{
				chunkSize = _sectorSize;
			}
			chunkSize = Math.Min(chunkSize, _maxPayloadSize);
		}
		_customChunkSize = chunkSize;
		if (chunkSize == 0)
		{
			_logDetail($"[Firehose] 分段模式: 关闭 (使用设备最大值 {FormatSize(_maxPayloadSize)})");
		}
		else
		{
			_logDetail($"[Firehose] 分段模式: 开启 ({FormatSize(chunkSize)}/块)");
		}
	}

	public void SetChunkSizeMB(int megabytes)
	{
		SetChunkSize(megabytes * 1024 * 1024);
	}

	private static string FormatSize(long bytes)
	{
		return QualcommConsole.Common.SizeFormatter.FormatSize(bytes);
	}

	public long GetLunTotalSectors(int lun)
	{
		if (_lunHeaders.TryGetValue(lun, out var value))
		{
			return (long)(value.AlternateLba + 1);
		}
		return -1L;
	}

	public long ResolveNegativeSector(int lun, long sector)
	{
		if (sector >= 0)
		{
			return sector;
		}
		long lunTotalSectors = GetLunTotalSectors(lun);
		if (lunTotalSectors <= 0)
		{
			_logDetail($"[GPT] 无法解析负扇区: LUN{lun} 总扇区数未知");
			return -1L;
		}
		long num = lunTotalSectors + sector;
		Action<string> logDetail = _logDetail;
		global::_003C_003Ey__InlineArray4<object> buffer = default(global::_003C_003Ey__InlineArray4<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 0) = lun;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 1) = sector;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 2) = num;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 3) = lunTotalSectors;
		logDetail(string.Format("[GPT] 负扇区转换: LUN{0} sector {1} -> {2} (总扇区: {3})", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer, 4)));
		return num;
	}

	public FirehoseClient(SerialPortManager port, Action<string> log = null, Action<long, long> progress = null, Action<string> logDetail = null)
	{
		_port = port;
		_log = log ?? ((Action<string>)delegate
		{
		});
		_logDetail = logDetail ?? ((Action<string>)delegate
		{
		});
		_progress = progress;
		StorageType = "ufs";
		SupportedFunctions = new List<string>();
		ChipSerial = "";
		ChipHwId = "";
		ChipPkHash = "";
	}

	private const double SpeedEmaAlpha = 0.3;

	public void ReportProgress(long current, long total)
	{
		DateTime utcNow = DateTime.UtcNow;
		if (_lastSpeedTime == DateTime.MinValue)
		{
			if (_transferStopwatch != null && _transferStopwatch.Elapsed.TotalSeconds > 0.05 && current > 0)
			{
				double elapsedSeconds = _transferStopwatch.Elapsed.TotalSeconds;
				_currentSpeedMBps = (double)current / elapsedSeconds / 1024.0 / 1024.0;
			}
			_lastSpeedBytes = current;
			_lastSpeedTime = utcNow;
		}
		else
		{
			double totalSeconds = (utcNow - _lastSpeedTime).TotalSeconds;
			if (totalSeconds > 0.05)
			{
				long num = current - _lastSpeedBytes;
				if (num >= 0)
				{
					double instantMBps = (double)num / totalSeconds / 1024.0 / 1024.0;
					if (_currentSpeedMBps <= 0.001)
						_currentSpeedMBps = instantMBps;
					else
						_currentSpeedMBps = _currentSpeedMBps * (1.0 - SpeedEmaAlpha) + instantMBps * SpeedEmaAlpha;
				}
				_lastSpeedBytes = current;
				_lastSpeedTime = utcNow;
			}
		}
		if (_progress != null)
		{
			_progress(current, total);
		}
	}

	public async Task<string> TestVipRwModeAsync(CancellationToken ct)
	{
		_log("[VIP] 开始探测欺骗模式...");
		try
		{
			if (await ProbeVipReadAsync(0, 5L, 31, "BackupGPT", "gpt_backup0.bin", ct, 8000))
			{
				_vipSpoofLabel = "BackupGPT";
				_vipSpoofFilenameFormat = "gpt_backup{0}.bin";
				_log("[VIP] 探测结果: BackupGPT (oplus_gptbackup)");
				await Task.Delay(100, ct);
				PurgeBuffer();
				return _vipSpoofLabel;
			}
		}
		catch (Exception ex)
		{
			_logDetail($"[VIP] BackupGPT 探测异常: {ex.Message}");
		}
		await Task.Delay(200, ct);
		try
		{
			if (await ProbeVipReadAsync(0, 33L, 3, "PrimaryGPT", "gpt_main0.bin", ct, 8000))
			{
				_vipSpoofLabel = "PrimaryGPT";
				_vipSpoofFilenameFormat = "gpt_main{0}.bin";
				_log("[VIP] 探测结果: PrimaryGPT (oplus_gptmain)");
				await Task.Delay(100, ct);
				PurgeBuffer();
				return _vipSpoofLabel;
			}
		}
		catch (Exception ex2)
		{
			_logDetail($"[VIP] PrimaryGPT 探测异常: {ex2.Message}");
		}
		_log("[VIP] 两种欺骗模式均失败，将使用完整策略列表");
		return null;
	}

	private async Task<bool> ProbeVipReadAsync(int lun, long startSector, int numSectors, string label, string filename, CancellationToken ct, int timeoutMs)
	{
		double num = (double)(numSectors * _sectorSize) / 1024.0;
		long num2 = startSector * _sectorSize;
		global::_003C_003Ey__InlineArray8<object> buffer = default(global::_003C_003Ey__InlineArray8<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 0) = _sectorSize;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 1) = filename;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 2) = label;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 3) = numSectors;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 4) = lun;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 5) = num;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 6) = num2;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 7) = startSector;
		string s = string.Format("<?xml version=\"1.0\" ?><data>\n<read SECTOR_SIZE_IN_BYTES=\"{0}\" file_sector_offset=\"0\" filename=\"{1}\" label=\"{2}\" num_partition_sectors=\"{3}\" partofsingleimage=\"true\" physical_partition_number=\"{4}\" readbackverify=\"false\" size_in_KB=\"{5:F1}\" sparse=\"false\" start_byte_hex=\"0x{6:X}\" start_sector=\"{7}\" />\n</data>\n", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray8<object>, object>(in buffer, 8));
		Action<string> logDetail = _logDetail;
		global::_003C_003Ey__InlineArray4<object> buffer2 = default(global::_003C_003Ey__InlineArray4<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 0) = label;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 1) = lun;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 2) = startSector;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 3) = numSectors;
		logDetail(string.Format("[VIP] 探测 {0}: LUN{1} sector {2}+{3}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer2, 4)));
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		byte[] buffer3 = new byte[numSectors * _sectorSize];
		using (CancellationTokenSource timeoutCts = new CancellationTokenSource(timeoutMs))
		{
			using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
			_ = 2;
			try
			{
				Task<bool> receiveTask = ReceiveDataAfterAckAsync(buffer3, linkedCts.Token);
				Task delayTask = Task.Delay(timeoutMs, ct);
				if (await Task.WhenAny(receiveTask, delayTask) == delayTask)
				{
					_logDetail($"[VIP] {label} 探测超时");
					return false;
				}
				if (await receiveTask)
				{
					await WaitForAckAsync(linkedCts.Token, 10);
					_logDetail($"[VIP] {label} 探测成功 ({buffer3.Length} 字节)");
					return true;
				}
			}
			catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
			{
				_logDetail($"[VIP] {label} 探测超时");
			}
			catch (Exception ex2)
			{
				_logDetail($"[VIP] {label} 探测失败: {ex2.Message}");
			}
		}
		return false;
	}

	public string GetVipSpoofFilename(int lun)
	{
		if (_vipSpoofFilenameFormat == null)
		{
			return null;
		}
		return string.Format(_vipSpoofFilenameFormat, lun);
	}

	public void SetVipSpoofMode(string label)
	{
		if (label == "BackupGPT")
		{
			_vipSpoofLabel = "BackupGPT";
			_vipSpoofFilenameFormat = "gpt_backup{0}.bin";
		}
		else if (label == "PrimaryGPT")
		{
			_vipSpoofLabel = "PrimaryGPT";
			_vipSpoofFilenameFormat = "gpt_main{0}.bin";
		}
		else
		{
			_vipSpoofLabel = null;
			_vipSpoofFilenameFormat = null;
		}
	}

	public static List<VipSpoofStrategy> GetDynamicSpoofStrategies(int lun, long startSector, string partitionName, bool isGptRead)
	{
		List<VipSpoofStrategy> list = new List<VipSpoofStrategy>();
		if (isGptRead || startSector <= 33)
		{
			list.Add(new VipSpoofStrategy($"gpt_main{lun}.bin", "PrimaryGPT", 0));
			list.Add(new VipSpoofStrategy("gpt_main0.bin", "PrimaryGPT", 1));
			list.Add(new VipSpoofStrategy($"gpt_backup{lun}.bin", "BackupGPT", 2));
			list.Add(new VipSpoofStrategy("gpt_backup0.bin", "BackupGPT", 3));
		}
		list.Add(new VipSpoofStrategy("gpt_backup0.bin", "BackupGPT", 4));
		if (!string.IsNullOrEmpty(partitionName))
		{
			string text = SanitizePartitionName(partitionName);
			list.Add(new VipSpoofStrategy("gpt_backup0.bin", text, 3));
			list.Add(new VipSpoofStrategy(text + ".bin", text, 4));
		}
		list.Add(new VipSpoofStrategy("ssd", "ssd", 5));
		list.Add(new VipSpoofStrategy("gpt_main0.bin", "gpt_main0.bin", 6));
		list.Add(new VipSpoofStrategy("buffer.bin", "buffer", 8));
		list.Add(new VipSpoofStrategy("", "", 99));
		return list;
	}

	private static string SanitizePartitionName(string name)
	{
		if (string.IsNullOrEmpty(name))
		{
			return "rawdata";
		}
		char[] invalidFileNameChars = Path.GetInvalidFileNameChars();
		StringBuilder stringBuilder = new StringBuilder();
		foreach (char c in name)
		{
			bool flag = true;
			char[] array = invalidFileNameChars;
			foreach (char c2 in array)
			{
				if (c == c2)
				{
					flag = false;
					break;
				}
			}
			if (flag)
			{
				stringBuilder.Append(c);
			}
		}
		string text = stringBuilder.ToString().ToLowerInvariant();
		if (text.Length > 32)
		{
			text = text.Substring(0, 32);
		}
		if (!string.IsNullOrEmpty(text))
		{
			return text;
		}
		return "rawdata";
	}

	public async Task<bool> ConfigureAsync(string storageType = "ufs", int preferredPayloadSize = 0, CancellationToken ct = default(CancellationToken))
	{
		StorageType = storageType.ToLower();
		_sectorSize = ((StorageType == "emmc") ? 512 : 4096);
		int num = ((preferredPayloadSize > 0) ? preferredPayloadSize : 16777216);
		string s = string.Format("<?xml version=\"1.0\" ?><data><configure MemoryName=\"{0}\" Verbose=\"0\" AlwaysValidate=\"0\" MaxPayloadSizeToTargetInBytes=\"{1}\" MaxPayloadSizeFromTargetInBytes=\"{1}\" AckRawDataEveryNumPackets=\"0\" ZlpAwareHost=\"1\" SkipStorageInit=\"0\" /></data>", storageType, num);
		_log($"[Firehose] 配置设备 (存储: {storageType}, 请求Payload: {num / 1024}KB)...");
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		for (int i = 0; i < 5; i++)
		{
			if (ct.IsCancellationRequested)
			{
				return false;
			}
			XElement xElement = await ProcessXmlResponseAsync(ct, 3000);
			if (xElement != null)
			{
				string text = ((xElement.Attribute("value") != null) ? xElement.Attribute("value").Value : "");
				if (text.Equals("ACK", StringComparison.OrdinalIgnoreCase) || text.Equals("NAK", StringComparison.OrdinalIgnoreCase))
				{
					XAttribute xAttribute = xElement.Attribute("SectorSizeInBytes");
					if (xAttribute != null && int.TryParse(xAttribute.Value, out var result))
					{
						_sectorSize = result;
					}
					XAttribute xAttribute2 = xElement.Attribute("MaxPayloadSizeToTargetInBytes");
					if (xAttribute2 != null && int.TryParse(xAttribute2.Value, out var result2) && result2 > 0)
					{
						_maxPayloadSize = Math.Max(65536, Math.Min(result2, 67108864));
					}
					_log($"[Firehose] 配置成功 - 扇区: {_sectorSize}B, Payload: {FormatSize(_maxPayloadSize)}");
					return true;
				}
				if (!string.IsNullOrEmpty(text))
				{
					_log($"[Firehose] 收到非预期响应: {text}");
				}
			}
			else
			{
				_log($"[Firehose] 等待响应超时 ({i + 1}/5)...");
			}
			await Task.Delay(100, ct);
		}
		_log("[Firehose] 配置超时，设备可能不在 Firehose 模式");
		return false;
	}

	public void SetSectorSize(int size)
	{
		_sectorSize = size;
	}

	private async Task<XElement> ProcessXmlResponseAsync(CancellationToken ct, int timeoutMs = 5000, List<string> capturedLogs = null)
	{
		_ = 1;
		try
		{
			StringBuilder sb = new StringBuilder();
			DateTime startTime = DateTime.Now;
			int emptyReads = 0;
			while ((DateTime.Now - startTime).TotalMilliseconds < (double)timeoutMs)
			{
				if (ct.IsCancellationRequested)
				{
					return null;
				}
				int bytesToRead = _port.BytesToRead;
				if (bytesToRead > 0)
				{
					emptyReads = 0;
					byte[] array = new byte[Math.Min(bytesToRead, 65536)];
					int num = _port.Read(array, 0, array.Length);
					if (num <= 0)
					{
						continue;
					}
					sb.Append(Encoding.UTF8.GetString(array, 0, num));
					string text = sb.ToString();
					if (text.Contains("<log "))
					{
						foreach (Match item in Regex.Matches(text, "<log value=\"([^\"]*)\"\\s*/>"))
						{
							if (item.Groups.Count > 1)
							{
								string text2 = item.Groups[1].Value;
								try
								{
									text2 = System.Net.WebUtility.HtmlDecode(text2);
								}
								catch
								{
								}
								_logDetail("[Device] " + text2);
								capturedLogs?.Add(text2);
							}
						}
					}
					if (!text.Contains("</data>") && !text.Contains("<response"))
					{
						continue;
					}
					int num2 = text.IndexOf("<response");
					if (num2 >= 0)
					{
						int num3 = text.IndexOf("/>", num2);
						if (num3 > num2)
						{
							return XElement.Parse(text.Substring(num2, num3 - num2 + 2));
						}
					}
				}
				else
				{
					emptyReads++;
					if (emptyReads < 20)
					{
						Thread.SpinWait(500);
					}
					else if (emptyReads < 100)
					{
						Thread.Yield();
					}
					else if (emptyReads < 500)
					{
						await Task.Yield();
					}
					else
					{
						await Task.Delay(1, ct);
					}
				}
			}
		}
		catch (OperationCanceledException)
		{
			return null;
		}
		catch (Exception ex2)
		{
			_logDetail($"[Firehose] 响应解析异常: {ex2.Message}");
		}
		return null;
	}

	private async Task<bool> WaitForAckAsync(CancellationToken ct, int maxRetries = 50)
	{
		int emptyCount = 0;
		int totalWaitMs = 0;
		for (int i = 0; i < maxRetries; i++)
		{
			if (totalWaitMs >= 30000)
			{
				break;
			}
			if (ct.IsCancellationRequested)
			{
				return false;
			}
			XElement xElement = await ProcessXmlResponseAsync(ct);
			if (xElement != null)
			{
				emptyCount = 0;
				XAttribute xAttribute = xElement.Attribute("value");
				string text = ((xAttribute != null) ? xAttribute.Value : "");
				if (text.Equals("ACK", StringComparison.OrdinalIgnoreCase) || text.Equals("true", StringComparison.OrdinalIgnoreCase))
				{
					return true;
				}
				if (text.Equals("NAK", StringComparison.OrdinalIgnoreCase))
				{
					XAttribute xAttribute2 = xElement.Attribute("error");
					FirehoseErrorHelper.ParseNakError((xAttribute2 != null) ? xAttribute2.Value : xElement.ToString(), out var message, out var suggestion, out var _, out var _);
					_log($"[Firehose] NAK: {message}");
					if (!string.IsNullOrEmpty(suggestion))
					{
						_log($"[Firehose] {suggestion}");
					}
					return false;
				}
			}
			else
			{
				emptyCount++;
				int num;
				if (emptyCount < 50)
				{
					Thread.SpinWait(1000);
					num = 0;
				}
				else if (emptyCount < 200)
				{
					await Task.Yield();
					num = 1;
				}
				else
				{
					await Task.Delay(5, ct);
					num = 5;
				}
				totalWaitMs += num;
			}
		}
		_log("[Firehose] 等待 ACK 超时");
		return false;
	}

	private Task<bool> ReceiveDataAfterAckAsync(byte[] buffer, CancellationToken ct)
	{
		if (buffer == null)
		{
			return Task.FromResult(result: false);
		}
		return ReceiveDataAfterAckAsync(buffer, buffer.Length, ct);
	}

	private async Task<bool> ReceiveDataAfterAckAsync(byte[] buffer, int expectedBytes, CancellationToken ct)
	{
		_ = 3;
		if (buffer == null || expectedBytes <= 0 || expectedBytes > buffer.Length)
		{
			return false;
		}
		try
		{
			int received = 0;
			bool headerFound = false;
			byte[] probeBuf = new byte[262144];
			int probeIdx = 0;
			byte[] rawmodePattern = Encoding.ASCII.GetBytes("rawmode=\"true\"");
			byte[] dataEndPattern = Encoding.ASCII.GetBytes("</data>");
			byte[] nakPattern = Encoding.ASCII.GetBytes("NAK");
			Stopwatch sw = Stopwatch.StartNew();
			while (received < expectedBytes && sw.ElapsedMilliseconds < 30000)
			{
				if (ct.IsCancellationRequested)
				{
					return false;
				}
				if (!headerFound)
				{
					int num = probeBuf.Length - probeIdx;
					if (num <= 0)
					{
						probeIdx = 0;
						num = probeBuf.Length;
					}
					int num2 = await _port.ReadAsync(probeBuf, probeIdx, num, ct).ConfigureAwait(continueOnCapturedContext: false);
					if (num2 <= 0)
					{
						await Task.Delay(1, ct).ConfigureAwait(continueOnCapturedContext: false);
						continue;
					}
					probeIdx += num2;
					int num3 = IndexOfPattern(probeBuf, 0, probeIdx, rawmodePattern);
					if (num3 >= 0)
					{
						int num4 = IndexOfPattern(probeBuf, num3, probeIdx - num3, dataEndPattern);
						if (num4 >= 0)
						{
							headerFound = true;
							int i;
							for (i = num4 + dataEndPattern.Length; i < probeIdx && (probeBuf[i] == 10 || probeBuf[i] == 13 || probeBuf[i] == 32); i++)
							{
							}
							int num5 = probeIdx - i;
							if (num5 > 0)
							{
								int num6 = Math.Min(num5, expectedBytes);
								Buffer.BlockCopy(probeBuf, i, buffer, 0, num6);
								received = num6;
							}
						}
					}
					else if (IndexOfPattern(probeBuf, 0, probeIdx, nakPattern) >= 0)
					{
						try
						{
							string text = Encoding.UTF8.GetString(probeBuf, 0, Math.Min(probeIdx, 2048));
							_logDetail("[Read] NAK 响应: " + text.Replace("\n", " ").Replace("\r", "").Substring(0, Math.Min(text.Length, 500)));
						}
						catch
						{
						}
						return false;
					}
				}
				else
				{
					int count = Math.Min(expectedBytes - received, 8388608);
					int num7 = await _port.ReadAsync(buffer, received, count, ct).ConfigureAwait(continueOnCapturedContext: false);
					if (num7 <= 0)
					{
						await Task.Delay(1, ct).ConfigureAwait(continueOnCapturedContext: false);
					}
					else
					{
						received += num7;
					}
				}
			}
			return received >= expectedBytes;
		}
		catch (OperationCanceledException)
		{
			return false;
		}
		catch (Exception ex2)
		{
			_logDetail("[Read] 高速读取异常: " + ex2.Message);
			return false;
		}
	}

	private static int IndexOfPattern(byte[] data, int start, int length, byte[] pattern)
	{
		if (pattern.Length == 0 || length < pattern.Length)
		{
			return -1;
		}
		int num = start + length - pattern.Length;
		for (int i = start; i <= num; i++)
		{
			bool flag = true;
			for (int j = 0; j < pattern.Length; j++)
			{
				if (data[i + j] != pattern[j])
				{
					flag = false;
					break;
				}
			}
			if (flag)
			{
				return i;
			}
		}
		return -1;
	}

	private async Task<bool> WaitForRawDataModeAsync(CancellationToken ct, int timeoutMs = 5000)
	{
		return await Task.Run(delegate
		{
			try
			{
				byte[] array = new byte[16384];
				int num = 0;
				Stopwatch stopwatch = Stopwatch.StartNew();
				int num2 = 0;
				byte[] pattern = new byte[14]
				{
					114, 97, 119, 109, 111, 100, 101, 61, 34, 116,
					114, 117, 101, 34
				};
				byte[] pattern2 = new byte[7] { 60, 47, 100, 97, 116, 97, 62 };
				byte[] pattern3 = new byte[3] { 78, 65, 75 };
				byte[] pattern4 = new byte[3] { 65, 67, 75 };
				while (stopwatch.ElapsedMilliseconds < timeoutMs)
				{
					if (ct.IsCancellationRequested)
					{
						return false;
					}
					int bytesToRead = _port.BytesToRead;
					if (bytesToRead > 0)
					{
						int num3 = Math.Min(array.Length - num, bytesToRead);
						if (num3 <= 0)
						{
							num = 0;
							num3 = Math.Min(array.Length, bytesToRead);
						}
						int num4 = _port.Read(array, num, num3);
						if (num4 > 0)
						{
							num += num4;
							if (IndexOfPattern(array, 0, num, pattern3) >= 0)
							{
								_logDetail("[Write] 设备拒绝 (NAK)");
								return false;
							}
							bool num5 = IndexOfPattern(array, 0, num, pattern) >= 0;
							bool flag = IndexOfPattern(array, 0, num, pattern4) >= 0;
							bool flag2 = IndexOfPattern(array, 0, num, pattern2) >= 0;
							if ((num5 || flag) && flag2)
							{
								return true;
							}
							num2 = 0;
						}
					}
					else
					{
						num2++;
						if (num2 < 500)
						{
							Thread.SpinWait(50);
						}
						else if (num2 < 2000)
						{
							Thread.Yield();
						}
						else
						{
							Thread.Sleep(0);
						}
					}
				}
				return false;
			}
			catch (Exception ex)
			{
				_logDetail($"[Write] 等待异常: {ex.Message}");
				return false;
			}
		}, ct);
	}

	private void PurgeBuffer()
	{
		_port.DiscardInBuffer();
		_port.DiscardOutBuffer();
		_rxBuffer.Clear();
	}

	private void StartTransferTimer(long totalBytes)
	{
		_transferStopwatch = Stopwatch.StartNew();
		_transferTotalBytes = totalBytes;
		_lastSpeedBytes = 0L;
		_lastSpeedTime = DateTime.MinValue;
		_currentSpeedMBps = 0.0;
	}

	private void StopTransferTimer(string operationName, long bytesTransferred)
	{
		if (_transferStopwatch == null)
		{
			return;
		}
		_transferStopwatch.Stop();
		double totalSeconds = _transferStopwatch.Elapsed.TotalSeconds;
		if (totalSeconds > 0.1 && bytesTransferred > 0)
		{
			double speedBps = (double)bytesTransferred / totalSeconds;
			_log($"[速度] {operationName}: {QualcommConsole.Common.SizeFormatter.FormatSize(bytesTransferred)} 用时 {totalSeconds:F1}s ({QualcommConsole.Common.SizeFormatter.FormatSpeed(speedBps)})");
		}
		_transferStopwatch = null;
	}

	private bool ShouldEmitIoLog(ref DateTime lastLogUtc)
	{
		DateTime utcNow = DateTime.UtcNow;
		if (lastLogUtc == DateTime.MinValue || utcNow - lastLogUtc >= IoDetailLogInterval)
		{
			lastLogUtc = utcNow;
			return true;
		}
		return false;
	}

	private string FormatFileSize(long bytes)
	{
		return QualcommConsole.Common.SizeFormatter.FormatSize(bytes);
	}

	public void Dispose()
	{
		if (!_disposed)
		{
			_disposed = true;
		}
	}

	public async Task<bool> ResetAsync(string mode = "reset", CancellationToken ct = default(CancellationToken))
	{
		_log($"[Firehose] 重启设备 (模式: {mode})");
		string s = $"<?xml version=\"1.0\" ?><data><power verbose=\"0\"  value=\"{mode}\"/></data>";
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		return await WaitForAckAsync(ct);
	}

	/// <summary>
	/// 发送重启命令后立即返回（不等待 ACK），用于快速断开场景。
	/// </summary>
	public bool ResetNoAck(string mode = "reset")
	{
		try
		{
			_log($"[Firehose] 重启设备 (模式: {mode}, no-ack)");
			string s = $"<?xml version=\"1.0\" ?><data><power verbose=\"0\"  value=\"{mode}\"/></data>";
			PurgeBuffer();
			_port.Write(Encoding.UTF8.GetBytes(s));
			return true;
		}
		catch (Exception ex)
		{
			_log($"[Firehose] 发送重启命令失败: {ex.Message}");
			return false;
		}
	}

	public async Task<bool> PowerOffAsync(CancellationToken ct = default(CancellationToken))
	{
		_log("[Firehose] 关机...");
		string s = "<?xml version=\"1.0\"?><data><power value=\"off\"/></data>";
		_port.Write(Encoding.UTF8.GetBytes(s));
		return await WaitForAckAsync(ct);
	}

	public async Task<bool> RebootToEdlAsync(CancellationToken ct = default(CancellationToken))
	{
		_log("[Firehose] 重启到 EDL...");
		string s = "<?xml version=\"1.0\" ?><data><power verbose=\"0\"  value=\"reset_to_edl\"/></data>";
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		return await WaitForAckAsync(ct);
	}

	/// <summary>
	/// 发送重启到 EDL 命令后立即返回（不等待 ACK）。
	/// </summary>
	public bool RebootToEdlNoAck()
	{
		try
		{
			_log("[Firehose] 重启到 EDL... (no-ack)");
			string s = "<?xml version=\"1.0\" ?><data><power verbose=\"0\"  value=\"reset_to_edl\"/></data>";
			PurgeBuffer();
			_port.Write(Encoding.UTF8.GetBytes(s));
			return true;
		}
		catch (Exception ex)
		{
			_log($"[Firehose] 发送重启到 EDL 命令失败: {ex.Message}");
			return false;
		}
	}

	public async Task<bool> SetActiveSlotAsync(string slot, CancellationToken ct = default(CancellationToken))
	{
		slot = slot?.ToLower() ?? "a";
		if (slot != "a" && slot != "b")
		{
			_log("[Firehose] 错误: 槽位必须是 'a' 或 'b'");
			return false;
		}
		_log($"[Firehose] 设置活动 Slot: {slot}");
		string s = $"<?xml version=\"1.0\" ?><data><setactiveslot slot=\"{slot}\" /></data>";
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		if (await WaitForAckAsync(ct, 3))
		{
			_log("[Firehose] setactiveslot 命令成功");
			return true;
		}
		_log("[Firehose] setactiveslot 不支持，使用 patch 方式...");
		return await SetActiveSlotViaPatchAsync(slot, ct);
	}

	private async Task<bool> SetActiveSlotViaPatchAsync(string targetSlot, CancellationToken ct)
	{
		if (_cachedPartitions == null || _cachedPartitions.Count == 0)
		{
			_log("[Firehose] 错误: 没有缓存的分区信息，请先读取分区表");
			return false;
		}
		string[] array = new string[5] { "boot", "dtbo", "vbmeta", "vendor_boot", "init_boot" };
		string[] optionalAbPartitions = new string[8] { "system", "vendor", "product", "odm", "system_ext", "vendor_dlkm", "odm_dlkm", "system_dlkm" };
		string activeSuffix = "_" + targetSlot;
		string inactiveSuffix = ((targetSlot == "a") ? "_b" : "_a");
		int patchCount = 0;
		int failCount = 0;
		string[] array2 = array;
		foreach (string baseName in array2)
		{
			int num = await PatchSlotPairAsync(baseName, activeSuffix, inactiveSuffix, ct);
			if (num > 0)
			{
				patchCount += num;
			}
			else if (num < 0)
			{
				failCount++;
			}
		}
		array2 = optionalAbPartitions;
		foreach (string baseName2 in array2)
		{
			int num2 = await PatchSlotPairAsync(baseName2, activeSuffix, inactiveSuffix, ct, optional: true);
			if (num2 > 0)
			{
				patchCount += num2;
			}
		}
		if (patchCount == 0)
		{
			_log("[Firehose] 未找到任何 A/B 分区");
			return false;
		}
		_log($"[Firehose] 已修改 {patchCount} 个分区属性");
		_log("[Firehose] 正在保存 GPT 更改...");
		bool num3 = await FixGptAsync(-1, growLastPartition: false, ct);
		if (num3)
		{
			_log($"[Firehose] 活动槽位已切换到: {targetSlot}");
		}
		else
		{
			_log("[Firehose] 警告: GPT 修复失败，更改可能未保存");
		}
		return num3 && failCount == 0;
	}

	private async Task<int> PatchSlotPairAsync(string baseName, string activeSuffix, string inactiveSuffix, CancellationToken ct, bool optional = false)
	{
		int count = 0;
		PartitionInfo activePart = _cachedPartitions.Find((PartitionInfo p) => p.Name.Equals(baseName + activeSuffix, StringComparison.OrdinalIgnoreCase));
		if (activePart != null)
		{
			ulong newAttr = SetSlotFlags(activePart.Attributes, true, 3, false, false);
			if (await PatchPartitionAttributesAsync(activePart, newAttr, ct))
			{
				_logDetail($"[Firehose] {activePart.Name}: 已激活 (attr=0x{newAttr:X16})");
				count++;
			}
			else if (!optional)
			{
				_log($"[Firehose] 错误: 无法修改 {activePart.Name} 属性");
				return -1;
			}
		}
		PartitionInfo inactivePart = _cachedPartitions.Find((PartitionInfo p) => p.Name.Equals(baseName + inactiveSuffix, StringComparison.OrdinalIgnoreCase));
		if (inactivePart != null)
		{
			ulong newAttr = SetSlotFlags(inactivePart.Attributes, false, 1);
			if (await PatchPartitionAttributesAsync(inactivePart, newAttr, ct))
			{
				_logDetail($"[Firehose] {inactivePart.Name}: 已停用 (attr=0x{newAttr:X16})");
				count++;
			}
		}
		return count;
	}

	private async Task<bool> PatchPartitionAttributesAsync(PartitionInfo partition, ulong newAttributes, CancellationToken ct)
	{
		byte[] bytes = BitConverter.GetBytes(newAttributes);
		string attrHex = BitConverter.ToString(bytes).Replace("-", "");
		global::_003C_003Ey__InlineArray5<object> buffer = default(global::_003C_003Ey__InlineArray5<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 0) = _sectorSize;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 1) = 48;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 2) = partition.Name;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 3) = partition.Lun;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 4) = attrHex;
		string s = string.Format("<?xml version=\"1.0\" ?><data><patch SECTOR_SIZE_IN_BYTES=\"{0}\" byte_offset=\"{1}\" filename=\"{2}\" physical_partition_number=\"{3}\" size_in_bytes=\"8\" start_sector=\"0\" value=\"{4}\" what=\"attributes\" /></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray5<object>, object>(in buffer, 5));
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		if (await WaitForAckAsync(ct, 3))
		{
			return true;
		}
		if (partition.EntryIndex >= 0)
		{
			long num = partition.GptEntriesStartSector * _sectorSize + partition.EntryIndex * 128 + 48;
			long num2 = num / _sectorSize;
			int num3 = (int)(num % _sectorSize);
			Action<string> logDetail = _logDetail;
			global::_003C_003Ey__InlineArray4<object> buffer2 = default(global::_003C_003Ey__InlineArray4<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 0) = partition.Name;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 1) = partition.EntryIndex;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 2) = num2;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 3) = num3;
			logDetail(string.Format("[Firehose] Patch {0}: Entry#{1}, Sector={2}, Offset={3}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer2, 4)));
			global::_003C_003Ey__InlineArray5<object> buffer3 = default(global::_003C_003Ey__InlineArray5<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer3, 0) = _sectorSize;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer3, 1) = num3;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer3, 2) = partition.Lun;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer3, 3) = num2;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer3, 4) = attrHex;
			string s2 = string.Format("<?xml version=\"1.0\" ?><data><patch SECTOR_SIZE_IN_BYTES=\"{0}\" byte_offset=\"{1}\" filename=\"DISK\" physical_partition_number=\"{2}\" size_in_bytes=\"8\" start_sector=\"{3}\" value=\"{4}\" /></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray5<object>, object>(in buffer3, 5));
			PurgeBuffer();
			_port.Write(Encoding.UTF8.GetBytes(s2));
			if (await WaitForAckAsync(ct, 3))
			{
				return true;
			}
			_logDetail($"[Firehose] 方法 2 失败: {partition.Name}");
		}
		else
		{
			_logDetail($"[Firehose] {partition.Name} 缺少 EntryIndex，跳过精确 patch");
		}
		string s3 = string.Format("<?xml version=\"1.0\" ?><data><setactivepartition name=\"{0}\" slot=\"{1}\" /></data>", partition.Name.TrimEnd('_', 'a', 'b'), partition.Name.EndsWith("_a") ? "a" : "b");
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s3));
		if (await WaitForAckAsync(ct, 2))
		{
			return true;
		}
		_logDetail($"[Firehose] 所有 patch 方法均失败: {partition.Name}");
		return false;
	}

	private ulong SetSlotFlags(ulong attr, bool? active = null, int? priority = null, bool? successful = null, bool? unbootable = null)
	{
		if (priority.HasValue)
		{
			attr &= 0xFFFCFFFFFFFFFFFFuL;
			attr |= (ulong)((long)(priority.Value & 3) << 48);
		}
		if (active.HasValue)
		{
			attr = ((!active.Value) ? (attr & 0xFFFBFFFFFFFFFFFFuL) : (attr | 0x4000000000000L));
		}
		if (successful.HasValue)
		{
			attr = ((!successful.Value) ? (attr & 0xFFF7FFFFFFFFFFFFuL) : (attr | 0x8000000000000L));
		}
		if (unbootable.HasValue)
		{
			attr = ((!unbootable.Value) ? (attr & 0xFFEFFFFFFFFFFFFFuL) : (attr | 0x10000000000000L));
		}
		return attr;
	}

	public bool IsSlotActive(ulong attributes)
	{
		return (attributes & 0x4000000000000L) != 0;
	}

	public int GetSlotPriority(ulong attributes)
	{
		return (int)((attributes >> 48) & 3);
	}

	public async Task<bool> FixGptAsync(int lun = -1, bool growLastPartition = true, CancellationToken ct = default(CancellationToken))
	{
		string arg = ((lun == -1) ? "all" : lun.ToString());
		string arg2 = (growLastPartition ? "1" : "0");
		_log($"[Firehose] 修复 GPT (LUN={arg})...");
		string s = $"<?xml version=\"1.0\" ?><data><fixgpt lun=\"{arg}\" grow_last_partition=\"{arg2}\" /></data>";
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		if (await WaitForAckAsync(ct, 10))
		{
			_log("[Firehose] GPT 修复成功");
			return true;
		}
		_log("[Firehose] GPT 修复失败");
		return false;
	}

	public async Task<bool> SetBootLunAsync(int lun, CancellationToken ct = default(CancellationToken))
	{
		_log($"[Firehose] 设置启动 LUN: {lun}");
		string s = $"<?xml version=\"1.0\" ?><data><setbootablestoragedrive value=\"{lun}\" /></data>";
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		return await WaitForAckAsync(ct);
	}

	public async Task<bool> SendUfsGlobalConfigAsync(byte bNumberLU, byte bBootEnable, byte bDescrAccessEn, byte bInitPowerMode, byte bHighPriorityLUN, byte bSecureRemovalType, byte bInitActiveICCLevel, short wPeriodicRTCUpdate, byte bConfigDescrLock, CancellationToken ct = default(CancellationToken))
	{
		if (!EnableProvision)
		{
			_log("[Provision] 功能已禁用，请先设置 EnableProvision = true");
			return false;
		}
		_log($"[Provision] 发送 UFS 全局配置 (LUN数={bNumberLU}, Boot={bBootEnable})...");
		_003C_003Ey__InlineArray9<object> buffer = default(_003C_003Ey__InlineArray9<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray9<object>, object>(ref buffer, 0) = bNumberLU;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray9<object>, object>(ref buffer, 1) = bBootEnable;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray9<object>, object>(ref buffer, 2) = bDescrAccessEn;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray9<object>, object>(ref buffer, 3) = bInitPowerMode;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray9<object>, object>(ref buffer, 4) = bHighPriorityLUN;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray9<object>, object>(ref buffer, 5) = bSecureRemovalType;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray9<object>, object>(ref buffer, 6) = bInitActiveICCLevel;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray9<object>, object>(ref buffer, 7) = wPeriodicRTCUpdate;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray9<object>, object>(ref buffer, 8) = bConfigDescrLock;
		string s = string.Format("<?xml version=\"1.0\" ?><data><ufs bNumberLU=\"{0}\" bBootEnable=\"{1}\" bDescrAccessEn=\"{2}\" bInitPowerMode=\"{3}\" bHighPriorityLUN=\"{4}\" bSecureRemovalType=\"{5}\" bInitActiveICCLevel=\"{6}\" wPeriodicRTCUpdate=\"{7}\" bConfigDescrLock=\"{8}\" /></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<_003C_003Ey__InlineArray9<object>, object>(in buffer, 9));
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		bool num = await WaitForAckAsync(ct, 30);
		if (num)
		{
			_logDetail("[Provision] 全局配置已发送");
		}
		else
		{
			_log("[Provision] 全局配置发送失败");
		}
		return num;
	}

	public async Task<bool> SendUfsLunConfigAsync(byte luNum, byte bLUEnable, byte bBootLunID, long sizeInKB, byte bDataReliability, byte bLUWriteProtect, byte bMemoryType, byte bLogicalBlockSize, byte bProvisioningType, short wContextCapabilities, CancellationToken ct = default(CancellationToken))
	{
		if (!EnableProvision)
		{
			_log("[Provision] 功能已禁用");
			return false;
		}
		string text = ((sizeInKB >= 1048576) ? $"{(double)sizeInKB / 1048576.0:F1}GB" : $"{sizeInKB / 1024}MB");
		Action<string> logDetail = _logDetail;
		global::_003C_003Ey__InlineArray4<object> buffer = default(global::_003C_003Ey__InlineArray4<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 0) = luNum;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 1) = text;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 2) = bLUEnable;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 3) = bBootLunID;
		logDetail(string.Format("[Provision] 配置 LUN{0}: {1}, 启用={2}, Boot={3}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer, 4)));
		_003C_003Ey__InlineArray10<object> buffer2 = default(_003C_003Ey__InlineArray10<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray10<object>, object>(ref buffer2, 0) = luNum;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray10<object>, object>(ref buffer2, 1) = bLUEnable;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray10<object>, object>(ref buffer2, 2) = bBootLunID;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray10<object>, object>(ref buffer2, 3) = sizeInKB;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray10<object>, object>(ref buffer2, 4) = bDataReliability;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray10<object>, object>(ref buffer2, 5) = bLUWriteProtect;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray10<object>, object>(ref buffer2, 6) = bMemoryType;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray10<object>, object>(ref buffer2, 7) = bLogicalBlockSize;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray10<object>, object>(ref buffer2, 8) = bProvisioningType;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray10<object>, object>(ref buffer2, 9) = wContextCapabilities;
		string s = string.Format("<?xml version=\"1.0\" ?><data><ufs LUNum=\"{0}\" bLUEnable=\"{1}\" bBootLunID=\"{2}\" size_in_kb=\"{3}\" bDataReliability=\"{4}\" bLUWriteProtect=\"{5}\" bMemoryType=\"{6}\" bLogicalBlockSize=\"{7}\" bProvisioningType=\"{8}\" wContextCapabilities=\"{9}\" /></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<_003C_003Ey__InlineArray10<object>, object>(in buffer2, 10));
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		return await WaitForAckAsync(ct, 30);
	}

	public async Task<bool> CommitUfsProvisionAsync(CancellationToken ct = default(CancellationToken))
	{
		if (!EnableProvision)
		{
			_log("[Provision] 功能已禁用，无法提交配置");
			return false;
		}
		_log("[Provision] 提交 UFS 配置 (此操作可能不可逆!)...");
		string s = "<?xml version=\"1.0\" ?><data><ufs commit=\"true\" /></data>";
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		bool num = await WaitForAckAsync(ct, 60);
		if (num)
		{
			_log("[Provision] UFS 配置已提交成功");
		}
		else
		{
			_log("[Provision] UFS 配置提交失败");
		}
		return num;
	}

	public async Task<FirehoseStorageInfo> GetStorageInfoDetailedAsync(CancellationToken ct = default(CancellationToken))
	{
		FirehoseStorageInfo firehoseStorageInfo = new FirehoseStorageInfo();
		_log("[Provision] 读取存储信息...");
		string s = "<?xml version=\"1.0\" ?><data><getstorageinfo physical_partition_number=\"0\" /></data>";
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		DateTime utcNow = DateTime.UtcNow;
		while ((DateTime.UtcNow - utcNow).TotalMilliseconds < 12000.0)
		{
			if (ct.IsCancellationRequested)
			{
				firehoseStorageInfo.ErrorMessage = "canceled";
				break;
			}
			XElement xElement = await ProcessXmlResponseAsync(ct, 2000, firehoseStorageInfo.Logs);
			if (xElement == null)
			{
				continue;
			}
			foreach (XAttribute item in xElement.Attributes())
			{
				if (item != null)
				{
					firehoseStorageInfo.Attributes[item.Name.LocalName] = item.Value;
				}
			}
			string text = ((xElement.Attribute("value") != null) ? xElement.Attribute("value").Value : "");
			firehoseStorageInfo.ResultValue = text;
			if (text.Equals("ACK", StringComparison.OrdinalIgnoreCase) || text.Equals("true", StringComparison.OrdinalIgnoreCase))
			{
				firehoseStorageInfo.Success = true;
				break;
			}
			if (text.Equals("NAK", StringComparison.OrdinalIgnoreCase))
			{
				firehoseStorageInfo.Success = false;
				firehoseStorageInfo.ErrorMessage = ((xElement.Attribute("error") != null) ? xElement.Attribute("error").Value : "NAK");
				break;
			}
		}
		if (!firehoseStorageInfo.Success)
		{
			if (string.IsNullOrEmpty(firehoseStorageInfo.ErrorMessage))
			{
				firehoseStorageInfo.ErrorMessage = "timeout_or_unsupported";
			}
			_logDetail("[Provision] getstorageinfo 命令可能不被支持");
		}
		if (firehoseStorageInfo.Logs.Count > 0)
		{
			_log(string.Format("[Provision] 设备返回存储日志: {0} 行", firehoseStorageInfo.Logs.Count));
			for (int i = 0; i < Math.Min(8, firehoseStorageInfo.Logs.Count); i++)
			{
				_log("  " + firehoseStorageInfo.Logs[i]);
			}
			if (firehoseStorageInfo.Logs.Count > 8)
			{
				_log(string.Format("  ... 还有 {0} 行日志", firehoseStorageInfo.Logs.Count - 8));
			}
		}
		return firehoseStorageInfo;
	}

	public async Task<bool> GetStorageInfoAsync(CancellationToken ct = default(CancellationToken))
	{
		FirehoseStorageInfo firehoseStorageInfo = await GetStorageInfoDetailedAsync(ct);
		if (firehoseStorageInfo == null)
		{
			return false;
		}
		return firehoseStorageInfo.Success;
	}

	public async Task<bool> ApplyPatchAsync(int lun, long startSector, int byteOffset, int sizeInBytes, string value, CancellationToken ct = default(CancellationToken))
	{
		if (string.IsNullOrEmpty(value) || sizeInBytes == 0)
		{
			return true;
		}
		string text;
		if (startSector < 0)
		{
			text = $"NUM_DISK_SECTORS{startSector}.";
			Action<string> logDetail = _logDetail;
			global::_003C_003Ey__InlineArray4<object> buffer = default(global::_003C_003Ey__InlineArray4<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 0) = lun;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 1) = text;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 2) = byteOffset;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 3) = sizeInBytes;
			logDetail(string.Format("[Patch] LUN{0} Sector {1} Offset{2} Size{3}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer, 4)));
		}
		else
		{
			text = startSector.ToString();
			Action<string> logDetail2 = _logDetail;
			global::_003C_003Ey__InlineArray4<object> buffer2 = default(global::_003C_003Ey__InlineArray4<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 0) = lun;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 1) = startSector;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 2) = byteOffset;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 3) = sizeInBytes;
			logDetail2(string.Format("[Patch] LUN{0} Sector{1} Offset{2} Size{3}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer2, 4)));
		}
		_003C_003Ey__InlineArray6<object> buffer3 = default(_003C_003Ey__InlineArray6<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer3, 0) = _sectorSize;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer3, 1) = byteOffset;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer3, 2) = lun;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer3, 3) = sizeInBytes;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer3, 4) = text;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer3, 5) = value;
		string s = string.Format("<?xml version=\"1.0\" ?><data>\n<patch SECTOR_SIZE_IN_BYTES=\"{0}\" byte_offset=\"{1}\" filename=\"DISK\" physical_partition_number=\"{2}\" size_in_bytes=\"{3}\" start_sector=\"{4}\" value=\"{5}\" />\n</data>\n", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<_003C_003Ey__InlineArray6<object>, object>(in buffer3, 6));
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		return await WaitForAckAsync(ct);
	}

	public async Task<int> ApplyPatchXmlAsync(string patchXmlPath, CancellationToken ct = default(CancellationToken))
	{
		if (!File.Exists(patchXmlPath))
		{
			_log($"[Firehose] Patch 文件不存在: {patchXmlPath}");
			return 0;
		}
		_logDetail($"[Firehose] 应用 Patch: {Path.GetFileName(patchXmlPath)}");
		int successCount = 0;
		try
		{
			XElement root = XDocument.Load(patchXmlPath).Root;
			if (root == null)
			{
				return 0;
			}
			foreach (XElement item in root.Elements("patch"))
			{
				if (ct.IsCancellationRequested)
				{
					break;
				}
				string value = item.Attribute("value")?.Value ?? "";
				if (string.IsNullOrEmpty(value))
				{
					continue;
				}
				int lun = 0;
				int.TryParse(item.Attribute("physical_partition_number")?.Value ?? "0", out lun);
				long startSector = 0L;
				string text = item.Attribute("start_sector")?.Value ?? "0";
				if (text.Contains("NUM_DISK_SECTORS"))
				{
					if (text.Contains("-"))
					{
						if (long.TryParse(text.Split('-')[1].TrimEnd('.'), out var result))
						{
							startSector = -result;
						}
					}
					else
					{
						startSector = -1L;
					}
				}
				else if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				{
					long.TryParse(text.Substring(2), NumberStyles.HexNumber, null, out startSector);
				}
				else
				{
					if (text.EndsWith("."))
					{
						text = text.Substring(0, text.Length - 1);
					}
					long.TryParse(text, out startSector);
				}
				int result2 = 0;
				int.TryParse(item.Attribute("byte_offset")?.Value ?? "0", out result2);
				int result3 = 0;
				int.TryParse(item.Attribute("size_in_bytes")?.Value ?? "0", out result3);
				if (result3 != 0)
				{
					if (await ApplyPatchAsync(lun, startSector, result2, result3, value, ct))
					{
						successCount++;
					}
					else
					{
						_logDetail($"[Patch] 失败: LUN{lun} Sector{startSector}");
					}
				}
			}
		}
		catch (Exception ex)
		{
			_log($"[Patch] 应用异常: {ex.Message}");
		}
		_logDetail($"[Patch] {Path.GetFileName(patchXmlPath)} 成功应用 {successCount} 个补丁");
		return successCount;
	}

	public async Task<bool> PingAsync(CancellationToken ct = default(CancellationToken))
	{
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes("<?xml version=\"1.0\" ?><data><nop /></data>"));
		return await WaitForAckAsync(ct, 3);
	}

	public async Task<List<PartitionInfo>> ReadGptPartitionsAsync(bool useVipMode = false, CancellationToken ct = default(CancellationToken), IProgress<int> lunProgress = null)
	{
		List<PartitionInfo> partitions = new List<PartitionInfo>();
		ResetSlotDetection();
		var totalSw = System.Diagnostics.Stopwatch.StartNew();
		_log(string.Format("[GPT] ========== 开始读取分区表 =========="));
		_log(string.Format("[GPT] 模式: {0}, 扇区大小: {1}B, 扫描LUN: 0-5", useVipMode ? "VIP" : "标准", _sectorSize));
		for (int lun = 0; lun < 6; lun++)
		{
			lunProgress?.Report(lun);
			var lunSw = System.Diagnostics.Stopwatch.StartNew();
			byte[] gptData = null;
			int gptSectors = 256;
			if (useVipMode)
			{
				int vipGptSectors = ((_sectorSize == 4096) ? 6 : 34);
				if (lun == 0)
				{
					_log($"[GPT] VIP 模式读取 (扇区大小={_sectorSize}B, {vipGptSectors} 扇区/LUN, {vipGptSectors * _sectorSize / 1024}KB)");
				}
				List<(string label, string filename)> strategies = new List<(string, string)>();
				if (_vipSpoofLabel != null)
				{
					strategies.Add((_vipSpoofLabel, GetVipSpoofFilename(lun)));
				}
				else if (_lastSuccessfulGptStrategy >= 0)
				{
					switch (_lastSuccessfulGptStrategy)
					{
					case 0:
						strategies.Add(("BackupGPT", $"gpt_backup{lun}.bin"));
						break;
					case 1:
						strategies.Add(("BackupGPT", "gpt_backup0.bin"));
						break;
					case 2:
						strategies.Add(("PrimaryGPT", $"gpt_main{lun}.bin"));
						break;
					case 3:
						strategies.Add(("ssd", "ssd"));
						break;
					}
				}
				else
				{
					strategies.Add(("BackupGPT", $"gpt_backup{lun}.bin"));
					strategies.Add(("BackupGPT", "gpt_backup0.bin"));
					strategies.Add(("PrimaryGPT", $"gpt_main{lun}.bin"));
					strategies.Add(("PrimaryGPT", "gpt_main0.bin"));
					strategies.Add(("ssd", "ssd"));
				}
				for (int i = 0; i < strategies.Count; i++)
				{
					try
					{
						if (lun == 0)
						{
							Action<string> logDetail = _logDetail;
							global::_003C_003Ey__InlineArray4<object> buffer = default(global::_003C_003Ey__InlineArray4<object>);
							global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 0) = i + 1;
							global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 1) = strategies.Count;
							global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 2) = strategies[i].label;
							global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 3) = strategies[i].filename;
							logDetail(string.Format("[GPT] 尝试策略 {0}/{1}: {2}/{3}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer, 4)));
						}
						gptData = await ReadGptPacketWithTimeoutAsync(lun, 0L, vipGptSectors, strategies[i].label, strategies[i].filename, ct, 8000);
						if (gptData != null && gptData.Length >= 512)
						{
							if (_lastSuccessfulGptStrategy < 0)
							{
								_lastSuccessfulGptStrategy = i;
								_log($"[GPT] 使用伪装策略: {strategies[i].label}");
							}
							break;
						}
						if (lun == 0)
						{
							Action<string> logDetail2 = _logDetail;
							string item = strategies[i].label;
							byte[] array = gptData;
							logDetail2($"[GPT] 策略 {item} 返回空数据或太小 (len={((array != null) ? array.Length : 0)})");
						}
					}
					catch (TimeoutException)
					{
						_log($"[GPT] LUN{lun} 策略 {strategies[i].label} 超时");
					}
					catch (Exception ex2)
					{
						_log($"[GPT] LUN{lun} 策略 {strategies[i].label} 异常: {ex2.Message}");
					}
					if (i < strategies.Count - 1)
					{
						await Task.Delay(100, ct);
					}
				}
			}
			else
			{
				try
				{
					PurgeBuffer();
					if (lun > 0)
					{
						await Task.Delay(50, ct);
					}
					_logDetail($"[GPT] LUN{lun} 标准模式读取 {gptSectors} 扇区 ({gptSectors * _sectorSize / 1024}KB)...");
					gptData = await ReadSectorsAsync(lun, 0L, gptSectors, ct);
					if (gptData != null)
						_logDetail($"[GPT] LUN{lun} 收到 {gptData.Length} 字节");
				}
				catch (Exception ex3)
				{
					_logDetail($"[GPT] LUN{lun} 读取异常: {ex3.Message}");
				}
			}
			lunSw.Stop();
			if (gptData == null || gptData.Length < 512)
			{
				_logDetail($"[GPT] LUN{lun} 无数据 ({lunSw.ElapsedMilliseconds}ms)");
				continue;
			}
			_logDetail($"[GPT] LUN{lun} 读取完成: {gptData.Length} 字节 ({lunSw.ElapsedMilliseconds}ms)");
			bool flag = false;
			for (int j = 0; j < Math.Min(gptData.Length - 8, 8192); j += 512)
			{
				if (gptData.Length > j + 7 && gptData[j] == 69 && gptData[j + 1] == 70 && gptData[j + 2] == 73 && gptData[j + 3] == 32 && gptData[j + 4] == 80 && gptData[j + 5] == 65 && gptData[j + 6] == 82 && gptData[j + 7] == 84)
				{
					flag = true;
					_logDetail($"[GPT] LUN{lun} 找到 GPT 签名 @ 偏移 {j}");
					break;
				}
			}
			if (!flag)
			{
				_logDetail($"[GPT] LUN{lun} 未找到 GPT 签名 (数据长度={gptData.Length})");
				if (gptData.Length >= 64)
				{
					_logDetail(string.Format("[GPT] LUN{0} 前64字节: {1}", lun, BitConverter.ToString(gptData, 0, 64).Replace("-", " ")));
				}
			}
			List<PartitionInfo> list = ParseGptPartitions(gptData, lun);
			if (list.Count > 0)
			{
				partitions.AddRange(list);
				_log($"[GPT] LUN{lun}: {list.Count} 个分区");
				// 输出该 LUN 的分区列表
				foreach (var p in list)
				{
					_logDetail(string.Format("[GPT]   {0,-30} LBA {1,10} - {2,10}  {3,10}",
						p.Name, p.StartSector, p.StartSector + p.NumSectors - 1, p.FormattedSize));
				}
			}
			else
			{
				_logDetail($"[GPT] LUN{lun} 未解析到分区");
			}
		}
		totalSw.Stop();
		if (partitions.Count > 0)
		{
			_cachedPartitions = partitions;
			// 按 LUN 统计
			var lunGroups = new Dictionary<int, int>();
			foreach (var p in partitions)
			{
				if (!lunGroups.ContainsKey(p.Lun)) lunGroups[p.Lun] = 0;
				lunGroups[p.Lun]++;
			}
			var lunSummary = new System.Text.StringBuilder();
			foreach (var kv in lunGroups)
				lunSummary.AppendFormat(" LUN{0}={1}", kv.Key, kv.Value);
			_log($"[GPT] ========== 分区读取完成 ({totalSw.ElapsedMilliseconds}ms) ==========");
			_log($"[GPT] 共 {partitions.Count} 个分区 ({lunGroups.Count} 个LUN:{lunSummary})");
			if (_mergedSlot != "nonexistent")
			{
				_log($"[GPT] 设备槽位: {_mergedSlot} (A={_slotACount}, B={_slotBCount})");
			}
		}
		else
		{
			_log($"[GPT] ========== 分区读取完成 ({totalSw.ElapsedMilliseconds}ms) - 未找到分区 ==========");
		}
		await Task.Delay(50);
		PurgeBuffer();
		return partitions;
	}

	public async Task<byte[]> ReadGptPacketAsync(int lun, long startSector, int numSectors, string label, string filename, CancellationToken ct)
	{
		return await ReadGptPacketWithTimeoutAsync(lun, startSector, numSectors, label, filename, ct, 30000);
	}

	public async Task<byte[]> ReadGptPacketWithTimeoutAsync(int lun, long startSector, int numSectors, string label, string filename, CancellationToken ct, int timeoutMs = 10000)
	{
		double num = (double)(numSectors * _sectorSize) / 1024.0;
		long num2 = startSector * _sectorSize;
		global::_003C_003Ey__InlineArray8<object> buffer = default(global::_003C_003Ey__InlineArray8<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 0) = _sectorSize;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 1) = filename;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 2) = label;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 3) = numSectors;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 4) = lun;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 5) = num;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 6) = num2;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray8<object>, object>(ref buffer, 7) = startSector;
		string s = string.Format("<?xml version=\"1.0\" ?><data>\n<read SECTOR_SIZE_IN_BYTES=\"{0}\" file_sector_offset=\"0\" filename=\"{1}\" label=\"{2}\" num_partition_sectors=\"{3}\" partofsingleimage=\"true\" physical_partition_number=\"{4}\" readbackverify=\"false\" size_in_KB=\"{5:F1}\" sparse=\"false\" start_byte_hex=\"0x{6:X}\" start_sector=\"{7}\" />\n</data>\n", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray8<object>, object>(in buffer, 8));
		_logDetail($"[GPT] 读取 LUN{lun} (伪装: {label}/{filename})...");
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		byte[] buffer2 = new byte[numSectors * _sectorSize];
		using (CancellationTokenSource timeoutCts = new CancellationTokenSource(timeoutMs))
		{
			using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
			_ = 2;
			try
			{
				Task<bool> receiveTask = ReceiveDataAfterAckAsync(buffer2, linkedCts.Token);
				Task delayTask = Task.Delay(timeoutMs, ct);
				if (await Task.WhenAny(receiveTask, delayTask) == delayTask)
				{
					_logDetail($"[GPT] LUN{lun} 读取超时 ({timeoutMs}ms)");
					throw new TimeoutException($"GPT 读取超时: LUN{lun}");
				}
				if (await receiveTask)
				{
					await WaitForAckAsync(linkedCts.Token, 10);
					_logDetail($"[GPT] LUN{lun} 读取成功 ({buffer2.Length} 字节)");
					return buffer2;
				}
			}
			catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
			{
				_logDetail($"[GPT] LUN{lun} 读取超时 ({timeoutMs}ms)");
				throw new TimeoutException($"GPT 读取超时: LUN{lun}");
			}
			catch (TimeoutException)
			{
				throw;
			}
			catch (Exception ex3)
			{
				_logDetail($"[GPT] LUN{lun} 读取异常: {ex3.Message}");
			}
		}
		_logDetail($"[GPT] LUN{lun} 读取失败");
		return null;
	}

	public void ResetSlotDetection()
	{
		_mergedSlot = "nonexistent";
		_slotACount = 0;
		_slotBCount = 0;
	}

	private void MergeSlotInfo(GptParseResult result)
	{
		if (result?.SlotInfo == null)
		{
			return;
		}
		SlotInfo slotInfo = result.SlotInfo;
		if (slotInfo.HasAbPartitions)
		{
			if (_mergedSlot == "nonexistent")
			{
				_mergedSlot = "undefined";
			}
			if (slotInfo.CurrentSlot == "a")
			{
				_slotACount++;
			}
			else if (slotInfo.CurrentSlot == "b")
			{
				_slotBCount++;
			}
		}
		if (_slotACount > _slotBCount && _slotACount > 0)
		{
			_mergedSlot = "a";
		}
		else if (_slotBCount > _slotACount && _slotBCount > 0)
		{
			_mergedSlot = "b";
		}
		else if (_slotACount > 0 && _slotBCount > 0)
		{
			_mergedSlot = "unknown";
		}
	}

	public List<PartitionInfo> ParseGptPartitions(byte[] gptData, int lun)
	{
		_logDetail($"[GPT] LUN{lun} 开始解析 GPT (数据大小: {gptData.Length} 字节, 当前扇区大小: {_sectorSize}B)");
		GptParseResult gptParseResult = (LastGptResult = new GptParser(_log, _logDetail).Parse(gptData, lun, _sectorSize));
		MergeSlotInfo(gptParseResult);
		if (gptParseResult.Success && gptParseResult.Header != null)
		{
			var h = gptParseResult.Header;
			_lunHeaders[lun] = h;
			if (h.SectorSize > 0 && h.SectorSize != _sectorSize)
			{
				_logDetail($"[GPT] 更新扇区大小: {_sectorSize} -> {h.SectorSize}");
				_sectorSize = h.SectorSize;
			}
			_logDetail($"[GPT] LUN{lun} GPT Header 详情:");
			_logDetail($"[GPT]   类型: {h.GptType ?? "unknown"}, 版本: {h.Revision:X8}, Header大小: {h.HeaderSize}B");
			_logDetail($"[GPT]   磁盘 GUID: {h.DiskGuid}");
			_logDetail($"[GPT]   MyLBA: {h.MyLba}, AlternateLBA: {h.AlternateLba}");
			_logDetail($"[GPT]   可用区域: LBA {h.FirstUsableLba} - {h.LastUsableLba}");
			_logDetail($"[GPT]   分区条目: LBA {h.PartitionEntryLba}, 数量: {h.NumberOfPartitionEntries}, 条目大小: {h.SizeOfPartitionEntry}B");
			_logDetail($"[GPT]   扇区大小: {h.SectorSize}B, CRC: {(h.CrcValid ? "有效" : "无效")}");
			if (gptParseResult.SlotInfo != null && gptParseResult.SlotInfo.HasAbPartitions)
			{
				string method = gptParseResult.SlotInfoV2?.DetectionMethod ?? "";
				_logDetail($"[GPT]   A/B 槽位: {gptParseResult.SlotInfo.CurrentSlot} ({method})");
			}
			_logDetail($"[GPT] LUN{lun} 解析结果: {gptParseResult.Partitions.Count} 个分区");
		}
		else if (!string.IsNullOrEmpty(gptParseResult.ErrorMessage))
		{
			_log($"[GPT] LUN{lun} 解析失败: {gptParseResult.ErrorMessage}");
		}
		return gptParseResult.Partitions;
	}

	public string GenerateRawprogramXml()
	{
		if (_cachedPartitions == null || _cachedPartitions.Count == 0)
		{
			return null;
		}
		return new GptParser(_log, _logDetail).GenerateRawprogramXml(_cachedPartitions, _sectorSize);
	}

	public string GeneratePartitionXml()
	{
		if (_cachedPartitions == null || _cachedPartitions.Count == 0)
		{
			return null;
		}
		return new GptParser(_log, _logDetail).GeneratePartitionXml(_cachedPartitions, _sectorSize);
	}

	public async Task<bool> ReadPartitionAsync(PartitionInfo partition, string savePath, CancellationToken ct = default(CancellationToken), Action<int, int, long> chunkProgress = null)
	{
		return await ReadPartitionChunkedAsync(partition.Lun, partition.StartSector, partition.NumSectors, partition.SectorSize, savePath, partition.Name, ct, chunkProgress);
	}

	public async Task<bool> ReadPartitionChunkedAsync(int lun, long startSector, long numSectors, int sectorSize, string savePath, string label, CancellationToken ct = default(CancellationToken), Action<int, int, long> chunkProgress = null)
	{
		_log($"[Firehose] 读取: {label} ({FormatSize(numSectors * sectorSize)})");
		int effectiveChunkSize = EffectiveChunkSize;
		long sectorsPerChunk = effectiveChunkSize / sectorSize;
		int totalChunks = (int)Math.Ceiling((double)numSectors / (double)sectorsPerChunk);
		long totalSize = numSectors * sectorSize;
		long totalRead = 0L;
		if (_customChunkSize > 0)
		{
			_logDetail($"[Firehose] 分段传输: {FormatSize(effectiveChunkSize)}/块, 共 {totalChunks} 块");
		}
		StartTransferTimer(totalSize);
		string saveDir = Path.GetDirectoryName(savePath);
		if (!string.IsNullOrEmpty(saveDir) && !Directory.Exists(saveDir))
			Directory.CreateDirectory(saveDir);
		using (FileStream fs = new FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 4194304))
		{
			for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
			{
				if (ct.IsCancellationRequested)
				{
					_log("[Firehose] 读取已取消");
					return false;
				}
				long num = chunkIndex * sectorsPerChunk;
				long num2 = Math.Min(sectorsPerChunk, numSectors - num);
				long currentStartSector = startSector + num;
				chunkProgress?.Invoke(chunkIndex + 1, totalChunks, num2 * sectorSize);
				byte[] data = await ReadSectorsAsync(lun, currentStartSector, (int)num2, ct, partitionName: label);
				if (data == null)
				{
					_log($"[Firehose] 读取失败 @ 块 {chunkIndex + 1}/{totalChunks}, sector {currentStartSector}");
					return false;
				}
				await fs.WriteAsync(data, 0, data.Length, ct);
				totalRead += data.Length;
				_progress?.Invoke(totalRead, totalSize);
			}
		}
		StopTransferTimer("读取", totalRead);
		_log($"[Firehose] {label} 读取完成: {FormatSize(totalRead)}");
		return true;
	}

	public async Task<byte[]> ReadPartitionToMemoryAsync(PartitionInfo partition, CancellationToken ct = default(CancellationToken), Action<int, int, long> chunkProgress = null)
	{
		return await ReadToMemoryChunkedAsync(partition.Lun, partition.StartSector, partition.NumSectors, partition.Name, ct, chunkProgress);
	}

	public async Task<byte[]> ReadToMemoryChunkedAsync(int lun, long startSector, long numSectors, string label, CancellationToken ct = default(CancellationToken), Action<int, int, long> chunkProgress = null)
	{
		int effectiveChunkSize = EffectiveChunkSize;
		long sectorsPerChunk = effectiveChunkSize / _sectorSize;
		int totalChunks = (int)Math.Ceiling((double)numSectors / (double)sectorsPerChunk);
		long totalSize = numSectors * _sectorSize;
		using MemoryStream ms = new MemoryStream((int)Math.Min(totalSize, 2147483647L));
		long totalRead = 0L;
		for (int chunkIndex = 0; chunkIndex < totalChunks; chunkIndex++)
		{
			if (ct.IsCancellationRequested)
			{
				return null;
			}
			long num = chunkIndex * sectorsPerChunk;
			long num2 = Math.Min(sectorsPerChunk, numSectors - num);
			long startSector2 = startSector + num;
			chunkProgress?.Invoke(chunkIndex + 1, totalChunks, num2 * _sectorSize);
			byte[] array = await ReadSectorsAsync(lun, startSector2, (int)num2, ct, partitionName: label);
			if (array == null)
			{
				return null;
			}
			ms.Write(array, 0, array.Length);
			totalRead += array.Length;
			_progress?.Invoke(totalRead, totalSize);
		}
		return ms.ToArray();
	}

	public async Task<int> ReadSectorsIntoBufferAsync(int lun, long startSector, int numSectors, byte[] targetBuffer, CancellationToken ct, bool useVipMode = false, string partitionName = null)
	{
		if (targetBuffer == null || numSectors <= 0)
		{
			return -1;
		}
		int expectedBytes;
		try
		{
			expectedBytes = checked(numSectors * _sectorSize);
		}
		catch (OverflowException)
		{
			_logDetail("[Read] 读取长度溢出");
			return -1;
		}
		if (targetBuffer.Length < expectedBytes)
		{
			throw new ArgumentException("targetBuffer 长度不足", "targetBuffer");
		}
		if (useVipMode)
		{
			byte[] array = await ReadSectorsAsync(lun, startSector, numSectors, ct, useVipMode, partitionName).ConfigureAwait(continueOnCapturedContext: false);
			if (array == null || array.Length < expectedBytes)
			{
				return -1;
			}
			Buffer.BlockCopy(array, 0, targetBuffer, 0, expectedBytes);
			return expectedBytes;
		}
		try
		{
			PurgeBuffer();
			double num = (double)(numSectors * _sectorSize) / 1024.0;
			global::_003C_003Ey__InlineArray5<object> buffer = default(global::_003C_003Ey__InlineArray5<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 0) = _sectorSize;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 1) = numSectors;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 2) = lun;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 3) = num;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 4) = startSector;
			string s = string.Format("<?xml version=\"1.0\" ?><data>\n<read SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" size_in_KB=\"{3:F1}\" start_sector=\"{4}\" />\n</data>\n", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray5<object>, object>(in buffer, 5));
			_port.Write(Encoding.UTF8.GetBytes(s));
			if (await ReceiveDataAfterAckAsync(targetBuffer, expectedBytes, ct).ConfigureAwait(continueOnCapturedContext: false))
			{
				await WaitForAckAsync(ct).ConfigureAwait(continueOnCapturedContext: false);
				return expectedBytes;
			}
		}
		catch (Exception ex2)
		{
			_logDetail($"[Read] LUN{lun} 扇区{startSector} 异常: {ex2.Message}");
		}
		return -1;
	}

	public async Task<byte[]> ReadSectorsAsync(int lun, long startSector, int numSectors, CancellationToken ct, bool useVipMode = false, string partitionName = null)
	{
		long bytes = (long)numSectors * (long)_sectorSize;
		// VipMode 属性为 true 时强制走 VIP 欺骗模式（即使调用方未传 useVipMode）
		if (VipMode) useVipMode = true;
		if (useVipMode)
		{
			// VipPartitionName (即 _vipSpoofLabel) 在探测阶段保证非空
			// VipMode=true 时直接使用全局 VipPartitionName，不走动态策略回退
			if (_vipSpoofLabel != null)
			{
				string effectiveLabel = _vipSpoofLabel;
				string effectiveFilename = GetVipSpoofFilename(lun);
				if (ShouldEmitIoLog(ref _lastReadIoLogUtc))
				{
					_logDetail(string.Format("[Read] LUN{0} 扇区{1}+{2} ({3}) VIP={4}", lun, startSector, numSectors, FormatSize(bytes), effectiveLabel));
				}
				return await ReadSectorsWithSpoofAsync(lun, startSector, numSectors, effectiveLabel, effectiveFilename, ct);
			}
			// VipPartitionName 未就绪 — 探测阶段应已保证有值，此处为安全兜底
			_logDetail(string.Format("[Read] LUN{0} 扇区{1}: VipMode 已启用但 VipPartitionName 为空，拒绝读取", lun, startSector));
			return null;
		}
		if (ShouldEmitIoLog(ref _lastReadIoLogUtc))
		{
			Action<string> logDetail3 = _logDetail;
			global::_003C_003Ey__InlineArray4<object> buffer3 = default(global::_003C_003Ey__InlineArray4<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 0) = lun;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 1) = startSector;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 2) = numSectors;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 3) = FormatSize(bytes);
			logDetail3(string.Format("[Read] LUN{0} 扇区{1}+{2} ({3}) 标准模式", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer3, 4)));
		}
		int num = numSectors * _sectorSize;
		byte[] buffer4 = new byte[num];
		int num2 = await ReadSectorsIntoBufferAsync(lun, startSector, numSectors, buffer4, ct, useVipMode: false, partitionName).ConfigureAwait(continueOnCapturedContext: false);
		if (num2 == num)
		{
			return buffer4;
		}
		_logDetail($"[Read] LUN{lun} 扇区{startSector} 读取失败");
		return null;
	}

	private async Task<byte[]> ReadSectorsWithSpoofAsync(int lun, long startSector, int numSectors, string label, string filename, CancellationToken ct)
	{
		PurgeBuffer();
		double num = (double)(numSectors * _sectorSize) / 1024.0;
		string s;
		if (string.IsNullOrEmpty(label))
		{
			global::_003C_003Ey__InlineArray5<object> buffer = default(global::_003C_003Ey__InlineArray5<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 0) = _sectorSize;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 1) = numSectors;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 2) = lun;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 3) = num;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 4) = startSector;
			s = string.Format("<?xml version=\"1.0\" ?><data>\n<read SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" size_in_KB=\"{3:F1}\" start_sector=\"{4}\" />\n</data>\n", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray5<object>, object>(in buffer, 5));
		}
		else
		{
			_003C_003Ey__InlineArray7<object> buffer2 = default(_003C_003Ey__InlineArray7<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer2, 0) = _sectorSize;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer2, 1) = filename;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer2, 2) = label;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer2, 3) = numSectors;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer2, 4) = lun;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer2, 5) = num;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer2, 6) = startSector;
			s = string.Format("<?xml version=\"1.0\" ?><data>\n<read SECTOR_SIZE_IN_BYTES=\"{0}\" filename=\"{1}\" label=\"{2}\" num_partition_sectors=\"{3}\" physical_partition_number=\"{4}\" size_in_KB=\"{5:F1}\" sparse=\"false\" start_sector=\"{6}\" />\n</data>\n", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<_003C_003Ey__InlineArray7<object>, object>(in buffer2, 7));
		}
		_port.Write(Encoding.UTF8.GetBytes(s));
		int num2 = numSectors * _sectorSize;
		byte[] buffer3 = new byte[num2];
		if (await ReceiveDataAfterAckAsync(buffer3, ct))
		{
			await WaitForAckAsync(ct);
			return buffer3;
		}
		return null;
	}

	public async Task<bool> WritePartitionAsync(PartitionInfo partition, string imagePath, bool useOppoMode = false, CancellationToken ct = default(CancellationToken), Action<int, int, long> chunkProgress = null)
	{
		return await WritePartitionChunkedAsync(partition.Lun, partition.StartSector, _sectorSize, imagePath, partition.Name, useOppoMode, ct, chunkProgress);
	}

	public async Task<bool> WritePartitionChunkedAsync(int lun, long startSector, int sectorSize, string imagePath, string label = "Partition", bool useOppoMode = false, CancellationToken ct = default(CancellationToken), Action<int, int, long> chunkProgress = null)
	{
		if (!File.Exists(imagePath))
		{
			throw new FileNotFoundException("镜像文件不存在", imagePath);
		}
		if (SparseStream.IsSparseFile(imagePath))
		{
			return await WriteSparsePartitionSmartAsync(lun, startSector, sectorSize, imagePath, label, useOppoMode, ct);
		}
		long length = new FileInfo(imagePath).Length;
		_log($"[Firehose] 写入: {label} ({FormatSize(length)})");
		int effectiveChunkSize = EffectiveChunkSize;
		long num = effectiveChunkSize / sectorSize;
		long bytesPerChunk = num * sectorSize;
		int totalChunks = (int)Math.Ceiling((double)length / (double)bytesPerChunk);
		if (_customChunkSize > 0)
		{
			_logDetail($"[Firehose] 分段传输: {FormatSize(effectiveChunkSize)}/块, 共 {totalChunks} 块");
		}
		using Stream sourceStream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4194304, FileOptions.SequentialScan);
		long totalBytes = sourceStream.Length;
		long totalWritten = 0L;
		int currentChunk = 0;
		StartTransferTimer(totalBytes);
		byte[] buffer = SimpleBufferPool.Rent((int)bytesPerChunk);
		try
		{
			long currentSector = startSector;
			while (totalWritten < totalBytes)
			{
				if (ct.IsCancellationRequested)
				{
					_log("[Firehose] 写入已取消");
					return false;
				}
				currentChunk++;
				int count = (int)Math.Min(bytesPerChunk, totalBytes - totalWritten);
				int bytesRead = sourceStream.Read(buffer, 0, count);
				if (bytesRead == 0)
				{
					break;
				}
				chunkProgress?.Invoke(currentChunk, totalChunks, bytesRead);
				int num2 = (bytesRead + sectorSize - 1) / sectorSize * sectorSize;
				if (num2 > bytesRead)
				{
					Array.Clear(buffer, bytesRead, num2 - bytesRead);
				}
				int sectorsToWrite = num2 / sectorSize;
				if (!(await WriteSectorsAsync(lun, currentSector, buffer, num2, label, useOppoMode, ct)))
				{
					_log($"[Firehose] 写入失败 @ 块 {currentChunk}/{totalChunks}, sector {currentSector}");
					return false;
				}
				totalWritten += bytesRead;
				currentSector += sectorsToWrite;
				_progress?.Invoke(totalWritten, totalBytes);
			}
			StopTransferTimer("写入", totalWritten);
			_log($"[Firehose] {label} 写入完成: {FormatSize(totalWritten)}");
			return true;
		}
		finally
		{
			SimpleBufferPool.Return(buffer);
		}
	}

	private async Task<bool> WriteSparsePartitionSmartAsync(int lun, long startSector, int sectorSize, string imagePath, string label, bool useOppoMode, CancellationToken ct)
	{
		_logDetail($"[Sparse] 正在解析 {Path.GetFileName(imagePath)}...");
		SparseStream sparse = null;
		long totalExpandedSize = 0L;
		long realDataSize = 0L;
		List<Tuple<long, long>> dataRanges = null;
		try
		{
			await Task.Run(delegate
			{
				sparse = SparseStream.Open(imagePath, _log);
				totalExpandedSize = sparse.Length;
				realDataSize = sparse.GetRealDataSize();
				dataRanges = sparse.GetDataRanges();
			});
			_log($"[Firehose] 写入: {label} ({FormatFileSize(realDataSize)}) [Sparse]");
			_logDetail($"[Sparse] 展开大小: {QualcommConsole.Common.SizeFormatter.FormatSize(totalExpandedSize)}, 实际数据: {QualcommConsole.Common.SizeFormatter.FormatSize(realDataSize)}, 节省: {((realDataSize > 0) ? (1.0 - (double)realDataSize / (double)totalExpandedSize) : 1.0):P1}");
			if (dataRanges == null || dataRanges.Count == 0)
			{
				_logDetail($"[Sparse] 镜像无实际数据，擦除分区 {label}...");
				long numSectors = totalExpandedSize / sectorSize;
				bool num = await EraseSectorsAsync(lun, startSector, numSectors, ct);
				if (num)
				{
					_logDetail($"[Sparse] 分区 {label} 擦除完成 ({QualcommConsole.Common.SizeFormatter.FormatSize(totalExpandedSize)})");
				}
				else
				{
					_log($"[Sparse] 分区 {label} 擦除失败");
				}
				return num;
			}
			int num2 = _maxPayloadSize / sectorSize;
			int bytesPerChunk = num2 * sectorSize;
			long totalWritten = 0L;
			StartTransferTimer(realDataSize);
			byte[] buffer = SimpleBufferPool.Rent(bytesPerChunk);
			try
			{
				foreach (Tuple<long, long> item2 in dataRanges)
				{
					if (ct.IsCancellationRequested)
					{
						return false;
					}
					long item = item2.Item1;
					long rangeSize = item2.Item2;
					long rangeStartSector = startSector + item / sectorSize;
					sparse.Seek(item, SeekOrigin.Begin);
					long rangeWritten = 0L;
					while (rangeWritten < rangeSize)
					{
						if (ct.IsCancellationRequested)
						{
							return false;
						}
						int count = (int)Math.Min(bytesPerChunk, rangeSize - rangeWritten);
						int bytesRead = sparse.Read(buffer, 0, count);
						if (bytesRead == 0)
						{
							break;
						}
						int num3 = (bytesRead + sectorSize - 1) / sectorSize * sectorSize;
						if (num3 > bytesRead)
						{
							Array.Clear(buffer, bytesRead, num3 - bytesRead);
						}
						_ = num3 / sectorSize;
						long currentSector = rangeStartSector + rangeWritten / sectorSize;
						if (!(await WriteSectorsAsync(lun, currentSector, buffer, num3, label, useOppoMode, ct)))
						{
							_log($"[Firehose] 写入失败 @ sector {currentSector}");
							return false;
						}
						rangeWritten += bytesRead;
						totalWritten += bytesRead;
						if (_progress != null)
						{
							_progress(totalWritten, realDataSize);
						}
					}
				}
				StopTransferTimer("写入", totalWritten);
				_logDetail($"[Firehose] {label} 完成: {QualcommConsole.Common.SizeFormatter.FormatSize(totalWritten)} (跳过 {QualcommConsole.Common.SizeFormatter.FormatSize(totalExpandedSize - realDataSize)})");
				return true;
			}
			finally
			{
				SimpleBufferPool.Return(buffer);
			}
		}
		finally
		{
			if (sparse != null)
			{
				try
				{
					sparse.Dispose();
				}
				catch
				{
				}
			}
		}
	}

	private async Task<bool> WriteSectorsAsync(int lun, long startSector, byte[] data, int length, string label, bool useOppoMode, CancellationToken ct)
	{
		int num = length / _sectorSize;
		string text = _vipSpoofLabel ?? label;
		string text2 = ((_vipSpoofLabel != null) ? GetVipSpoofFilename(lun) : label);
		bool flag = _vipSpoofLabel != null;
		if (ShouldEmitIoLog(ref _lastWriteIoLogUtc))
		{
			Action<string> logDetail = _logDetail;
			global::_003C_003Ey__InlineArray5<object> buffer = default(global::_003C_003Ey__InlineArray5<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 0) = lun;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 1) = startSector;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 2) = num;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 3) = FormatSize(length);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer, 4) = (flag ? ("VIP=" + text) : ("label=" + label));
			logDetail(string.Format("[Write] LUN{0} 扇区{1}+{2} ({3}) {4}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray5<object>, object>(in buffer, 5)));
		}
		_003C_003Ey__InlineArray6<object> buffer2 = default(_003C_003Ey__InlineArray6<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer2, 0) = _sectorSize;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer2, 1) = num;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer2, 2) = lun;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer2, 3) = startSector;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer2, 4) = text2;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer2, 5) = text;
		string text3 = string.Format("<?xml version=\"1.0\" ?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" start_sector=\"{3}\" filename=\"{4}\" label=\"{5}\" /></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<_003C_003Ey__InlineArray6<object>, object>(in buffer2, 6));
		_port.DiscardInBuffer();
		await _port.WriteAsync(Encoding.UTF8.GetBytes(text3), 0, text3.Length, ct);
		if (!(await WaitForRawDataModeAsync(ct)))
		{
			_logDetail($"[Write] LUN{lun} 扇区{startSector} Program 命令未确认");
			return false;
		}
		if (!(await _port.WriteAsync(data, 0, length, ct)))
		{
			_logDetail($"[Write] LUN{lun} 扇区{startSector} 数据写入失败");
			return false;
		}
		bool flag2 = await WaitForAckAsync(ct, 10);
		if (ShouldEmitIoLog(ref _lastWriteIoLogUtc) || !flag2)
		{
			Action<string> logDetail2 = _logDetail;
			global::_003C_003Ey__InlineArray4<object> buffer3 = default(global::_003C_003Ey__InlineArray4<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 0) = (flag2 ? "成功" : "失败");
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 1) = lun;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 2) = startSector;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 3) = (flag2 ? "写入成功" : "写入失败");
			logDetail2(string.Format("[Write] {0} LUN{1} 扇区{2} {3}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer3, 4)));
		}
		return flag2;
	}

	public async Task<bool> FlashPartitionFromFileAsync(string partitionName, string filePath, int lun, long startSector, IProgress<double> progress, CancellationToken ct, bool useVipMode = false)
	{
		if (!File.Exists(filePath))
		{
			_log("Firehose: 文件不存在 - " + filePath);
			return false;
		}
		if (SparseStream.IsSparseFile(filePath))
		{
			return await FlashSparsePartitionSmartAsync(partitionName, filePath, lun, startSector, progress, ct, useVipMode);
		}
		using Stream sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4194304, FileOptions.SequentialScan);
		long fileSize = sourceStream.Length;
		int num = (int)Math.Ceiling((double)fileSize / (double)_sectorSize);
		Action<string> log = _log;
		global::_003C_003Ey__InlineArray4<object> buffer = default(global::_003C_003Ey__InlineArray4<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 0) = Path.GetFileName(filePath);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 1) = partitionName;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 2) = FormatFileSize(fileSize);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 3) = (useVipMode ? " [VIP模式]" : "");
		log(string.Format("Firehose: 刷写 {0} -> {1} ({2}){3}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer, 4)));
		if (useVipMode)
		{
			return await FlashPartitionVipModeAsync(partitionName, sourceStream, lun, startSector, num, fileSize, progress, ct);
		}
		string s;
		if (IsOnePlusAuthenticated)
		{
			_003C_003Ey__InlineArray7<object> buffer2 = default(_003C_003Ey__InlineArray7<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer2, 0) = _sectorSize;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer2, 1) = num;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer2, 2) = lun;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer2, 3) = startSector;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer2, 4) = partitionName;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer2, 5) = OnePlusProgramToken;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer2, 6) = OnePlusProgramPk;
			s = string.Format("<?xml version=\"1.0\"?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" start_sector=\"{3}\" filename=\"{4}\" label=\"{4}\" read_back_verify=\"true\" token=\"{5}\" pk=\"{6}\"/></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<_003C_003Ey__InlineArray7<object>, object>(in buffer2, 7));
			_log("[OnePlus] 使用认证令牌写入");
		}
		else
		{
			global::_003C_003Ey__InlineArray5<object> buffer3 = default(global::_003C_003Ey__InlineArray5<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer3, 0) = _sectorSize;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer3, 1) = num;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer3, 2) = lun;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer3, 3) = startSector;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer3, 4) = partitionName;
			s = string.Format("<?xml version=\"1.0\"?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" start_sector=\"{3}\" filename=\"{4}\" label=\"{4}\" read_back_verify=\"true\"/></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray5<object>, object>(in buffer3, 5));
		}
		_port.Write(Encoding.UTF8.GetBytes(s));
		if (!(await WaitForRawDataModeAsync(ct)))
		{
			_log("Firehose: Program 命令被拒绝");
			return false;
		}
		return await SendStreamDataAsync(sourceStream, fileSize, progress, ct);
	}

	public async Task<bool> FlashPartitionWithNegativeSectorAsync(string partitionName, string filePath, int lun, long startSector, IProgress<double> progress, CancellationToken ct)
	{
		if (!File.Exists(filePath))
		{
			_log("Firehose: 文件不存在 - " + filePath);
			return false;
		}
		if (SparseStream.IsSparseFile(filePath))
		{
			_log("Firehose: 负扇区格式不支持 Sparse 镜像");
			return false;
		}
		using Stream sourceStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4194304, FileOptions.SequentialScan);
		long fileSize = sourceStream.Length;
		int num = (int)Math.Ceiling((double)fileSize / (double)_sectorSize);
		string text = ((startSector >= 0) ? startSector.ToString() : $"NUM_DISK_SECTORS{startSector}.");
		Action<string> log = _log;
		global::_003C_003Ey__InlineArray4<object> buffer = default(global::_003C_003Ey__InlineArray4<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 0) = Path.GetFileName(filePath);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 1) = partitionName;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 2) = FormatFileSize(fileSize);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 3) = text;
		log(string.Format("Firehose: 刷写 {0} -> {1} ({2}) @ {3}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer, 4)));
		global::_003C_003Ey__InlineArray5<object> buffer2 = default(global::_003C_003Ey__InlineArray5<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer2, 0) = _sectorSize;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer2, 1) = num;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer2, 2) = lun;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer2, 3) = text;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer2, 4) = partitionName;
		string s = string.Format("<?xml version=\"1.0\"?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" start_sector=\"{3}\" filename=\"{4}\" label=\"{4}\" read_back_verify=\"true\"/></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray5<object>, object>(in buffer2, 5));
		_port.Write(Encoding.UTF8.GetBytes(s));
		if (!(await WaitForRawDataModeAsync(ct)))
		{
			_log("Firehose: Program 命令被拒绝 (负扇区格式)");
			return false;
		}
		return await SendStreamDataAsync(sourceStream, fileSize, progress, ct);
	}

	private async Task<bool> FlashSparsePartitionSmartAsync(string partitionName, string filePath, int lun, long startSector, IProgress<double> progress, CancellationToken ct, bool useVipMode)
	{
		_logDetail($"[Sparse] 正在解析 {Path.GetFileName(filePath)}...");
		SparseStream sparse = null;
		long totalExpandedSize = 0L;
		long realDataSize = 0L;
		List<Tuple<long, long>> dataRanges = null;
		try
		{
			await Task.Run(delegate
			{
				sparse = SparseStream.Open(filePath, _log);
				totalExpandedSize = sparse.Length;
				realDataSize = sparse.GetRealDataSize();
				dataRanges = sparse.GetDataRanges();
			});
			Action<string> log = _log;
			global::_003C_003Ey__InlineArray4<object> buffer = default(global::_003C_003Ey__InlineArray4<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 0) = Path.GetFileName(filePath);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 1) = partitionName;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 2) = FormatFileSize(realDataSize);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 3) = (useVipMode ? " [VIP]" : "");
			log(string.Format("Firehose: 刷写 {0} -> {1} ({2}) [Sparse]{3}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer, 4)));
			_logDetail($"[Sparse] 展开: {QualcommConsole.Common.SizeFormatter.FormatSize(totalExpandedSize)}, 实际数据: {QualcommConsole.Common.SizeFormatter.FormatSize(realDataSize)}, 节省: {((realDataSize > 0) ? (1.0 - (double)realDataSize / (double)totalExpandedSize) : 1.0):P1}");
			if (dataRanges == null || dataRanges.Count == 0)
			{
				_logDetail($"[Sparse] 镜像无实际数据，擦除分区 {partitionName}...");
				long numSectors = totalExpandedSize / _sectorSize;
				bool num = await EraseSectorsAsync(lun, startSector, numSectors, ct).ConfigureAwait(continueOnCapturedContext: false);
				progress?.Report(100.0);
				if (num)
				{
					_logDetail($"[Sparse] 分区 {partitionName} 擦除完成 ({QualcommConsole.Common.SizeFormatter.FormatSize(totalExpandedSize)})");
				}
				else
				{
					_log($"[Sparse] 分区 {partitionName} 擦除失败");
				}
				return num;
			}
			long totalWritten = 0L;
			int rangeIndex = 0;
			foreach (Tuple<long, long> item2 in dataRanges)
			{
				if (ct.IsCancellationRequested)
				{
					return false;
				}
				rangeIndex++;
				long item = item2.Item1;
				long rangeSize = item2.Item2;
				long num2 = startSector + item / _sectorSize;
				int num3 = (int)Math.Ceiling((double)rangeSize / (double)_sectorSize);
				sparse.Seek(item, SeekOrigin.Begin);
				string s;
				if (useVipMode && _vipSpoofLabel != null)
				{
					_003C_003Ey__InlineArray6<object> buffer2 = default(_003C_003Ey__InlineArray6<object>);
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer2, 0) = _sectorSize;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer2, 1) = num3;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer2, 2) = lun;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer2, 3) = num2;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer2, 4) = GetVipSpoofFilename(lun);
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer2, 5) = _vipSpoofLabel;
					s = string.Format("<?xml version=\"1.0\"?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" start_sector=\"{3}\" filename=\"{4}\" label=\"{5}\" read_back_verify=\"true\"/></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<_003C_003Ey__InlineArray6<object>, object>(in buffer2, 6));
				}
				else if (useVipMode)
				{
					global::_003C_003Ey__InlineArray4<object> buffer3 = default(global::_003C_003Ey__InlineArray4<object>);
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 0) = _sectorSize;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 1) = num3;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 2) = lun;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 3) = num2;
					s = string.Format("<?xml version=\"1.0\"?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" start_sector=\"{3}\" filename=\"gpt_backup{2}.bin\" label=\"BackupGPT\" read_back_verify=\"true\"/></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer3, 4));
				}
				else if (IsOnePlusAuthenticated)
				{
					_003C_003Ey__InlineArray7<object> buffer4 = default(_003C_003Ey__InlineArray7<object>);
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer4, 0) = _sectorSize;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer4, 1) = num3;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer4, 2) = lun;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer4, 3) = num2;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer4, 4) = partitionName;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer4, 5) = OnePlusProgramToken;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray7<object>, object>(ref buffer4, 6) = OnePlusProgramPk;
					s = string.Format("<?xml version=\"1.0\"?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" start_sector=\"{3}\" filename=\"{4}\" label=\"{4}\" read_back_verify=\"true\" token=\"{5}\" pk=\"{6}\"/></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<_003C_003Ey__InlineArray7<object>, object>(in buffer4, 7));
				}
				else
				{
					global::_003C_003Ey__InlineArray5<object> buffer5 = default(global::_003C_003Ey__InlineArray5<object>);
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer5, 0) = _sectorSize;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer5, 1) = num3;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer5, 2) = lun;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer5, 3) = num2;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray5<object>, object>(ref buffer5, 4) = partitionName;
					s = string.Format("<?xml version=\"1.0\"?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" start_sector=\"{3}\" filename=\"{4}\" label=\"{4}\" read_back_verify=\"true\"/></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray5<object>, object>(in buffer5, 5));
				}
				byte[] bytes = Encoding.UTF8.GetBytes(s);
				await _port.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(continueOnCapturedContext: false);
				if (!(await WaitForRawDataModeAsync(ct).ConfigureAwait(continueOnCapturedContext: false)))
				{
					_logDetail($"[Sparse] 第 {rangeIndex}/{dataRanges.Count} 段 Program 命令被拒绝");
					return false;
				}
				long sent = 0L;
				int chunkSize = Math.Min(4194304, _maxPayloadSize);
				byte[] buffer6 = new byte[chunkSize];
				DateTime lastProgressTime = DateTime.MinValue;
				while (sent < rangeSize)
				{
					if (ct.IsCancellationRequested)
					{
						return false;
					}
					int count = (int)Math.Min(chunkSize, rangeSize - sent);
					int read = sparse.Read(buffer6, 0, count);
					if (read == 0)
					{
						break;
					}
					int num4 = (read + _sectorSize - 1) / _sectorSize * _sectorSize;
					if (num4 > read)
					{
						Array.Clear(buffer6, read, num4 - read);
					}
					await _port.WriteAsync(buffer6, 0, num4, ct).ConfigureAwait(continueOnCapturedContext: false);
					sent += read;
					totalWritten += read;
					DateTime now = DateTime.Now;
					if (progress != null && realDataSize > 0 && (now - lastProgressTime).TotalMilliseconds > 200.0)
					{
						progress.Report((double)totalWritten * 100.0 / (double)realDataSize);
						lastProgressTime = now;
					}
				}
				if (!(await WaitForAckAsync(ct, 30).ConfigureAwait(continueOnCapturedContext: false)))
				{
					_logDetail($"[Sparse] 第 {rangeIndex}/{dataRanges.Count} 段写入未确认");
					return false;
				}
			}
			_logDetail($"[Sparse] {partitionName} 写入完成: {QualcommConsole.Common.SizeFormatter.FormatSize(totalWritten)} (跳过 {QualcommConsole.Common.SizeFormatter.FormatSize(totalExpandedSize - realDataSize)} 空白)");
			return true;
		}
		finally
		{
			if (sparse != null)
			{
				try
				{
					sparse.Dispose();
				}
				catch
				{
				}
			}
		}
	}

	private async Task<bool> FlashPartitionVipModeAsync(string partitionName, Stream sourceStream, int lun, long startSector, int numSectors, long fileSize, IProgress<double> progress, CancellationToken ct)
	{
		List<(string, string)> list = new List<(string, string)>();
		if (_vipSpoofLabel != null)
		{
			list.Add((_vipSpoofLabel, GetVipSpoofFilename(lun)));
		}
		else
		{
			foreach (VipSpoofStrategy dynamicSpoofStrategy in GetDynamicSpoofStrategies(lun, startSector, partitionName, isGptRead: false))
			{
				string item = (string.IsNullOrEmpty(dynamicSpoofStrategy.Label) ? partitionName : dynamicSpoofStrategy.Label);
				string item2 = (string.IsNullOrEmpty(dynamicSpoofStrategy.Filename) ? partitionName : dynamicSpoofStrategy.Filename);
				list.Add((item, item2));
			}
		}
		foreach (var strategy in list)
		{
			if (ct.IsCancellationRequested)
			{
				break;
			}
			_logDetail($"[Write] {partitionName} 尝试策略: {strategy.Item1}");
			PurgeBuffer();
			_003C_003Ey__InlineArray6<object> buffer = default(_003C_003Ey__InlineArray6<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 0) = _sectorSize;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 1) = numSectors;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 2) = lun;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 3) = startSector;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 4) = strategy.Item2;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 5) = strategy.Item1;
			string s = string.Format("<?xml version=\"1.0\"?><data><program SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" start_sector=\"{3}\" filename=\"{4}\" label=\"{5}\" partofsingleimage=\"true\" read_back_verify=\"true\" sparse=\"false\"/></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<_003C_003Ey__InlineArray6<object>, object>(in buffer, 6));
			_port.Write(Encoding.UTF8.GetBytes(s));
			if (await WaitForRawDataModeAsync(ct))
			{
				_logDetail($"[Write] {partitionName} 策略 {strategy.Item1} 确认，传输数据...");
				sourceStream.Position = 0L;
				if (await SendStreamDataAsync(sourceStream, fileSize, progress, ct))
				{
					_log($"[Write] {partitionName} 写入成功 (VIP={strategy.Item1})");
					return true;
				}
				_logDetail($"[Write] {partitionName} 数据传输失败");
			}
			else
			{
				_logDetail($"[Write] 策略 {strategy.Item1} 未确认");
			}
			await Task.Delay(100, ct);
		}
		_log($"[Write] {partitionName} 所有VIP策略失败");
		return false;
	}

	private async Task<bool> SendStreamDataAsync(Stream stream, long streamSize, IProgress<double> progress, CancellationToken ct)
	{
		long sent = 0L;
		int chunkSize = Math.Min(Math.Max(1048576, EffectiveChunkSize), 16777216);
		chunkSize = Math.Min(chunkSize, Math.Max(_sectorSize, _maxPayloadSize));
		byte[] array = SimpleBufferPool.Rent(chunkSize);
		byte[] array2 = SimpleBufferPool.Rent(chunkSize);
		byte[] currentBuffer = array;
		byte[] nextBuffer = array2;
		double lastPercent = -1.0;
		DateTime lastProgressTime = DateTime.MinValue;
		try
		{
			int num = await stream.ReadAsync(currentBuffer, 0, (int)Math.Min(chunkSize, streamSize), ct).ConfigureAwait(continueOnCapturedContext: false);
			if (num <= 0)
			{
				return await WaitForAckAsync(ct, 60).ConfigureAwait(continueOnCapturedContext: false);
			}
			while (sent < streamSize)
			{
				if (ct.IsCancellationRequested)
				{
					return false;
				}
				long num2 = streamSize - sent - num;
				Task<int> task = null;
				if (num2 > 0)
				{
					int count = (int)Math.Min(chunkSize, num2);
					task = stream.ReadAsync(nextBuffer, 0, count, ct);
				}
				int num3 = num;
				if (num % _sectorSize != 0)
				{
					num3 = (num / _sectorSize + 1) * _sectorSize;
					Array.Clear(currentBuffer, num, num3 - num);
				}
				if (!await _port.WriteAsync(currentBuffer, 0, num3, ct).ConfigureAwait(continueOnCapturedContext: false))
				{
					_log("Firehose: 数据写入失败");
					return false;
				}
				sent += num;
				DateTime now = DateTime.Now;
				double num4 = 100.0 * (double)sent / (double)streamSize;
				if (num4 > lastPercent + 1.0 || (now - lastProgressTime).TotalMilliseconds > 200.0)
				{
					ReportProgress(sent, streamSize);
					progress?.Report(num4);
					lastPercent = num4;
					lastProgressTime = now;
				}
				if (task == null)
				{
					break;
				}
				num = await task.ConfigureAwait(continueOnCapturedContext: false);
				if (num <= 0)
				{
					break;
				}
				byte[] array3 = currentBuffer;
				currentBuffer = nextBuffer;
				nextBuffer = array3;
			}
			ReportProgress(streamSize, streamSize);
			progress?.Report(100.0);
			return await WaitForAckAsync(ct, 60).ConfigureAwait(continueOnCapturedContext: false);
		}
		finally
		{
			SimpleBufferPool.Return(array);
			SimpleBufferPool.Return(array2);
		}
	}

	public async Task<bool> ErasePartitionAsync(PartitionInfo partition, CancellationToken ct = default(CancellationToken), bool useVipMode = false)
	{
		long bytes = partition.NumSectors * _sectorSize;
		Action<string> log = _log;
		_003C_003Ey__InlineArray6<object> buffer = default(_003C_003Ey__InlineArray6<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 0) = partition.Name;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 1) = partition.Lun;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 2) = partition.StartSector;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 3) = partition.NumSectors;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 4) = FormatSize(bytes);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 5) = (useVipMode ? " VIP" : "");
		log(string.Format("[Erase] {0} LUN{1} 扇区{2}+{3} ({4}){5}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<_003C_003Ey__InlineArray6<object>, object>(in buffer, 6)));
		if (useVipMode)
		{
			return await ErasePartitionVipModeAsync(partition, ct);
		}
		global::_003C_003Ey__InlineArray4<object> buffer2 = default(global::_003C_003Ey__InlineArray4<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 0) = _sectorSize;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 1) = partition.NumSectors;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 2) = partition.Lun;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 3) = partition.StartSector;
		string s = string.Format("<?xml version=\"1.0\" ?><data><erase SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" start_sector=\"{3}\" /></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer2, 4));
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		if (await WaitForAckAsync(ct))
		{
			_log($"[Erase] {partition.Name} 擦除完成");
			return true;
		}
		_log($"[Erase] {partition.Name} 擦除失败");
		return false;
	}

	private async Task<bool> ErasePartitionVipModeAsync(PartitionInfo partition, CancellationToken ct)
	{
		List<(string, string)> list = new List<(string, string)>();
		if (_vipSpoofLabel != null)
		{
			_logDetail($"[Erase] {partition.Name} 使用缓存VIP模式: {_vipSpoofLabel}");
			list.Add((_vipSpoofLabel, GetVipSpoofFilename(partition.Lun)));
		}
		else
		{
			foreach (VipSpoofStrategy dynamicSpoofStrategy in GetDynamicSpoofStrategies(partition.Lun, partition.StartSector, partition.Name, isGptRead: false))
			{
				string item = (string.IsNullOrEmpty(dynamicSpoofStrategy.Label) ? partition.Name : dynamicSpoofStrategy.Label);
				string item2 = (string.IsNullOrEmpty(dynamicSpoofStrategy.Filename) ? partition.Name : dynamicSpoofStrategy.Filename);
				list.Add((item, item2));
			}
		}
		foreach (var strategy in list)
		{
			if (!ct.IsCancellationRequested)
			{
				_logDetail($"[Erase] {partition.Name} 尝试策略: {strategy.Item1}");
				PurgeBuffer();
				_003C_003Ey__InlineArray6<object> buffer = default(_003C_003Ey__InlineArray6<object>);
				global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 0) = _sectorSize;
				global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 1) = partition.NumSectors;
				global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 2) = partition.Lun;
				global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 3) = partition.StartSector;
				global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 4) = strategy.Item1;
				global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 5) = strategy.Item2;
				string s = string.Format("<?xml version=\"1.0\" ?><data><erase SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" start_sector=\"{3}\" label=\"{4}\" filename=\"{5}\" /></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<_003C_003Ey__InlineArray6<object>, object>(in buffer, 6));
				_port.Write(Encoding.UTF8.GetBytes(s));
				if (await WaitForAckAsync(ct))
				{
					_log($"[Erase] {partition.Name} 擦除成功 (VIP={strategy.Item1})");
					return true;
				}
				_logDetail($"[Erase] 策略 {strategy.Item1} 失败");
				await Task.Delay(100, ct);
				continue;
			}
			break;
		}
		_log($"[Erase] {partition.Name} 所有VIP策略失败");
		return false;
	}

	public async Task<bool> ErasePartitionAsync(string partitionName, int lun, long startSector, long numSectors, CancellationToken ct, bool useVipMode = false)
	{
		long bytes = numSectors * _sectorSize;
		Action<string> log = _log;
		_003C_003Ey__InlineArray6<object> buffer = default(_003C_003Ey__InlineArray6<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 0) = partitionName;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 1) = lun;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 2) = startSector;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 3) = numSectors;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 4) = FormatSize(bytes);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<_003C_003Ey__InlineArray6<object>, object>(ref buffer, 5) = (useVipMode ? " VIP" : "");
		log(string.Format("[Erase] {0} LUN{1} 扇区{2}+{3} ({4}){5}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<_003C_003Ey__InlineArray6<object>, object>(in buffer, 6)));
		if (useVipMode)
		{
			PartitionInfo partition = new PartitionInfo
			{
				Name = partitionName,
				Lun = lun,
				StartSector = startSector,
				NumSectors = numSectors,
				SectorSize = _sectorSize
			};
			return await ErasePartitionVipModeAsync(partition, ct);
		}
		global::_003C_003Ey__InlineArray4<object> buffer2 = default(global::_003C_003Ey__InlineArray4<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 0) = _sectorSize;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 1) = numSectors;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 2) = lun;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 3) = startSector;
		string s = string.Format("<?xml version=\"1.0\"?><data><erase SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" start_sector=\"{3}\"/></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer2, 4));
		_port.Write(Encoding.UTF8.GetBytes(s));
		bool flag = await WaitForAckAsync(ct, 100);
		_log(flag ? "Firehose: 擦除成功" : "Firehose: 擦除失败");
		return flag;
	}

	public async Task<bool> EraseSectorsAsync(int lun, long startSector, long numSectors, CancellationToken ct)
	{
		global::_003C_003Ey__InlineArray4<object> buffer = default(global::_003C_003Ey__InlineArray4<object>);
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 0) = _sectorSize;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 1) = numSectors;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 2) = lun;
		global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 3) = startSector;
		string s = string.Format("<?xml version=\"1.0\"?><data><erase SECTOR_SIZE_IN_BYTES=\"{0}\" num_partition_sectors=\"{1}\" physical_partition_number=\"{2}\" start_sector=\"{3}\"/></data>", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer, 4));
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		return await WaitForAckAsync(ct, 120);
	}

	public void SetPartitionCache(List<PartitionInfo> partitions)
	{
		_cachedPartitions = partitions;
	}

	public PartitionInfo FindPartition(string name)
	{
		if (_cachedPartitions == null)
		{
			return null;
		}
		foreach (PartitionInfo cachedPartition in _cachedPartitions)
		{
			if (cachedPartition.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
			{
				return cachedPartition;
			}
		}
		return null;
	}

	public async Task<string> SendRawXmlAsync(string xml, CancellationToken ct)
	{
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(xml));
		StringBuilder sb = new StringBuilder();
		int emptyCount = 0;
		int totalWaitMs = 0;
		for (int i = 0; i < 200; i++)
		{
			if (totalWaitMs >= 10000)
			{
				break;
			}
			if (ct.IsCancellationRequested)
			{
				return null;
			}
			int bytesToRead = _port.BytesToRead;
			if (bytesToRead > 0)
			{
				byte[] array = new byte[Math.Min(bytesToRead, 65536)];
				int num = _port.Read(array, 0, array.Length);
				if (num > 0)
				{
					sb.Append(Encoding.UTF8.GetString(array, 0, num));
					emptyCount = 0;
					string text = sb.ToString();
					if (text.Contains("</data>") || text.Contains("<response"))
					{
						_logDetail(string.Format("[RawXml] 响应: {0}", (text.Length > 500) ? (text.Substring(0, 500) + "...") : text));
						return text;
					}
				}
			}
			else
			{
				emptyCount++;
				if (emptyCount < 50)
				{
					Thread.SpinWait(500);
				}
				else if (emptyCount < 150)
				{
					await Task.Yield();
					totalWaitMs++;
				}
				else
				{
					await Task.Delay(10, ct);
					totalWaitMs += 10;
				}
			}
		}
		string text2 = sb.ToString();
		if (text2.Length > 0)
		{
			_logDetail(string.Format("[RawXml] 部分响应: {0}", (text2.Length > 500) ? (text2.Substring(0, 500) + "...") : text2));
			return text2;
		}
		return null;
	}

	public async Task<string> SendRawBytesAndGetResponseAsync(byte[] data, CancellationToken ct)
	{
		PurgeBuffer();
		_port.Write(data, 0, data.Length);
		StringBuilder sb = new StringBuilder();
		int emptyCount = 0;
		int totalWaitMs = 0;
		for (int i = 0; i < 300; i++)
		{
			if (totalWaitMs >= 15000)
			{
				break;
			}
			if (ct.IsCancellationRequested)
			{
				return null;
			}
			int bytesToRead = _port.BytesToRead;
			if (bytesToRead > 0)
			{
				byte[] array = new byte[Math.Min(bytesToRead, 65536)];
				int num = _port.Read(array, 0, array.Length);
				if (num > 0)
				{
					sb.Append(Encoding.UTF8.GetString(array, 0, num));
					emptyCount = 0;
					string text = sb.ToString();
					if (text.Contains("</data>") || text.Contains("<response") || text.Contains("ACK") || text.Contains("NAK"))
					{
						_logDetail(string.Format("[RawBytes] 响应: {0}", (text.Length > 500) ? (text.Substring(0, 500) + "...") : text));
						return text;
					}
				}
			}
			else
			{
				emptyCount++;
				if (emptyCount < 50)
				{
					Thread.SpinWait(500);
				}
				else if (emptyCount < 150)
				{
					await Task.Yield();
					totalWaitMs++;
				}
				else
				{
					await Task.Delay(10, ct);
					totalWaitMs += 10;
				}
			}
		}
		string text2 = sb.ToString();
		if (text2.Length > 0)
		{
			_logDetail(string.Format("[RawBytes] 部分响应: {0}", (text2.Length > 500) ? (text2.Substring(0, 500) + "...") : text2));
			return text2;
		}
		return null;
	}

	public async Task<bool> PerformVipAuthAsync(string digestPath, string signaturePath, CancellationToken ct)
	{
		if (!File.Exists(digestPath))
		{
			_log($"[VIP Auth] Digest 文件不存在: {digestPath}");
			return false;
		}
		if (!File.Exists(signaturePath))
		{
			_log($"[VIP Auth] Signature 文件不存在: {signaturePath}");
			return false;
		}
		byte[] digestData = File.ReadAllBytes(digestPath);
		byte[] signatureData = File.ReadAllBytes(signaturePath);
		return await PerformVipAuthAsync(digestData, signatureData, ct);
	}

	public async Task<bool> PerformVipAuthAsync(byte[] digestData, byte[] signatureData, CancellationToken ct)
	{
		if (digestData == null || digestData.Length == 0)
		{
			_log("[VIP Auth] Digest 数据为空");
			return false;
		}
		if (signatureData == null || signatureData.Length == 0)
		{
			_log("[VIP Auth] Signature 数据为空");
			return false;
		}
		_logDetail($"[VIP Auth] Digest: {digestData.Length} 字节, Signature: {signatureData.Length} 字节");
		try
		{
			_logDetail("[VIP Auth] Step 1/4: 发送 Digest...");
			string text = await SendRawBytesAndGetResponseAsync(digestData, ct);
			if (text != null && text.Contains("NAK"))
			{
				_log("[VIP Auth] Digest 被拒绝 (NAK)");
				return false;
			}
			_logDetail("[VIP Auth] Step 2/4: 发送 Verify...");
			string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?><data><verify value=\"ping\" EnableVip=\"1\"/></data>";
			string resp = await SendRawXmlAsync(xml, ct);
			_logDetail($"[VIP Auth] Step 2 响应: {TruncateResp(resp)}");
			_logDetail("[VIP Auth] Step 3/4: 发送 Signature...");
			string text2 = await SendRawBytesAndGetResponseAsync(signatureData, ct);
			if (text2 != null && text2.Contains("NAK"))
			{
				_log("[VIP Auth] Signature 被拒绝 (NAK)");
				return false;
			}
			_logDetail("[VIP Auth] Step 4/4: 发送 SHA256Init...");
			string xml2 = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?><data><sha256init Verbose=\"1\"/></data>";
			string resp2 = await SendRawXmlAsync(xml2, ct);
			_logDetail($"[VIP Auth] Step 4 响应: {TruncateResp(resp2)}");
			_logDetail("[VIP Auth] 认证流程完成");
			return true;
		}
		catch (OperationCanceledException)
		{
			throw;
		}
		catch (Exception ex2)
		{
			_log($"[VIP Auth] 认证异常: {ex2.Message}");
			return false;
		}
	}

	public async Task<bool> SendVipDigestAsync(byte[] digestData, CancellationToken ct)
	{
		if (digestData == null || digestData.Length == 0)
		{
			return false;
		}
		_logDetail($"[VIP Auth] 发送 Digest ({digestData.Length} 字节)...");
		string text = await SendRawBytesAndGetResponseAsync(digestData, ct);
		if (text != null && text.Contains("NAK"))
		{
			_log("[VIP Auth] Digest 被拒绝");
			return false;
		}
		return true;
	}

	public async Task<bool> PrepareVipModeAsync(CancellationToken ct)
	{
		_logDetail("[VIP Auth] 发送 Verify (EnableVip=1)...");
		string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?><data><verify value=\"ping\" EnableVip=\"1\"/></data>";
		string resp = await SendRawXmlAsync(xml, ct);
		_logDetail($"[VIP Auth] Verify 响应: {TruncateResp(resp)}");
		return true;
	}

	public async Task<bool> SendVipSignatureAsync(byte[] signatureData, CancellationToken ct)
	{
		if (signatureData == null || signatureData.Length == 0)
		{
			return false;
		}
		_logDetail($"[VIP Auth] 发送 Signature ({signatureData.Length} 字节)...");
		string text = await SendRawBytesAndGetResponseAsync(signatureData, ct);
		if (text != null && text.Contains("NAK"))
		{
			_log("[VIP Auth] Signature 被拒绝");
			return false;
		}
		return true;
	}

	public async Task<bool> FinalizeVipAuthAsync(CancellationToken ct)
	{
		_logDetail("[VIP Auth] 发送 SHA256Init...");
		string xml = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?><data><sha256init Verbose=\"1\"/></data>";
		string text = await SendRawXmlAsync(xml, ct);
		_logDetail($"[VIP Auth] SHA256Init 响应: {TruncateResp(text)}");
		return text == null || !text.Contains("NAK");
	}

	public async Task<string> GetVipChallengeAsync(CancellationToken ct)
	{
		_logDetail("[VIP] 获取设备挑战码...");
		string xml = "<?xml version=\"1.0\" ?><data><getvipchallenge /></data>";
		string text = await SendRawXmlAsync(xml, ct);
		if (string.IsNullOrEmpty(text))
		{
			return null;
		}
		Match match = Regex.Match(text, "challenge=\"([^\"]+)\"", RegexOptions.IgnoreCase);
		if (match.Success)
		{
			string value = match.Groups[1].Value;
			_logDetail($"[VIP] Challenge: {value}");
			return value;
		}
		return text;
	}

	public async Task<bool> Sha256InitAsync(CancellationToken ct)
	{
		_logDetail("[SHA256] 初始化...");
		string s = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?><data><sha256init Verbose=\"1\"/></data>";
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		return await WaitForAckAsync(ct, 10);
	}

	public async Task<bool> Sha256FinalAsync(CancellationToken ct)
	{
		_logDetail("[SHA256] 结束...");
		string s = "<?xml version=\"1.0\" encoding=\"UTF-8\" ?><data><sha256final Verbose=\"1\"/></data>";
		PurgeBuffer();
		_port.Write(Encoding.UTF8.GetBytes(s));
		return await WaitForAckAsync(ct, 10);
	}

	private static string TruncateResp(string resp)
	{
		if (string.IsNullOrEmpty(resp))
		{
			return "(空)";
		}
		if (resp.Length <= 200)
		{
			return resp;
		}
		return resp.Substring(0, 200) + "...";
	}
}
