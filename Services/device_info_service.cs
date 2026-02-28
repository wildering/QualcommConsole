using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WackeEdl.Qualcomm.Common;
using WackeEdl.Qualcomm.Database;

namespace WackeEdl.Qualcomm.Services;

public class DeviceInfoService
{
	public delegate byte[] DeviceReadDelegate(long offsetInSuper, int size);

	private const uint LP_METADATA_GEOMETRY_MAGIC = 1634485351u;

	private const uint LP_METADATA_HEADER_MAGIC = 1097336112u;

	private const uint LP_METADATA_HEADER_MAGIC_ALP0 = 1095520304u;

	private const ushort EXT4_MAGIC = 61267;

	private const uint EROFS_MAGIC = 3774210530u;

	private readonly Action<string> _log;

	private readonly Action<string> _logDetail;

	private readonly SemaphoreSlim _singleFlight = new SemaphoreSlim(1, 1);

	private readonly SemaphoreSlim _ioGate = new SemaphoreSlim(1, 1);

	public DeviceInfoService(Action<string> log = null, Action<string> logDetail = null)
	{
		_log = log ?? ((Action<string>)delegate
		{
		});
		_logDetail = logDetail ?? ((Action<string>)delegate
		{
		});
	}

	private void SafeLog(string message)
	{
		try
		{
			_log(message);
		}
		catch
		{
		}
	}

	private void SafeLogDetail(string message)
	{
		try
		{
			_logDetail(message);
		}
		catch
		{
		}
	}

	public BuildPropInfo ParseBuildPropFile(string filePath)
	{
		try
		{
			if (!File.Exists(filePath))
			{
				_log("文件不存在: " + filePath);
				return null;
			}
			string content = File.ReadAllText(filePath, Encoding.UTF8);
			return ParseBuildProp(content);
		}
		catch (Exception ex)
		{
			_log("解析 build.prop 失败: " + ex.Message);
			return null;
		}
	}

	public BuildPropInfo ParseBuildProp(string content)
	{
		BuildPropInfo buildPropInfo = new BuildPropInfo();
		if (string.IsNullOrEmpty(content))
		{
			return buildPropInfo;
		}
		string[] array;
		if (content.Contains("\0"))
		{
			List<string> list = new List<string>();
			foreach (Match item in Regex.Matches(content, "(ro|display|persist)\\.[a-zA-Z0-9._-]+=[^\\r\\n\\x00\\s]+"))
			{
				list.Add(item.Value);
			}
			foreach (Match item2 in Regex.Matches(content, "(separate\\.soft|region|date\\.utc|ro\\.build\\.oplus_nv_id|display\\.id\\.show|ro\\.lenovo\\.series|ro\\.lenovo\\.cpuinfo|ro\\.system_ext\\.build\\.version\\.incremental|ro\\.zui\\.version|ro\\.miui\\.ui\\.version\\.name|ro\\.miui\\.ui\\.version\\.code|ro\\.miui\\.region|ro\\.build\\.MiFavor_version|ro\\.build\\.display\\.id)=[^\\r\\n\\x00\\s]+"))
			{
				list.Add(item2.Value);
			}
			array = list.ToArray();
		}
		else
		{
			array = content.Split(new char[2] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
		}
		string[] array2 = array;
		for (int i = 0; i < array2.Length; i++)
		{
			string text = array2[i].Trim();
			if (string.IsNullOrEmpty(text) || text.StartsWith("#"))
			{
				continue;
			}
			int num = text.IndexOf('=');
			if (num <= 0)
			{
				continue;
			}
			string text2 = text.Substring(0, num).Trim();
			string text3 = text.Substring(num + 1).Trim();
			if (text3.Length > 0 && (text3[text3.Length - 1] < ' ' || text3[text3.Length - 1] > '~'))
			{
				text3 = text3.TrimEnd('\0', '\r', '\n', '\t', ' ');
			}
			if (string.IsNullOrEmpty(text3))
			{
				continue;
			}
			buildPropInfo.AllProperties[text2] = text3;
			switch (text2)
			{
			case "ro.product.vendor.brand":
			case "ro.product.manufacturer":
			case "ro.product.brand":
				if (string.IsNullOrEmpty(buildPropInfo.Brand) || text3 != "oplus")
				{
					buildPropInfo.Brand = text3;
				}
				break;
			case "ro.vendor.oplus.market.name":
				buildPropInfo.MarketName = text3;
				break;
			case "ro.vendor.oplus.market.enname":
				if (string.IsNullOrEmpty(buildPropInfo.MarketName))
				{
					buildPropInfo.MarketName = text3;
				}
				break;
			case "ro.product.marketname":
			case "ro.product.vendor.marketname":
			case "ro.product.odm.marketname":
				if (string.IsNullOrEmpty(buildPropInfo.MarketName))
				{
					buildPropInfo.MarketName = text3;
				}
				break;
			case "ro.product.vendor.model":
			case "ro.lenovo.series":
			case "ro.product.model":
			case "ro.product.odm.model":
			case "ro.product.odm.cert":
				if (string.IsNullOrEmpty(buildPropInfo.Model) || text3.Length > buildPropInfo.Model.Length || text2 == "ro.lenovo.series")
				{
					if (text3.Contains("Y700") || text3.Contains("Legion"))
					{
						buildPropInfo.MarketName = text3;
					}
					else
					{
						buildPropInfo.Model = text3;
					}
				}
				break;
			case "ro.miui.ui.version.name":
				if (string.IsNullOrEmpty(buildPropInfo.OtaVersion))
				{
					buildPropInfo.OtaVersion = text3;
				}
				if (text3.Contains("OS3."))
				{
					buildPropInfo.AndroidVersion = "16.0";
				}
				else if (text3.Contains("OS2."))
				{
					buildPropInfo.AndroidVersion = "15.0";
				}
				else if (text3.Contains("OS1."))
				{
					buildPropInfo.AndroidVersion = "14.0";
				}
				break;
			case "ro.miui.ui.version.code":
				if (string.IsNullOrEmpty(buildPropInfo.OtaVersion))
				{
					buildPropInfo.OtaVersion = text3;
				}
				break;
			case "ro.build.version.incremental":
			case "ro.system.build.version.incremental":
			case "ro.vendor.build.version.incremental":
				if (string.IsNullOrEmpty(buildPropInfo.Incremental))
				{
					buildPropInfo.Incremental = text3;
				}
				if (string.IsNullOrEmpty(text3))
				{
					break;
				}
				if (text3.StartsWith("V") || text3.StartsWith("OS"))
				{
					if (string.IsNullOrEmpty(buildPropInfo.OtaVersion) || buildPropInfo.OtaVersion.Length < text3.Length)
					{
						buildPropInfo.OtaVersion = text3;
					}
				}
				else if (text3.Contains(".") && text3.Length > 8 && string.IsNullOrEmpty(buildPropInfo.OtaVersion))
				{
					buildPropInfo.OtaVersion = text3;
				}
				break;
			case "ro.build.MiFavor_version":
				if (string.IsNullOrEmpty(buildPropInfo.OtaVersion))
				{
					buildPropInfo.OtaVersion = text3;
				}
				break;
			case "ro.build.display.id.show":
				buildPropInfo.OtaVersion = text3;
				buildPropInfo.OtaVersionFull = text3;
				break;
			case "ro.build.display.full_id":
				buildPropInfo.OtaVersionFull = text3;
				if (string.IsNullOrEmpty(buildPropInfo.OtaVersion))
				{
					Match match3 = Regex.Match(text3, "(\\d+\\.\\d+\\.\\d+\\.\\d+)\\(([A-Z]{2}\\d+)\\)");
					if (match3.Success)
					{
						buildPropInfo.OtaVersion = $"{match3.Groups[1].Value}({match3.Groups[2].Value})";
					}
				}
				break;
			case "ro.build.version.ota":
				if (string.IsNullOrEmpty(buildPropInfo.OtaVersionFull))
				{
					buildPropInfo.OtaVersionFull = text3;
				}
				break;
			case "ro.build.display.id":
			case "ro.system_ext.build.version.incremental":
			case "ro.vendor.build.display.id":
				if (string.IsNullOrEmpty(buildPropInfo.DisplayId) && text2 == "ro.build.display.id")
				{
					buildPropInfo.DisplayId = text3;
				}
				if (!string.IsNullOrEmpty(buildPropInfo.OtaVersion) && buildPropInfo.OtaVersion.Contains("("))
				{
					break;
				}
				if (text3.Contains("ZUI") || text3.Contains("ZUXOS"))
				{
					buildPropInfo.OtaVersionFull = text3;
					Match match4 = Regex.Match(text3, "\\d+\\.\\d+\\.\\d+\\.\\d+");
					if (match4.Success)
					{
						buildPropInfo.OtaVersion = match4.Value;
					}
				}
				else if (text3.Contains("RedMagic") || text3.Contains("Nebula"))
				{
					buildPropInfo.OtaVersion = text3;
				}
				else if (text3.Contains("(") && text3.Contains(")") && (text3.Contains("CN") || text3.Contains("GL") || text3.Contains("EU") || text3.Contains("IN")))
				{
					buildPropInfo.OtaVersionFull = text3;
					buildPropInfo.OtaVersion = text3;
				}
				else if (text3.StartsWith("V") || text3.StartsWith("OS"))
				{
					if (string.IsNullOrEmpty(buildPropInfo.OtaVersion) || buildPropInfo.OtaVersion.Length < text3.Length)
					{
						buildPropInfo.OtaVersion = text3;
					}
				}
				else if (string.IsNullOrEmpty(buildPropInfo.OtaVersion))
				{
					buildPropInfo.OtaVersion = text3;
				}
				break;
			case "ro.vendor.oplus.ota.version":
			case "ro.oem.version":
				if (!string.IsNullOrEmpty(text3))
				{
					buildPropInfo.OtaVersionFull = text3;
					if (string.IsNullOrEmpty(buildPropInfo.OtaVersion) || !buildPropInfo.OtaVersion.Contains("."))
					{
						buildPropInfo.OtaVersion = text3;
					}
				}
				break;
			case "display.id.show":
			case "region":
				if (text2 == "display.id.show" || text2 == "region" || string.IsNullOrEmpty(buildPropInfo.OtaVersion))
				{
					if (text2 == "display.id.show" && text3.Contains("(") && text3.Contains(")"))
					{
						buildPropInfo.OtaVersion = text3;
					}
					else if (string.IsNullOrEmpty(buildPropInfo.OtaVersion))
					{
						buildPropInfo.OtaVersion = text3;
					}
				}
				break;
			case "ro.build.oplus_nv_id":
				buildPropInfo.OplusNvId = text3;
				break;
			case "ro.oplus.image.my_product.type":
				buildPropInfo.OplusProject = text3;
				break;
			case "ro.separate.soft":
			case "ro.product.supported_versions":
				if (string.IsNullOrEmpty(buildPropInfo.OplusProject))
				{
					buildPropInfo.OplusProject = text3;
				}
				break;
			case "ro.build.version.oplusrom":
			case "ro.build.version.oplusrom.confidential":
			case "ro.build.version.oplusrom.display":
				if (!buildPropInfo.AllProperties.ContainsKey("oplus_rom_version"))
				{
					buildPropInfo.AllProperties["oplus_rom_version"] = text3;
				}
				break;
			case "ro.oplus.image.my_region.type":
			case "ro.oplus.pipeline_key":
				if (!buildPropInfo.AllProperties.ContainsKey("oplus_region"))
				{
					buildPropInfo.AllProperties["oplus_region"] = text3;
				}
				break;
			case "ro.lenovo.cpuinfo":
				buildPropInfo.OplusCpuInfo = text3;
				break;
			case "ro.build.date":
				buildPropInfo.BuildDate = text3;
				break;
			case "ro.build.date.utc":
				buildPropInfo.BuildUtc = text3;
				break;
			case "ro.odm.build.version.release":
			case "ro.build.version.release":
			case "ro.system.build.version.release":
			case "ro.vendor.build.version.release":
			case "ro.build.version.release_or_codename":
			case "ro.vendor.build.version.release_or_codename":
			case "ro.product.build.version.release":
				if (string.IsNullOrEmpty(buildPropInfo.AndroidVersion))
				{
					buildPropInfo.AndroidVersion = text3;
				}
				break;
			case "ro.vendor.build.version.sdk":
			case "ro.system.build.version.sdk":
			case "ro.build.version.sdk":
				if (string.IsNullOrEmpty(buildPropInfo.SdkVersion))
				{
					buildPropInfo.SdkVersion = text3;
				}
				break;
			case "ro.system.build.version.security_patch":
			case "ro.vendor.build.version.security_patch":
			case "ro.build.version.security_patch":
				if (string.IsNullOrEmpty(buildPropInfo.SecurityPatch))
				{
					buildPropInfo.SecurityPatch = text3;
				}
				break;
			case "ro.build.product":
			case "ro.product.board":
			case "ro.product.odm.device":
			case "ro.product.system.device":
			case "ro.product.vendor.device":
			case "ro.product.device":
				if (string.IsNullOrEmpty(buildPropInfo.Codename))
				{
					buildPropInfo.Codename = text3;
				}
				if (string.IsNullOrEmpty(buildPropInfo.Device))
				{
					buildPropInfo.Device = text3;
				}
				break;
			case "ro.build.id":
				buildPropInfo.BuildId = text3;
				break;
			case "ro.system.build.fingerprint":
			case "ro.vendor.build.fingerprint":
			case "ro.build.fingerprint":
				if (string.IsNullOrEmpty(buildPropInfo.Fingerprint))
				{
					buildPropInfo.Fingerprint = text3;
				}
				break;
			case "ro.product.name":
			case "ro.product.vendor.name":
				if (string.IsNullOrEmpty(buildPropInfo.DeviceName))
				{
					buildPropInfo.DeviceName = text3;
				}
				if (string.IsNullOrEmpty(buildPropInfo.Codename) && !text3.Contains(" "))
				{
					buildPropInfo.Codename = text3;
				}
				break;
			}
		}
		if (string.IsNullOrEmpty(buildPropInfo.Codename) && !string.IsNullOrEmpty(buildPropInfo.Fingerprint))
		{
			string[] array3 = buildPropInfo.Fingerprint.Split('/');
			if (array3.Length >= 3)
			{
				string text4 = array3[1];
				if (!string.IsNullOrEmpty(text4) && !text4.Contains(" ") && text4.Length > 2)
				{
					buildPropInfo.Codename = text4;
				}
			}
		}
		return buildPropInfo;
	}

	public List<LpPartitionInfo> ParseLpMetadataFromDevice(DeviceReadDelegate readFromDevice, long superStartSector = 0L, int physicalSectorSize = 512)
	{
		try
		{
			byte[] array = readFromDevice(4096L, 4096);
			if (array == null || array.Length < 52)
			{
				_log("无法读取 LP Geometry");
				return null;
			}
			if (BitConverter.ToUInt32(array, 0) != 1634485351)
			{
				_log("无效的 LP Geometry magic");
				return null;
			}
			BitConverter.ToUInt32(array, 40);
			BitConverter.ToUInt32(array, 44);
			long[] obj = new long[4] { 8192L, 12288L, 4096L, 16384L };
			byte[] array2 = null;
			uint num = 0u;
			long num2 = 0L;
			long[] array3 = obj;
			foreach (long num3 in array3)
			{
				array2 = readFromDevice(num3, 4096);
				if (array2 != null && array2.Length >= 256)
				{
					num = BitConverter.ToUInt32(array2, 0);
					if (num == 1097336112 || num == 1095520304)
					{
						num2 = num3;
						break;
					}
				}
			}
			if (num2 == 0L)
			{
				_log("无法找到有效的 LP Metadata Header");
				return null;
			}
			uint num4 = BitConverter.ToUInt32(array2, 8);
			uint num5 = BitConverter.ToUInt32(array2, 16);
			int num6 = (int)(num4 + num5);
			if (num4 > 4096 || num5 > 262144)
			{
				_log($"[LP] Suspicious header size: headerSize={num4}, tablesSize={num5}, trying fallback offset");
				num4 = BitConverter.ToUInt32(array2, 8);
				num5 = BitConverter.ToUInt32(array2, 20);
				num6 = (int)(num4 + num5);
			}
			Action<string> logDetail = _logDetail;
			global::_003C_003Ey__InlineArray4<object> buffer = default(global::_003C_003Ey__InlineArray4<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 0) = num2;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 1) = num4;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 2) = num5;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 3) = num6;
			logDetail(string.Format("[LP] Header 偏移={0}, headerSize={1}, tablesSize={2}, 需读取={3} 字节", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer, 4)));
			if (num6 > array2.Length)
			{
				if (num6 > 1048576)
				{
					_log($"LP Metadata 过大 ({num6} 字节)，限制为 1MB");
					num6 = 1048576;
				}
				array2 = readFromDevice(num2, num6);
				if (array2 == null)
				{
					_log($"无法读取 LP Metadata (偏移={num2}, 大小={num6})");
					return null;
				}
				if (array2.Length < num6)
				{
					_log($"LP Metadata 读取不完整: 期望 {num6} 字节, 实际 {array2.Length} 字节");
					if (array2.Length < num4)
					{
						return null;
					}
				}
			}
			int num7 = (int)num4;
			int num8 = 80;
			uint num9 = BitConverter.ToUInt32(array2, num8);
			uint num10 = BitConverter.ToUInt32(array2, num8 + 4);
			uint num11 = BitConverter.ToUInt32(array2, num8 + 8);
			uint num12 = BitConverter.ToUInt32(array2, num8 + 12);
			uint num13 = BitConverter.ToUInt32(array2, num8 + 16);
			uint num14 = BitConverter.ToUInt32(array2, num8 + 20);
			List<Tuple<long, long>> list = new List<Tuple<long, long>>();
			for (int j = 0; j < num13; j++)
			{
				int num15 = num7 + (int)num12 + j * (int)num14;
				if (num15 + 12 > array2.Length)
				{
					break;
				}
				long item = BitConverter.ToInt64(array2, num15);
				long item2 = BitConverter.ToInt64(array2, num15 + 12);
				list.Add(Tuple.Create(item, item2));
			}
			List<LpPartitionInfo> list2 = new List<LpPartitionInfo>();
			for (int k = 0; k < num10; k++)
			{
				int num16 = num7 + (int)num9 + k * (int)num11;
				if (num16 + num11 > array2.Length)
				{
					break;
				}
				string text = Encoding.UTF8.GetString(array2, num16, 36).TrimEnd('\0');
				if (!string.IsNullOrEmpty(text))
				{
					uint attrs = BitConverter.ToUInt32(array2, num16 + 36);
					uint num17 = BitConverter.ToUInt32(array2, num16 + 40);
					if (BitConverter.ToUInt32(array2, num16 + 44) != 0 && num17 < list.Count)
					{
						Tuple<long, long> tuple = list[(int)num17];
						long item3 = tuple.Item2;
						long absoluteSector = superStartSector + item3 * 512 / physicalSectorSize;
						LpPartitionInfo lpPartitionInfo = new LpPartitionInfo
						{
							Name = text,
							Attrs = attrs,
							RelativeSector = item3,
							AbsoluteSector = absoluteSector,
							SizeInSectors = tuple.Item1 * 512 / physicalSectorSize,
							Size = tuple.Item1 * 512
						};
						list2.Add(lpPartitionInfo);
						_logDetail($"逻辑分区 [{lpPartitionInfo.Name}]: 物理扇区={lpPartitionInfo.AbsoluteSector}, 大小={lpPartitionInfo.Size / 1024 / 1024}MB");
					}
				}
			}
			return list2;
		}
		catch (Exception ex)
		{
			_log("解析 LP Metadata 失败: " + ex.Message);
			return null;
		}
	}

	public string DetectFileSystem(byte[] data)
	{
		if (data == null || data.Length < 512)
		{
			_log($"  数据过短: {((data != null) ? data.Length : 0)} 字节");
			return "unknown";
		}
		string text = "";
		if (data.Length >= 4)
		{
			string text2 = text;
			global::_003C_003Ey__InlineArray4<object> buffer = default(global::_003C_003Ey__InlineArray4<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 0) = data[0];
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 1) = data[1];
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 2) = data[2];
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 3) = data[3];
			text = text2 + string.Format("@0={0:X2}{1:X2}{2:X2}{3:X2}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer, 4));
		}
		if (data.Length >= 1028)
		{
			string text3 = text;
			global::_003C_003Ey__InlineArray4<object> buffer2 = default(global::_003C_003Ey__InlineArray4<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 0) = data[1024];
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 1) = data[1025];
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 2) = data[1026];
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer2, 3) = data[1027];
			text = text3 + string.Format(" @1024={0:X2}{1:X2}{2:X2}{3:X2}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer2, 4));
		}
		if (data.Length >= 1082)
		{
			text += $" @1080={data[1080]:X2}{data[1081]:X2}";
		}
		_logDetail($"  魔数: {text}");
		if (data.Length >= 4 && BitConverter.ToUInt32(data, 0) == 3978755898u)
		{
			_log("  -> Sparse 镜像格式");
			return "sparse";
		}
		if (data.Length >= 1028 && BitConverter.ToUInt32(data, 1024) == 3774210530u)
		{
			return "erofs";
		}
		if (data.Length >= 4 && BitConverter.ToUInt32(data, 0) == 3774210530u)
		{
			_logDetail("  -> EROFS 在偏移 0");
			return "erofs_raw";
		}
		if (data.Length >= 1082 && BitConverter.ToUInt16(data, 1080) == 61267)
		{
			return "ext4";
		}
		if (data.Length >= 1028 && BitConverter.ToUInt32(data, 1024) == 4076150800u)
		{
			return "f2fs";
		}
		if (data.Length >= 4)
		{
			uint num = BitConverter.ToUInt32(data, 0);
			if (num == 1936814952 || num == 1752396147)
			{
				return "squashfs";
			}
		}
		if (data.Length >= 8 && data[0] == 65 && data[1] == 78 && data[2] == 68 && data[3] == 82 && data[4] == 79 && data[5] == 73 && data[6] == 68 && data[7] == 33)
		{
			return "android_boot";
		}
		if (data.Length >= 4)
		{
			bool flag = true;
			for (int i = 0; i < Math.Min(64, data.Length); i++)
			{
				if (data[i] != 0)
				{
					flag = false;
					break;
				}
			}
			if (flag)
			{
				_log("  -> 分区数据为空");
				return "empty";
			}
			if (data.Length >= 4)
			{
				char c = (char)data[0];
				char c2 = (char)data[1];
				char c3 = (char)data[2];
				char c4 = (char)data[3];
				if (((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) && (c4 == '_' || c3 == '_'))
				{
					Action<string> logDetail = _logDetail;
					global::_003C_003Ey__InlineArray4<object> buffer3 = default(global::_003C_003Ey__InlineArray4<object>);
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 0) = c;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 1) = c2;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 2) = c3;
					global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer3, 3) = c4;
					logDetail(string.Format("  -> 检测到签名头: {0}{1}{2}{3} (文件系统可能在后面)", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer3, 4)));
					return "signed";
				}
			}
		}
		return "unknown";
	}

	public async Task<BuildPropInfo> ReadBuildPropFromDevice(Func<string, long, int, Task<byte[]>> readPartition, string activeSlot = "", bool hasSuper = true, long superStartSector = 0L, int physicalSectorSize = 512, string vendorName = "")
	{
		await _singleFlight.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
		int abortRequested = 0;
		const int SuperReadTimeoutSeconds = 6;
		const int SuperReadPhaseTimeoutMs = 45000;
		const int SuperMaxConsecutiveTimeouts = 2;
		try
		{
			BuildPropInfo finalInfo = null;
			if (hasSuper)
			{
				SafeLog("正在从 Super 分区逻辑卷解析 build.prop...");
				Task<BuildPropInfo> superReadTask = Task.Run(delegate
				{
					int consecutiveTimeouts = 0;
					DeviceReadDelegate readFromSuper = delegate(long offset, int size)
					{
						if (Volatile.Read(ref abortRequested) == 1)
						{
							return null;
						}
						if (consecutiveTimeouts >= SuperMaxConsecutiveTimeouts)
						{
							return null;
						}
						bool gateTaken = false;
						try
						{
							gateTaken = _ioGate.Wait(TimeSpan.FromSeconds(SuperReadTimeoutSeconds));
							if (!gateTaken)
							{
								consecutiveTimeouts++;
								return null;
							}
							if (Volatile.Read(ref abortRequested) == 1)
							{
								return null;
							}
							Task<byte[]> task = readPartition("super", offset, size);
							if (!task.Wait(TimeSpan.FromSeconds(SuperReadTimeoutSeconds)))
							{
								consecutiveTimeouts++;
								SafeLogDetail($"读取 super 分区超时 @offset={offset}, size={size} (连续{consecutiveTimeouts}次)");
								try
								{
									task.Wait(TimeSpan.FromSeconds(1L));
								}
								catch
								{
								}
								return null;
							}
							consecutiveTimeouts = 0;
							return task.Result;
						}
						catch (AggregateException ex2) when (ex2.InnerException is OperationCanceledException)
						{
							return null;
						}
						catch (Exception ex3)
						{
							SafeLogDetail("读取 super 分区异常: " + ex3.Message);
							return null;
						}
						finally
						{
							if (gateTaken)
							{
								_ioGate.Release();
							}
						}
					};
					return ReadBuildPropFromSuper(readFromSuper, activeSlot, superStartSector, physicalSectorSize);
				});
				if (await Task.WhenAny(superReadTask, Task.Delay(SuperReadPhaseTimeoutMs)).ConfigureAwait(continueOnCapturedContext: false) == superReadTask)
				{
					finalInfo = await superReadTask.ConfigureAwait(continueOnCapturedContext: false);
				}
				else
				{
					Interlocked.Exchange(ref abortRequested, 1);
					SafeLog("从 Super 分区读取超时 (45秒)，跳过");
				}
			}
			if (finalInfo != null && (!string.IsNullOrEmpty(finalInfo.Model) || !string.IsNullOrEmpty(finalInfo.MarketName)))
			{
				SafeLog("从 Super 获取到基本信息，跳过物理分区扫描");
				return finalInfo;
			}
			SafeLog("正在扫描物理分区以提取 build.prop...");
			List<string> list = new List<string>();
			void AddCandidate(string name)
			{
				if (!string.IsNullOrWhiteSpace(name) && !list.Exists((string p) => p.Equals(name, StringComparison.OrdinalIgnoreCase)))
				{
					list.Add(name);
				}
			}
			string text = activeSlot;
			if (string.IsNullOrEmpty(text) || text == "undefined" || text == "unknown" || text == "nonexistent")
			{
				text = "";
			}
			string text2 = (string.IsNullOrEmpty(text) ? "" : ("_" + text.ToLower().TrimStart('_')));
			if (!string.IsNullOrEmpty(text2))
			{
				AddCandidate("system" + text2);
				AddCandidate("system_ext" + text2);
				AddCandidate("vendor" + text2);
				AddCandidate("product" + text2);
				AddCandidate("odm" + text2);
				AddCandidate("my_manifest" + text2);
			}
			AddCandidate("system");
			AddCandidate("system_ext");
			AddCandidate("vendor");
			AddCandidate("product");
			AddCandidate("odm");
			AddCandidate("my_manifest");
			AddCandidate("cust");
			AddCandidate("lenovocust");
			AddCandidate("persist");
			SafeLog($"  将扫描 {list.Count} 个分区");
			foreach (string item in list)
			{
				BuildPropInfo buildPropInfo = await ReadBuildPropFromStandalonePartition(item, readPartition);
				if (buildPropInfo == null)
				{
					continue;
				}
				if (finalInfo == null)
				{
					finalInfo = buildPropInfo;
				}
				else
				{
					MergeProperties(finalInfo, buildPropInfo);
				}
				if (!string.IsNullOrEmpty(finalInfo.MarketName) || !string.IsNullOrEmpty(finalInfo.Model))
				{
					break;
				}
			}
			return finalInfo;
		}
		catch (Exception ex)
		{
			SafeLog($"读取 build.prop 整体流程失败: {ex.Message}");
		}
		finally
		{
			_singleFlight.Release();
		}
		return null;
	}

	private BuildPropInfo ReadBuildPropFromSuper(DeviceReadDelegate readFromSuper, string activeSlot = "", long superStartSector = 0L, int physicalSectorSize = 512)
	{
		BuildPropInfo buildPropInfo = new BuildPropInfo();
		try
		{
			List<LpPartitionInfo> list = ParseLpMetadataFromDevice(readFromSuper, superStartSector, physicalSectorSize);
			if (list == null || list.Count == 0)
			{
				return null;
			}
			string text = activeSlot;
			if (!string.IsNullOrEmpty(text))
			{
				switch (text)
				{
				case "undefined":
				case "unknown":
				case "nonexistent":
					break;
				default:
					goto IL_005b;
				}
			}
			text = "";
			goto IL_005b;
			IL_005b:
			string text2 = (string.IsNullOrEmpty(text) ? "" : ("_" + text.ToLower().TrimStart('_')));
			foreach (string item in new List<string> { "system", "system_ext", "product", "vendor", "odm", "my_manifest" })
			{
				string[] array = new string[2]
				{
					item + text2,
					item
				};
				foreach (string name in array)
				{
					LpPartitionInfo lpPartitionInfo = list.FirstOrDefault((LpPartitionInfo p) => p.Name == name);
					if (lpPartitionInfo == null)
					{
						continue;
					}
					_logDetail($"[DevInfo] 解析逻辑卷 {lpPartitionInfo.Name} (扇区: {lpPartitionInfo.AbsoluteSector})");
					long num = lpPartitionInfo.RelativeSector * 512;
					BuildPropInfo buildPropInfo2 = null;
					byte[] array2 = readFromSuper(num, 4096);
					if (array2 != null && array2.Length >= 4096)
					{
						buildPropInfo2 = ((BitConverter.ToUInt32(array2, 1024) != 3774210530u) ? ParseExt4AndFindBuildProp(readFromSuper, lpPartitionInfo, num) : ParseErofsAndFindBuildProp(readFromSuper, lpPartitionInfo, num));
					}
					if (buildPropInfo2 == null && lpPartitionInfo.Size < 2097152)
					{
						_logDetail($"尝试对逻辑卷 {lpPartitionInfo.Name} 进行暴力属性扫描...");
						byte[] array3 = readFromSuper(num, (int)lpPartitionInfo.Size);
						if (array3 != null)
						{
							string content = Encoding.UTF8.GetString(array3);
							buildPropInfo2 = ParseBuildProp(content);
						}
					}
					if (buildPropInfo2 != null)
					{
						MergeProperties(buildPropInfo, buildPropInfo2);
					}
					break;
				}
			}
		}
		catch (Exception ex)
		{
			_log("从 Super 精准读取失败: " + ex.Message);
		}
		if (string.IsNullOrEmpty(buildPropInfo.Model) && string.IsNullOrEmpty(buildPropInfo.MarketName) && string.IsNullOrEmpty(buildPropInfo.Brand) && string.IsNullOrEmpty(buildPropInfo.Device))
		{
			return null;
		}
		return buildPropInfo;
	}

	private void MergeProperties(BuildPropInfo target, BuildPropInfo source)
	{
		if (source == null)
		{
			return;
		}
		if (!string.IsNullOrEmpty(source.Brand))
		{
			target.Brand = source.Brand;
		}
		if (!string.IsNullOrEmpty(source.Model))
		{
			target.Model = source.Model;
		}
		if (!string.IsNullOrEmpty(source.MarketName))
		{
			target.MarketName = source.MarketName;
		}
		if (!string.IsNullOrEmpty(source.MarketNameEn))
		{
			target.MarketNameEn = source.MarketNameEn;
		}
		if (!string.IsNullOrEmpty(source.Device))
		{
			target.Device = source.Device;
		}
		if (!string.IsNullOrEmpty(source.Manufacturer))
		{
			target.Manufacturer = source.Manufacturer;
		}
		if (!string.IsNullOrEmpty(source.AndroidVersion))
		{
			target.AndroidVersion = source.AndroidVersion;
		}
		if (!string.IsNullOrEmpty(source.SdkVersion))
		{
			target.SdkVersion = source.SdkVersion;
		}
		if (!string.IsNullOrEmpty(source.SecurityPatch))
		{
			target.SecurityPatch = source.SecurityPatch;
		}
		if (!string.IsNullOrEmpty(source.DisplayId))
		{
			target.DisplayId = source.DisplayId;
		}
		if (!string.IsNullOrEmpty(source.OtaVersion))
		{
			target.OtaVersion = source.OtaVersion;
		}
		if (!string.IsNullOrEmpty(source.OtaVersionFull))
		{
			target.OtaVersionFull = source.OtaVersionFull;
		}
		if (!string.IsNullOrEmpty(source.BuildDate))
		{
			target.BuildDate = source.BuildDate;
		}
		if (!string.IsNullOrEmpty(source.BuildUtc))
		{
			target.BuildUtc = source.BuildUtc;
		}
		if (!string.IsNullOrEmpty(source.OplusProject))
		{
			target.OplusProject = source.OplusProject;
		}
		if (!string.IsNullOrEmpty(source.OplusNvId))
		{
			target.OplusNvId = source.OplusNvId;
		}
		if (!string.IsNullOrEmpty(source.OplusCpuInfo))
		{
			target.OplusCpuInfo = source.OplusCpuInfo;
		}
		if (!string.IsNullOrEmpty(source.LenovoSeries))
		{
			target.LenovoSeries = source.LenovoSeries;
		}
		foreach (KeyValuePair<string, string> allProperty in source.AllProperties)
		{
			target.AllProperties[allProperty.Key] = allProperty.Value;
		}
	}

	private async Task<BuildPropInfo> ReadBuildPropFromStandalonePartition(string partitionName, Func<string, long, int, Task<byte[]>> readPartition)
	{
		try
		{
			SafeLog($"尝试从物理分区 {partitionName} 读取...");
			byte[] array = await readPartition(partitionName, 0L, 4096);
			if (array == null)
			{
				SafeLog($"  -> {partitionName}: 无法读取头部数据");
				return null;
			}
			string fsType = DetectFileSystem(array);
			long fsBaseOffset = 0L;
			if (fsType == "sparse")
			{
				SafeLog($"  -> {partitionName}: 检测到 Sparse 格式，跳过头部重新检测...");
				byte[] array2 = await readPartition(partitionName, 4096L, 4096);
				if (array2 != null && array2.Length > 1024)
				{
					fsType = DetectFileSystem(array2);
					if (fsType != "unknown" && fsType != "sparse")
					{
						SafeLog($"  -> {partitionName}: Sparse 内部为 {fsType.ToUpper()} 文件系统");
						fsBaseOffset = 4096L;
					}
				}
			}
			if (fsType == "erofs_raw")
			{
				fsType = "erofs";
				SafeLogDetail($"  -> {partitionName}: 检测到无偏移 EROFS 文件系统");
			}
			if (fsType == "unknown" || fsType == "empty" || fsType == "signed")
			{
				long[] array3 = new long[6] { 4096L, 8192L, 65536L, 1048576L, 2097152L, 4194304L };
				long[] array4 = array3;
				foreach (long offset in array4)
				{
					byte[] array5 = await readPartition(partitionName, offset, 4096);
					if (array5 != null && array5.Length >= 2048)
					{
						string text = DetectFileSystem(array5);
						if (text != "unknown" && text != "empty" && text != "sparse")
						{
							SafeLogDetail($"  -> {partitionName}: 在偏移 0x{offset:X} 处检测到 {text.ToUpper()} 文件系统");
							fsType = text;
							fsBaseOffset = offset;
							break;
						}
					}
				}
			}
			if (fsType == "unknown" || fsType == "sparse" || fsType == "empty" || fsType == "signed")
			{
				SafeLog($"  -> {partitionName}: 未识别的文件系统格式，尝试暴力扫描...");
				BuildPropInfo buildPropInfo = await BruteForceScanPartition(partitionName, readPartition);
				if (buildPropInfo != null)
				{
					SafeLog($"  -> {partitionName}: 暴力扫描成功");
					return buildPropInfo;
				}
				return null;
			}
			SafeLogDetail($"  -> {partitionName}: 检测到 {fsType.ToUpper()} 文件系统 (偏移=0x{fsBaseOffset:X})，正在解析...");
			LpPartitionInfo lpInfo = new LpPartitionInfo
			{
				Name = partitionName,
				RelativeSector = 0L,
				FileSystem = fsType
			};
			int parseAbortRequested = 0;
			long capturedBaseOffset = fsBaseOffset;
			string capturedPartName = partitionName;
			Task<BuildPropInfo> parseTask = Task.Run(delegate
			{
				DeviceReadDelegate readFromSuper = delegate(long num, int size)
				{
					for (int j = 0; j < 3; j++)
					{
						if (Volatile.Read(ref parseAbortRequested) == 1)
						{
							return null;
						}
						bool gateTaken = false;
						try
						{
							int num2 = (capturedPartName.Contains("system") ? 15 : 10);
							gateTaken = _ioGate.Wait(TimeSpan.FromSeconds(num2));
							if (!gateTaken)
							{
								if (j >= 2)
								{
									return null;
								}
								Thread.Sleep(200);
								continue;
							}
							if (Volatile.Read(ref parseAbortRequested) == 1)
							{
								return null;
							}
							Task<byte[]> task = readPartition(capturedPartName, capturedBaseOffset + num, size);
							if (!task.Wait(TimeSpan.FromSeconds(num2)))
							{
								if (j >= 2)
								{
									return null;
								}
								Thread.Sleep(200);
								continue;
							}
							if (task.Result != null && task.Result.Length != 0)
							{
								return task.Result;
							}
						}
						catch (Exception ex2)
						{
							if (j < 2)
							{
								Thread.Sleep(200);
							}
							else
							{
								SafeLogDetail($"读取分区数据失败: {ex2.Message}");
							}
						}
						finally
						{
							if (gateTaken)
							{
								_ioGate.Release();
							}
						}
					}
					return null;
				};
				if (fsType == "erofs")
				{
					return ParseErofsAndFindBuildProp(readFromSuper, lpInfo, 0L);
				}
				return (fsType == "ext4") ? ParseExt4AndFindBuildProp(readFromSuper, lpInfo, 0L) : null;
			});
			int timeoutMs = (partitionName.Contains("system") ? 45000 : 30000);
			if (await Task.WhenAny(parseTask, Task.Delay(timeoutMs)).ConfigureAwait(continueOnCapturedContext: false) == parseTask)
			{
				return await parseTask.ConfigureAwait(continueOnCapturedContext: false);
			}
			Interlocked.Exchange(ref parseAbortRequested, 1);
			SafeLog($"解析分区 {partitionName} 超时 ({timeoutMs / 1000}秒)");
		}
		catch (Exception ex)
		{
			SafeLogDetail("解析分区 build.prop 异常: " + ex.Message);
		}
		return null;
	}

	private async Task<BuildPropInfo> BruteForceScanPartition(string partitionName, Func<string, long, int, Task<byte[]>> readPartition)
	{
		try
		{
			List<string> foundProps = new List<string>();
			for (long offset = 0L; offset < 16777216; offset += 524288)
			{
				byte[] array = await readPartition(partitionName, offset, 524288);
				if (array == null || array.Length == 0)
				{
					break;
				}
				string input = Encoding.UTF8.GetString(array);
				string[] array2 = new string[11]
				{
					"ro\\.product\\.model=[^\\r\\n\\x00]+", "ro\\.product\\.brand=[^\\r\\n\\x00]+", "ro\\.product\\.name=[^\\r\\n\\x00]+", "ro\\.product\\.device=[^\\r\\n\\x00]+", "ro\\.product\\.manufacturer=[^\\r\\n\\x00]+", "ro\\.product\\.marketname=[^\\r\\n\\x00]+", "ro\\.build\\.display\\.id=[^\\r\\n\\x00]+", "ro\\.build\\.version\\.release=[^\\r\\n\\x00]+", "ro\\.build\\.version\\.sdk=[^\\r\\n\\x00]+", "ro\\.miui\\.ui\\.version\\.[^\\r\\n\\x00]+",
					"ro\\.build\\.MiFavor_version=[^\\r\\n\\x00]+"
				};
				foreach (string pattern in array2)
				{
					foreach (Match item in Regex.Matches(input, pattern))
					{
						if (!foundProps.Contains(item.Value))
						{
							foundProps.Add(item.Value);
						}
					}
				}
				if (foundProps.Count >= 5)
				{
					break;
				}
			}
			if (foundProps.Count > 0)
			{
				_log($"    暴力扫描找到 {foundProps.Count} 个属性");
				string content = string.Join("\n", foundProps);
				return ParseBuildProp(content);
			}
		}
		catch (Exception ex)
		{
			_logDetail($"暴力扫描失败: {ex.Message}");
		}
		return null;
	}

	private BuildPropInfo ParseErofsAndFindBuildProp(DeviceReadDelegate readFromSuper, LpPartitionInfo partition, long baseOffset = 0L)
	{
		try
		{
			DeviceReadDelegate deviceReadDelegate = (long offset, int size2) => readFromSuper(baseOffset + offset, size2);
			byte[] array = deviceReadDelegate(1024L, 128);
			if (array == null || array.Length < 128)
			{
				_logDetail("无法读取 EROFS superblock");
				return null;
			}
			if (array[0] != 226 || array[1] != 225 || array[2] != 245 || array[3] != 224)
			{
				_logDetail("无效的 EROFS superblock");
				return null;
			}
			byte b = array[12];
			ushort num = BitConverter.ToUInt16(array, 14);
			uint num2 = BitConverter.ToUInt32(array, 40);
			uint num3 = (uint)(1 << (int)b);
			_logDetail($"EROFS: BlockSize={num3}, RootNid={num}, MetaBlkAddr={num2}");
			long offsetInSuper = (long)num2 * (long)num3 + (long)num * 32L;
			byte[] array2 = deviceReadDelegate(offsetInSuper, 64);
			if (array2 == null || array2.Length < 32)
			{
				_logDetail("无法读取根目录 inode");
				return null;
			}
			ushort num4 = BitConverter.ToUInt16(array2, 0);
			bool flag = (num4 & 1) == 1;
			byte b2 = (byte)((num4 >> 1) & 7);
			if ((BitConverter.ToUInt16(array2, 4) & 0xF000) != 16384)
			{
				_logDetail("根 inode 不是目录");
				return null;
			}
			long num5 = (flag ? BitConverter.ToInt64(array2, 8) : BitConverter.ToUInt32(array2, 8));
			uint num6 = BitConverter.ToUInt32(array2, 16);
			int num7 = (flag ? 64 : 32);
			ushort num8 = BitConverter.ToUInt16(array2, 2);
			int num9 = ((num8 > 0) ? ((num8 - 1) * 4 + 12) : 0);
			int num10 = num7 + num9;
			_logDetail($"  EROFS 根目录: layout={b2}, size={num5}");
			byte[] array3 = null;
			switch (b2)
			{
			case 2:
			{
				int size = num10 + (int)Math.Min(num5, num3);
				byte[] array4 = deviceReadDelegate(offsetInSuper, size);
				if (array4 != null && array4.Length > num10)
				{
					int num11 = Math.Min((int)num5, array4.Length - num10);
					array3 = new byte[num11];
					Array.Copy(array4, num10, array3, 0, num11);
				}
				break;
			}
			case 0:
			{
				long offsetInSuper2 = (long)num6 * (long)num3;
				array3 = deviceReadDelegate(offsetInSuper2, (int)Math.Min(num5, num3 * 2));
				break;
			}
			case 1:
			case 3:
				_logDetail("检测到压缩 EROFS，尝试 LZ4 解压...");
				array3 = ReadErofsCompressedData(deviceReadDelegate, array2, flag, num6, num3, num5, num2);
				break;
			}
			if (array3 == null || array3.Length < 12)
			{
				_logDetail($"  无法读取目录数据 (layout={b2})");
				_log(string.Format("EROFS 分区读取失败", default(ReadOnlySpan<object>)));
				return null;
			}
			List<Tuple<ulong, string, byte>> list = ParseErofsDirectoryEntries(array3, num5);
			_logDetail($"  EROFS 根目录包含 {list.Count} 个条目");
			int num12 = 0;
			foreach (Tuple<ulong, string, byte> item in list)
			{
				if (num12++ < 8)
				{
					_logDetail($"    - {item.Item2} (type={item.Item3})");
				}
			}
			foreach (Tuple<ulong, string, byte> item2 in list)
			{
				if (item2.Item2 == "build.prop" && item2.Item3 == 1)
				{
					_logDetail("找到 /build.prop");
					return ReadErofsFile(deviceReadDelegate, num2, num3, item2.Item1);
				}
			}
			string[] array5 = new string[2] { "system", "etc" };
			foreach (string text in array5)
			{
				foreach (Tuple<ulong, string, byte> item3 in list)
				{
					if (!(item3.Item2 == text) || item3.Item3 != 2)
					{
						continue;
					}
					_log($"  进入 /{text} 目录搜索...");
					foreach (Tuple<ulong, string, byte> item4 in ReadErofsDirectory(deviceReadDelegate, num2, num3, item3.Item1))
					{
						if (item4.Item2 == "build.prop" && item4.Item3 == 1)
						{
							_logDetail($"找到 /{text}/build.prop");
							return ReadErofsFile(deviceReadDelegate, num2, num3, item4.Item1);
						}
					}
				}
			}
			_logDetail("未找到 build.prop");
			return null;
		}
		catch (Exception ex)
		{
			_logDetail($"解析 EROFS 失败: {ex.Message}");
			return null;
		}
	}

	private byte[] ReadErofsCompressedData(DeviceReadDelegate read, byte[] inodeData, bool isExtended, uint rawBlkAddr, uint blockSize, long uncompressedSize, uint metaBlkAddr)
	{
		try
		{
			long offsetInSuper = (long)rawBlkAddr * (long)blockSize;
			int size = (int)Math.Min(uncompressedSize * 2, blockSize * 4);
			byte[] array = read(offsetInSuper, size);
			if (array == null || array.Length == 0)
			{
				_logDetail("无法读取压缩数据");
				return null;
			}
			byte[] array2 = Lz4Decoder.Decompress(array, (int)uncompressedSize);
			if (array2 != null && array2.Length != 0 && IsValidDirectoryData(array2))
			{
				_log("  LZ4 解压成功 (无头格式)");
				return array2;
			}
			if (array.Length > 4)
			{
				array2 = Lz4Decoder.Decompress(array, 4, array.Length - 4, (int)uncompressedSize);
				if (array2 != null && array2.Length != 0 && IsValidDirectoryData(array2))
				{
					_log("  LZ4 解压成功 (4字节头)");
					return array2;
				}
			}
			array2 = Lz4Decoder.DecompressErofsBlock(array, (int)uncompressedSize);
			if (array2 != null && array2.Length != 0 && IsValidDirectoryData(array2))
			{
				_logDetail("  LZ4 解压成功 (EROFS 块格式)");
				return array2;
			}
			for (int i = 0; i < Math.Min(32, array.Length - 16); i++)
			{
				array2 = Lz4Decoder.Decompress(array, i, array.Length - i, (int)uncompressedSize);
				if (array2 != null && array2.Length != 0 && IsValidDirectoryData(array2))
				{
					_log($"  LZ4 解压成功 (偏移 {i})");
					return array2;
				}
			}
			_log("  LZ4 解压失败，压缩格式可能不支持");
			return null;
		}
		catch (Exception ex)
		{
			_logDetail($"解压失败: {ex.Message}");
			return null;
		}
	}

	private byte[] ReadErofsCompressedFileData(DeviceReadDelegate read, uint rawBlkAddr, uint blockSize, long uncompressedSize)
	{
		try
		{
			long offsetInSuper = (long)rawBlkAddr * (long)blockSize;
			int size = (int)Math.Min(uncompressedSize * 2, blockSize * 4);
			byte[] array = read(offsetInSuper, size);
			if (array == null || array.Length == 0)
			{
				return null;
			}
			byte[] array2 = Lz4Decoder.Decompress(array, (int)uncompressedSize);
			if (array2 != null && array2.Length != 0 && IsValidTextFile(array2))
			{
				return array2;
			}
			if (array.Length > 4)
			{
				array2 = Lz4Decoder.Decompress(array, 4, array.Length - 4, (int)uncompressedSize);
				if (array2 != null && array2.Length != 0 && IsValidTextFile(array2))
				{
					return array2;
				}
			}
			array2 = Lz4Decoder.DecompressErofsBlock(array, (int)uncompressedSize);
			if (array2 != null && array2.Length != 0 && IsValidTextFile(array2))
			{
				return array2;
			}
			for (int i = 1; i < Math.Min(16, array.Length - 16); i++)
			{
				array2 = Lz4Decoder.Decompress(array, i, array.Length - i, (int)uncompressedSize);
				if (array2 != null && array2.Length != 0 && IsValidTextFile(array2))
				{
					return array2;
				}
			}
			return null;
		}
		catch
		{
			return null;
		}
	}

	private bool IsValidTextFile(byte[] data)
	{
		if (data == null || data.Length < 10)
		{
			return false;
		}
		int num = 0;
		int num2 = Math.Min(data.Length, 256);
		for (int i = 0; i < num2; i++)
		{
			byte b = data[i];
			if ((b >= 32 && b <= 126) || b == 10 || b == 13 || b == 9)
			{
				num++;
			}
		}
		return num * 100 / num2 >= 80;
	}

	private bool IsValidDirectoryData(byte[] data)
	{
		if (data == null || data.Length < 12)
		{
			return false;
		}
		ushort num = BitConverter.ToUInt16(data, 8);
		if (num == 0 || num % 12 != 0 || num > data.Length)
		{
			return false;
		}
		if (BitConverter.ToUInt64(data, 0) > uint.MaxValue)
		{
			return false;
		}
		return true;
	}

	private List<Tuple<ulong, string, byte>> ReadErofsDirectory(DeviceReadDelegate read, uint metaBlkAddr, uint blockSize, ulong nid)
	{
		List<Tuple<ulong, string, byte>> result = new List<Tuple<ulong, string, byte>>();
		try
		{
			long offsetInSuper = (long)metaBlkAddr * (long)blockSize + (long)(nid * 32);
			byte[] array = read(offsetInSuper, 64);
			if (array == null || array.Length < 32)
			{
				return result;
			}
			ushort num = BitConverter.ToUInt16(array, 0);
			bool flag = (num & 1) == 1;
			byte b = (byte)((num >> 1) & 7);
			if ((BitConverter.ToUInt16(array, 4) & 0xF000) != 16384)
			{
				return result;
			}
			long num2 = (flag ? BitConverter.ToInt64(array, 8) : BitConverter.ToUInt32(array, 8));
			uint num3 = BitConverter.ToUInt32(array, 16);
			int num4 = (flag ? 64 : 32);
			ushort num5 = BitConverter.ToUInt16(array, 2);
			int num6 = ((num5 > 0) ? ((num5 - 1) * 4 + 12) : 0);
			int num7 = num4 + num6;
			byte[] array2 = null;
			switch (b)
			{
			case 2:
			{
				int size = num7 + (int)Math.Min(num2, blockSize);
				byte[] array3 = read(offsetInSuper, size);
				if (array3 != null && array3.Length > num7)
				{
					int num8 = Math.Min((int)num2, array3.Length - num7);
					array2 = new byte[num8];
					Array.Copy(array3, num7, array2, 0, num8);
				}
				break;
			}
			case 0:
			{
				long offsetInSuper2 = (long)num3 * (long)blockSize;
				array2 = read(offsetInSuper2, (int)Math.Min(num2, blockSize * 2));
				break;
			}
			case 1:
			case 3:
				array2 = ReadErofsCompressedData(read, array, flag, num3, blockSize, num2, metaBlkAddr);
				break;
			}
			if (array2 != null)
			{
				result = ParseErofsDirectoryEntries(array2, num2);
			}
		}
		catch (Exception ex)
		{
			_logDetail("[EROFS] 读取目录异常: " + ex.Message);
		}
		return result;
	}

	private List<Tuple<ulong, string, byte>> ParseErofsDirectoryEntries(byte[] dirData, long dirSize)
	{
		List<Tuple<ulong, string, byte>> list = new List<Tuple<ulong, string, byte>>();
		if (dirData == null || dirData.Length < 12)
		{
			return list;
		}
		try
		{
			ushort num = BitConverter.ToUInt16(dirData, 8);
			if (num == 0 || num > dirData.Length)
			{
				return list;
			}
			int num2 = num / 12;
			List<Tuple<ulong, ushort, byte>> list2 = new List<Tuple<ulong, ushort, byte>>();
			for (int i = 0; i < num2 && i * 12 + 12 <= dirData.Length; i++)
			{
				ulong item = BitConverter.ToUInt64(dirData, i * 12);
				ushort item2 = BitConverter.ToUInt16(dirData, i * 12 + 8);
				byte item3 = dirData[i * 12 + 10];
				list2.Add(Tuple.Create(item, item2, item3));
			}
			for (int j = 0; j < list2.Count; j++)
			{
				Tuple<ulong, ushort, byte> tuple = list2[j];
				int num3 = ((j + 1 < list2.Count) ? list2[j + 1].Item2 : Math.Min(dirData.Length, (int)dirSize));
				if (tuple.Item2 < dirData.Length)
				{
					int num4 = 0;
					for (int k = 0; k < num3 - tuple.Item2 && tuple.Item2 + k < dirData.Length && dirData[tuple.Item2 + k] != 0; k++)
					{
						num4++;
					}
					if (num4 > 0)
					{
						string item4 = Encoding.UTF8.GetString(dirData, tuple.Item2, num4);
						list.Add(Tuple.Create(tuple.Item1, item4, tuple.Item3));
					}
				}
			}
		}
		catch (Exception ex)
		{
			_logDetail("[EROFS] 解析目录项异常: " + ex.Message);
		}
		return list;
	}

	private BuildPropInfo ReadErofsFile(DeviceReadDelegate read, uint metaBlkAddr, uint blockSize, ulong nid)
	{
		try
		{
			long offsetInSuper = (long)metaBlkAddr * (long)blockSize + (long)(nid * 32);
			byte[] array = read(offsetInSuper, 64);
			if (array == null || array.Length < 32)
			{
				return null;
			}
			ushort num = BitConverter.ToUInt16(array, 0);
			bool flag = (num & 1) == 1;
			byte b = (byte)((num >> 1) & 7);
			if ((BitConverter.ToUInt16(array, 4) & 0xF000) != 32768)
			{
				return null;
			}
			long num2 = (flag ? BitConverter.ToInt64(array, 8) : BitConverter.ToUInt32(array, 8));
			uint num3 = BitConverter.ToUInt32(array, 16);
			int num4 = (flag ? 64 : 32);
			ushort num5 = BitConverter.ToUInt16(array, 2);
			int num6 = ((num5 > 0) ? ((num5 - 1) * 4 + 12) : 0);
			int num7 = num4 + num6;
			int num8 = (int)Math.Min(num2, 65536L);
			byte[] array2 = null;
			switch (b)
			{
			case 2:
			{
				int size = num7 + num8;
				byte[] array3 = read(offsetInSuper, size);
				if (array3 != null && array3.Length > num7)
				{
					int num9 = Math.Min(num8, array3.Length - num7);
					array2 = new byte[num9];
					Array.Copy(array3, num7, array2, 0, num9);
				}
				break;
			}
			case 0:
			{
				long offsetInSuper2 = (long)num3 * (long)blockSize;
				array2 = read(offsetInSuper2, num8);
				break;
			}
			case 1:
			case 3:
				array2 = ReadErofsCompressedFileData(read, num3, blockSize, num2);
				break;
			}
			if (array2 != null && array2.Length != 0)
			{
				string content = Encoding.UTF8.GetString(array2);
				return ParseBuildProp(content);
			}
		}
		catch (Exception ex)
		{
			_logDetail("[EROFS] 读取文件异常: " + ex.Message);
		}
		return null;
	}

	private BuildPropInfo ParseExt4AndFindBuildProp(DeviceReadDelegate readFromSuper, LpPartitionInfo partition, long baseOffset = 0L)
	{
		try
		{
			DeviceReadDelegate deviceReadDelegate = (long offset, int size) => readFromSuper(baseOffset + offset, size);
			byte[] array = deviceReadDelegate(1024L, 1024);
			if (array == null || array.Length < 256)
			{
				_logDetail("无法读取 EXT4 superblock");
				return null;
			}
			ushort num = BitConverter.ToUInt16(array, 56);
			if (num != 61267)
			{
				_logDetail($"无效的 EXT4 magic: 0x{num:X4}");
				return null;
			}
			uint num2 = BitConverter.ToUInt32(array, 24);
			uint num3 = (uint)(1024 << (int)num2);
			uint inodesPerGroup = BitConverter.ToUInt32(array, 40);
			ushort num4 = BitConverter.ToUInt16(array, 88);
			uint num5 = BitConverter.ToUInt32(array, 20);
			uint blocksPerGroup = BitConverter.ToUInt32(array, 32);
			uint num6 = BitConverter.ToUInt32(array, 96);
			bool flag = (num6 & 0x40) != 0;
			bool flag2 = (num6 & 0x80) != 0;
			Action<string> logDetail = _logDetail;
			global::_003C_003Ey__InlineArray4<object> buffer = default(global::_003C_003Ey__InlineArray4<object>);
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 0) = num3;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 1) = num4;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 2) = flag;
			global::_003CPrivateImplementationDetails_003E.InlineArrayElementRef<global::_003C_003Ey__InlineArray4<object>, object>(ref buffer, 3) = flag2;
			logDetail(string.Format("EXT4: BlockSize={0}, InodeSize={1}, Extents={2}, 64bit={3}", global::_003CPrivateImplementationDetails_003E.InlineArrayAsReadOnlySpan<global::_003C_003Ey__InlineArray4<object>, object>(in buffer, 4)));
			long num7 = (num5 + 1) * num3;
			int num8 = (flag2 ? 64 : 32);
			byte[] array2 = deviceReadDelegate(num7, num8);
			if (array2 == null || array2.Length < num8)
			{
				_log("无法读取 Block Group Descriptor");
				return null;
			}
			uint num9 = BitConverter.ToUInt32(array2, 8);
			uint num10 = (flag2 ? BitConverter.ToUInt32(array2, 40) : 0u);
			long num11 = (long)(num9 | ((ulong)num10 << 32));
			_logDetail($"Inode Table Block: {num11}");
			long offsetInSuper = num11 * num3 + (int)num4;
			byte[] array3 = deviceReadDelegate(offsetInSuper, num4);
			if (array3 == null || array3.Length < 128)
			{
				_logDetail("无法读取根目录 inode");
				return null;
			}
			if ((BitConverter.ToUInt16(array3, 0) & 0xF000) != 16384)
			{
				_logDetail("根 inode 不是目录");
				return null;
			}
			uint val = BitConverter.ToUInt32(array3, 4);
			bool num12 = (BitConverter.ToUInt32(array3, 32) & 0x80000) != 0;
			byte[] array4 = null;
			if (num12)
			{
				array4 = ReadExt4ExtentData(deviceReadDelegate, array3, num3, (int)Math.Min(val, num3 * 4));
			}
			else
			{
				uint num13 = BitConverter.ToUInt32(array3, 40);
				if (num13 != 0)
				{
					array4 = deviceReadDelegate((long)num13 * (long)num3, (int)Math.Min(val, num3 * 4));
				}
			}
			if (array4 == null || array4.Length < 12)
			{
				_logDetail("无法读取根目录数据");
				return null;
			}
			List<Tuple<uint, string, byte>> list = ParseExt4DirectoryEntries(array4);
			_logDetail($"根目录包含 {list.Count} 个条目");
			foreach (Tuple<uint, string, byte> item in list)
			{
				if (item.Item2 == "build.prop" && item.Item3 == 1)
				{
					_logDetail("找到 /build.prop");
					return ReadExt4FileByInode(deviceReadDelegate, item.Item1, num11, num3, num4, inodesPerGroup);
				}
			}
			string[] array5 = new string[2] { "system", "etc" };
			foreach (string text in array5)
			{
				foreach (Tuple<uint, string, byte> item2 in list)
				{
					if (!(item2.Item2 == text) || item2.Item3 != 2)
					{
						continue;
					}
					_logDetail($"进入 /{text} 目录...");
					byte[] array6 = ReadExt4DirectoryByInode(deviceReadDelegate, item2.Item1, num11, num3, num4, inodesPerGroup, blocksPerGroup, flag2, num7, num8);
					if (array6 == null)
					{
						continue;
					}
					foreach (Tuple<uint, string, byte> item3 in ParseExt4DirectoryEntries(array6))
					{
						if (item3.Item2 == "build.prop" && item3.Item3 == 1)
						{
							_logDetail($"找到 /{text}/build.prop");
							return ReadExt4FileByInode(deviceReadDelegate, item3.Item1, num11, num3, num4, inodesPerGroup);
						}
					}
				}
			}
			_logDetail("未找到 build.prop");
			return null;
		}
		catch (Exception ex)
		{
			_logDetail($"解析 EXT4 失败: {ex.Message}");
			return null;
		}
	}

	private byte[] ReadExt4ExtentData(DeviceReadDelegate read, byte[] inode, uint blockSize, int maxSize)
	{
		try
		{
			return ReadExt4ExtentDataRecursive(read, inode, 40, blockSize, maxSize, 0);
		}
		catch
		{
			return null;
		}
	}

	private byte[] ReadExt4ExtentDataRecursive(DeviceReadDelegate read, byte[] data, int headerOffset, uint blockSize, int maxSize, int depth)
	{
		if (depth > 5)
		{
			return null;
		}
		if (data == null || headerOffset + 12 > data.Length)
		{
			return null;
		}
		ushort num = BitConverter.ToUInt16(data, headerOffset);
		if (num != 62218)
		{
			_logDetail($"EXT4 Extent: 无效 magic 0x{num:X4}");
			return null;
		}
		ushort num2 = BitConverter.ToUInt16(data, headerOffset + 2);
		BitConverter.ToUInt16(data, headerOffset + 4);
		ushort num3 = BitConverter.ToUInt16(data, headerOffset + 6);
		_logDetail($"EXT4 Extent: depth={num3}, entries={num2}");
		if (num3 == 0)
		{
			return ReadExt4LeafExtents(read, data, headerOffset, num2, blockSize, maxSize);
		}
		return ReadExt4IndexExtents(read, data, headerOffset, num2, blockSize, maxSize, depth);
	}

	private byte[] ReadExt4LeafExtents(DeviceReadDelegate read, byte[] data, int headerOffset, int entries, uint blockSize, int maxSize)
	{
		List<byte> list = new List<byte>();
		int num = 0;
		for (int i = 0; i < entries; i++)
		{
			if (num >= maxSize)
			{
				break;
			}
			int num2 = headerOffset + 12 + i * 12;
			if (num2 + 12 > data.Length)
			{
				break;
			}
			BitConverter.ToUInt32(data, num2);
			ushort num3 = BitConverter.ToUInt16(data, num2 + 4);
			ushort num4 = BitConverter.ToUInt16(data, num2 + 6);
			uint num5 = BitConverter.ToUInt32(data, num2 + 8);
			bool flag = (num3 & 0x8000) != 0;
			int num6 = num3 & 0x7FFF;
			if (!flag && num6 != 0)
			{
				long num7 = (long)(num5 | ((ulong)num4 << 32));
				int num8 = Math.Min((int)(num6 * blockSize), maxSize - num);
				if (num8 <= 0)
				{
					break;
				}
				byte[] array = read(num7 * blockSize, num8);
				if (array != null)
				{
					list.AddRange(array);
					num += array.Length;
				}
			}
		}
		if (list.Count <= 0)
		{
			return null;
		}
		return list.ToArray();
	}

	private byte[] ReadExt4IndexExtents(DeviceReadDelegate read, byte[] data, int headerOffset, int entries, uint blockSize, int maxSize, int depth)
	{
		List<byte> list = new List<byte>();
		int num = 0;
		for (int i = 0; i < entries; i++)
		{
			if (num >= maxSize)
			{
				break;
			}
			int num2 = headerOffset + 12 + i * 12;
			if (num2 + 12 > data.Length)
			{
				break;
			}
			uint num3 = BitConverter.ToUInt32(data, num2 + 4);
			ushort num4 = BitConverter.ToUInt16(data, num2 + 8);
			long num5 = (long)(num3 | ((ulong)num4 << 32));
			byte[] array = read(num5 * blockSize, (int)blockSize);
			if (array != null && array.Length >= 12)
			{
				byte[] array2 = ReadExt4ExtentDataRecursive(read, array, 0, blockSize, maxSize - num, depth + 1);
				if (array2 != null)
				{
					list.AddRange(array2);
					num += array2.Length;
				}
			}
		}
		if (list.Count <= 0)
		{
			return null;
		}
		return list.ToArray();
	}

	private List<Tuple<uint, string, byte>> ParseExt4DirectoryEntries(byte[] dirData)
	{
		List<Tuple<uint, string, byte>> list = new List<Tuple<uint, string, byte>>();
		if (dirData == null || dirData.Length < 12)
		{
			return list;
		}
		try
		{
			int num = 0;
			while (num + 8 <= dirData.Length)
			{
				uint num2 = BitConverter.ToUInt32(dirData, num);
				ushort num3 = BitConverter.ToUInt16(dirData, num + 4);
				byte b = dirData[num + 6];
				byte item = dirData[num + 7];
				if (num3 < 8 || num3 > dirData.Length - num)
				{
					break;
				}
				if (num2 == 0)
				{
					num += num3;
					continue;
				}
				if (b > 0 && num + 8 + b <= dirData.Length)
				{
					string text = Encoding.UTF8.GetString(dirData, num + 8, b);
					if (text != "." && text != "..")
					{
						list.Add(Tuple.Create(num2, text, item));
					}
				}
				num += num3;
			}
		}
		catch (Exception ex)
		{
			_logDetail("[EXT4] 解析目录项异常: " + ex.Message);
		}
		return list;
	}

	private byte[] ReadExt4DirectoryByInode(DeviceReadDelegate read, uint inodeNum, long inodeTableBlock, uint blockSize, ushort inodeSize, uint inodesPerGroup, uint blocksPerGroup, bool is64Bit, long bgdtOffset, int bgdSize)
	{
		try
		{
			uint num = (inodeNum - 1) / inodesPerGroup;
			uint num2 = (inodeNum - 1) % inodesPerGroup;
			long num3 = inodeTableBlock;
			if (num != 0)
			{
				byte[] array = read(bgdtOffset + num * bgdSize, bgdSize);
				if (array != null && array.Length >= bgdSize)
				{
					uint num4 = BitConverter.ToUInt32(array, 8);
					uint num5 = (is64Bit ? BitConverter.ToUInt32(array, 40) : 0u);
					num3 = (long)(num4 | ((ulong)num5 << 32));
				}
			}
			long offsetInSuper = num3 * blockSize + num2 * inodeSize;
			byte[] array2 = read(offsetInSuper, inodeSize);
			if (array2 == null || array2.Length < 128)
			{
				return null;
			}
			if ((BitConverter.ToUInt16(array2, 0) & 0xF000) != 16384)
			{
				return null;
			}
			uint val = BitConverter.ToUInt32(array2, 4);
			if ((BitConverter.ToUInt32(array2, 32) & 0x80000) != 0)
			{
				return ReadExt4ExtentData(read, array2, blockSize, (int)Math.Min(val, blockSize * 4));
			}
			uint num6 = BitConverter.ToUInt32(array2, 40);
			if (num6 != 0)
			{
				return read((long)num6 * (long)blockSize, (int)Math.Min(val, blockSize * 4));
			}
		}
		catch (Exception ex)
		{
			_logDetail("[EXT4] 读取目录数据异常: " + ex.Message);
		}
		return null;
	}

	private BuildPropInfo ReadExt4FileByInode(DeviceReadDelegate read, uint inodeNum, long inodeTableBlock, uint blockSize, ushort inodeSize, uint inodesPerGroup)
	{
		try
		{
			uint num = (inodeNum - 1) % inodesPerGroup;
			long offsetInSuper = inodeTableBlock * blockSize + num * inodeSize;
			byte[] array = read(offsetInSuper, inodeSize);
			if (array == null || array.Length < 128)
			{
				return null;
			}
			if ((BitConverter.ToUInt16(array, 0) & 0xF000) != 32768)
			{
				return null;
			}
			uint val = BitConverter.ToUInt32(array, 4);
			bool num2 = (BitConverter.ToUInt32(array, 32) & 0x80000) != 0;
			int num3 = (int)Math.Min(val, 65536u);
			byte[] array2 = null;
			if (num2)
			{
				array2 = ReadExt4ExtentData(read, array, blockSize, num3);
			}
			else
			{
				uint num4 = BitConverter.ToUInt32(array, 40);
				if (num4 != 0)
				{
					array2 = read((long)num4 * (long)blockSize, num3);
				}
			}
			if (array2 != null && array2.Length != 0)
			{
				string content = Encoding.UTF8.GetString(array2);
				return ParseBuildProp(content);
			}
		}
		catch (Exception ex)
		{
			_logDetail("[EXT4] 读取文件异常: " + ex.Message);
		}
		return null;
	}

	public void ParseProInfo(byte[] data, DeviceFullInfo info)
	{
		if (data == null || data.Length < 1024)
		{
			return;
		}
		try
		{
			string text = ExtractString(data, 36, 32);
			if (string.IsNullOrEmpty(text) || !IsValidSerialNumber(text))
			{
				text = ExtractString(data, 56, 32);
			}
			if (IsValidSerialNumber(text))
			{
				info.HardwareSn = text;
				info.Sources["SN"] = "proinfo";
				_logDetail($"从 proinfo 发现序列号: {text}");
			}
			string text2 = ExtractString(data, 512, 64);
			if (!string.IsNullOrEmpty(text2) && text2.Contains("Lenovo"))
			{
				info.Model = text2.Replace("Lenovo", "").Trim();
				info.Brand = "Lenovo";
				_logDetail($"从 proinfo 发现型号: {text2}");
			}
		}
		catch (Exception ex)
		{
			_log("解析 proinfo 失败: " + ex.Message);
		}
	}

	public void ParseDevInfo(byte[] data, DeviceFullInfo info)
	{
		if (data == null || data.Length < 512)
		{
			return;
		}
		try
		{
			// ---- 尝试解析 ANDROID-BOOT! 结构体 (参考 edl 项目 generic.py) ----
			// 主偏移 0x00 (ZTE 偏移 0x7FFE00 由 Controller 层单独处理)
			if (data.Length >= 0x10 + 13)
			{
				if (TryParseAndroidBootInfo(data, 0, info))
				{
					info.Sources["devinfo"] = "ANDROID-BOOT";
				}
			}

			// ---- 原有逻辑: 提取 SN / IMEI ----
			string text = ExtractString(data, 0, 32);
			if (IsValidSerialNumber(text) && string.IsNullOrEmpty(info.HardwareSn))
			{
				info.HardwareSn = text;
				info.Sources["SN"] = "devinfo";
				_logDetail($"从 devinfo 发现序列号: {text}");
			}
			string text2 = "";
			if (data.Length >= 1280)
			{
				text2 = ExtractImei(data, 1024);
			}
			if (string.IsNullOrEmpty(text2) && data.Length >= 2304)
			{
				text2 = ExtractImei(data, 2048);
			}
			if (!string.IsNullOrEmpty(text2))
			{
				info.Sources["IMEI"] = "devinfo";
				_logDetail($"从 devinfo 发现 IMEI: {SensitiveDataPolicy.DisplayImei(text2)}");
			}
		}
		catch (Exception ex)
		{
			_log("解析 devinfo 失败: " + ex.Message);
		}
	}

	/// <summary>
	/// 从指定偏移处解析 ANDROID-BOOT! 结构体 (用于 ZTE 等分区内偏移场景)
	/// data 已从目标偏移处读取, baseOffset 通常为 0
	/// </summary>
	public void ParseDevInfoAtOffset(byte[] data, int baseOffset, DeviceFullInfo info, string sourceLabel)
	{
		if (data == null || data.Length < 13)
			return;
		try
		{
			if (TryParseAndroidBootInfo(data, baseOffset, info))
			{
				info.Sources["devinfo"] = sourceLabel;
			}
		}
		catch (Exception ex)
		{
			_log("解析 devinfo 失败 (" + sourceLabel + "): " + ex.Message);
		}
	}

	/// <summary>
	/// 解析 ANDROID-BOOT! 结构体 (参考 edl 项目 Modules/generic.py)
	/// 
	/// struct device_info {
	///     unsigned char magic[13];          // "ANDROID-BOOT!" @ 0x00
	///     // 对齐到 0x10
	///     bool is_unlocked;                 // @ 0x10
	///     bool is_tampered;                 // @ 0x14
	///     bool charger_screen_enabled;      // @ 0x18
	///     char display_panel[64];           // @ 0x1C
	///     char bootloader_version[64];      // @ 0x5C
	///     char radio_version[64];           // @ 0x9C
	///     bool verity_mode;                 // @ 0xDC (1=enforcing, 0=logging)
	///     bool is_unlock_critical;          // @ 0xE0
	/// };
	/// </summary>
	private bool TryParseAndroidBootInfo(byte[] data, int baseOffset, DeviceFullInfo info)
	{
		const string MAGIC = "ANDROID-BOOT!";
		const int MAGIC_SIZE = 13;

		if (baseOffset + MAGIC_SIZE > data.Length)
			return false;

		string magic = Encoding.ASCII.GetString(data, baseOffset, MAGIC_SIZE);
		if (magic != MAGIC)
			return false;

		_logDetail($"[DevInfo] 发现 ANDROID-BOOT! 魔数 @ 偏移 0x{baseOffset:X}");

		// is_unlocked @ 0x10 (单字节有效, 4字节对齐; edl 项目 size_in_bytes=1)
		if (baseOffset + 0x11 <= data.Length)
		{
			info.BootloaderUnlocked = data[baseOffset + 0x10] != 0;
			_logDetail($"[DevInfo] BL解锁: {info.BootloaderUnlocked.Value}");
		}

		// is_tampered @ 0x14
		if (baseOffset + 0x15 <= data.Length)
		{
			info.BootloaderTampered = data[baseOffset + 0x14] != 0;
			_logDetail($"[DevInfo] 篡改标志: {info.BootloaderTampered.Value}");
		}

		// ---- 检测 VBOOT_MOTA 变体 ----
		// VBOOT_MOTA: 0x18=is_verified, 0x1C=charger_screen, 0x20=display_panel(64)
		//             0x60=bootloader_version(64), 0xA0=radio_version(64), 0xE0=is_unlock_critical
		// 标准:       0x18=charger_screen, 0x1C=display_panel(64)
		//             0x5C=bootloader_version(64), 0x9C=radio_version(64), 0xDC=verity, 0xE0=unlock_critical
		//
		// 启发式: 若 0x1C 处第一个字节是可打印 ASCII (0x20-0x7E), 则为标准布局 (display_panel 起始)
		//         若 0x1C 处为 0x00 或 0x01 (bool 值), 且 0x20 处为可打印 ASCII, 则为 VBOOT_MOTA
		bool isVbootMota = false;
		if (baseOffset + 0x21 <= data.Length)
		{
			byte at1C = data[baseOffset + 0x1C];
			byte at20 = data[baseOffset + 0x20];
			if ((at1C == 0x00 || at1C == 0x01) && at20 >= 0x20 && at20 <= 0x7E)
			{
				isVbootMota = true;
				_logDetail("[DevInfo] 检测到 VBOOT_MOTA 布局");
			}
		}

		int panelOffset, blVerOffset, radioVerOffset, verityOffset, unlockCritOffset;
		if (isVbootMota)
		{
			// VBOOT_MOTA: charger_screen @ 0x1C, display_panel @ 0x20
			if (baseOffset + 0x19 <= data.Length)
				info.ChargerScreenEnabled = data[baseOffset + 0x18] != 0; // is_verified 实际上
			panelOffset = 0x20;
			blVerOffset = 0x60;
			radioVerOffset = 0xA0;
			verityOffset = -1; // VBOOT_MOTA 无 verity_mode 字段
			unlockCritOffset = 0xE0;
		}
		else
		{
			// 标准布局
			if (baseOffset + 0x19 <= data.Length)
				info.ChargerScreenEnabled = data[baseOffset + 0x18] != 0;
			panelOffset = 0x1C;
			blVerOffset = 0x5C;
			radioVerOffset = 0x9C;
			verityOffset = 0xDC;
			unlockCritOffset = 0xE0;
		}

		// display_panel (64 bytes)
		if (baseOffset + panelOffset + 64 <= data.Length)
		{
			string panel = ExtractString(data, baseOffset + panelOffset, 64);
			if (!string.IsNullOrEmpty(panel))
			{
				info.DisplayPanel = panel;
				_logDetail($"[DevInfo] 显示面板: {panel}");
			}
		}

		// bootloader_version (64 bytes)
		if (baseOffset + blVerOffset + 64 <= data.Length)
		{
			string blVer = ExtractString(data, baseOffset + blVerOffset, 64);
			if (!string.IsNullOrEmpty(blVer))
			{
				info.BootloaderVersion = blVer;
				_logDetail($"[DevInfo] 引导版本: {blVer}");
			}
		}

		// radio_version (64 bytes)
		if (baseOffset + radioVerOffset + 64 <= data.Length)
		{
			string radioVer = ExtractString(data, baseOffset + radioVerOffset, 64);
			if (!string.IsNullOrEmpty(radioVer))
			{
				info.RadioVersion = radioVer;
				_logDetail($"[DevInfo] 基带版本: {radioVer}");
			}
		}

		// verity_mode (标准布局才有)
		if (verityOffset >= 0 && baseOffset + verityOffset + 1 <= data.Length)
		{
			info.VerityMode = data[baseOffset + verityOffset] != 0;
		}

		// is_unlock_critical
		if (baseOffset + unlockCritOffset + 1 <= data.Length)
		{
			info.UnlockCritical = data[baseOffset + unlockCritOffset] != 0;
			_logDetail($"[DevInfo] 关键解锁: {info.UnlockCritical.Value}");
		}

		return true;
	}

	/// <summary>
	/// 解析 config 分区 OEM unlock 标志 (参考 edl 项目 Modules/generic.py)
	/// config 分区在偏移 0x7FFF 或 0x7FFFF 处存储 OEM 解锁字节
	/// </summary>
	public void ParseConfigPartition(byte[] data, DeviceFullInfo info)
	{
		if (data == null || data.Length < 0x8000)
		{
			return;
		}
		try
		{
			int sectorCount = data.Length / 4096;
			int offsetToCheck;
			if (sectorCount <= (0x8000 / 4096))
				offsetToCheck = 0x7FFF;
			else
				offsetToCheck = 0x7FFFF;

			if (offsetToCheck < data.Length)
			{
				byte val = data[offsetToCheck];
				info.ConfigOemUnlocked = val != 0;
				_logDetail($"[Config] OEM解锁标志 @ 0x{offsetToCheck:X} = 0x{val:X2} ({(val != 0 ? "已解锁" : "未解锁")})");
				info.Sources["config"] = "OEM-Unlock";
			}
		}
		catch (Exception ex)
		{
			_log("解析 config 分区失败: " + ex.Message);
		}
	}

	private string ExtractString(byte[] data, int offset, int maxLength)
	{
		if (offset >= data.Length)
		{
			return "";
		}
		int i;
		for (i = 0; i < maxLength && offset + i < data.Length && data[offset + i] != 0 && data[offset + i] >= 32 && data[offset + i] <= 126; i++)
		{
		}
		if (i == 0)
		{
			return "";
		}
		return Encoding.ASCII.GetString(data, offset, i).Trim();
	}

	private string ExtractImei(byte[] data, int offset)
	{
		string text = ExtractString(data, offset, 32);
		if (text.Length >= 14 && text.All(char.IsDigit))
		{
			return text;
		}
		return "";
	}

	private bool IsValidSerialNumber(string sn)
	{
		if (string.IsNullOrEmpty(sn) || sn.Length < 8)
		{
			return false;
		}
		return sn.All((char c) => char.IsLetterOrDigit(c));
	}

	public DeviceFullInfo GetInfoFromQualcommService(QualcommService service)
	{
		DeviceFullInfo deviceFullInfo = new DeviceFullInfo();
		if (service == null)
		{
			return deviceFullInfo;
		}
		QualcommChipInfo chipInfo = service.ChipInfo;
		if (chipInfo != null)
		{
			deviceFullInfo.ChipSerial = chipInfo.SerialHex;
			deviceFullInfo.ChipName = chipInfo.ChipName;
			deviceFullInfo.HwId = chipInfo.HwIdHex;
			deviceFullInfo.PkHash = chipInfo.PkHash;
			deviceFullInfo.Vendor = QualcommDatabase.ResolveVendor(chipInfo.OemId, chipInfo.PkHash);
			if (QualcommDatabase.IsUnknownVendor(deviceFullInfo.Vendor) && !QualcommDatabase.IsUnknownVendor(chipInfo.Vendor))
			{
				deviceFullInfo.Vendor = chipInfo.Vendor;
			}
			deviceFullInfo.Sources["ChipInfo"] = "Sahara";
		}
		deviceFullInfo.StorageType = service.StorageType;
		deviceFullInfo.SectorSize = service.SectorSize;
		deviceFullInfo.Sources["Storage"] = "Firehose";
		return deviceFullInfo;
	}

	private void MergeInfo(DeviceFullInfo target, DeviceFullInfo source)
	{
		if (!string.IsNullOrEmpty(source.MarketName) && string.IsNullOrEmpty(target.MarketName))
		{
			target.MarketName = source.MarketName;
		}
		if (!string.IsNullOrEmpty(source.Model) && string.IsNullOrEmpty(target.Model))
		{
			target.Model = source.Model;
		}
		if (!string.IsNullOrEmpty(source.ChipName) && string.IsNullOrEmpty(target.ChipName))
		{
			target.ChipName = source.ChipName;
		}
		if (!string.IsNullOrEmpty(source.OtaVersion) && string.IsNullOrEmpty(target.OtaVersion))
		{
			target.OtaVersion = source.OtaVersion;
		}
		if (!string.IsNullOrEmpty(source.OplusProject) && string.IsNullOrEmpty(target.OplusProject))
		{
			target.OplusProject = source.OplusProject;
		}
		if (!string.IsNullOrEmpty(source.OplusNvId) && string.IsNullOrEmpty(target.OplusNvId))
		{
			target.OplusNvId = source.OplusNvId;
		}
	}

	private void MergeFromBuildProp(DeviceFullInfo target, BuildPropInfo source)
	{
		if (!string.IsNullOrEmpty(source.Brand) && (string.IsNullOrEmpty(target.Brand) || target.Brand == "oplus"))
		{
			target.Brand = source.Brand;
		}
		if (!string.IsNullOrEmpty(source.Model) && string.IsNullOrEmpty(target.Model))
		{
			target.Model = source.Model;
		}
		if (!string.IsNullOrEmpty(source.MarketName) && string.IsNullOrEmpty(target.MarketName))
		{
			target.MarketName = source.MarketName;
		}
		if (!string.IsNullOrEmpty(source.MarketNameEn) && string.IsNullOrEmpty(target.MarketNameEn))
		{
			target.MarketNameEn = source.MarketNameEn;
		}
		if (string.IsNullOrEmpty(target.DeviceCodename))
		{
			if (!string.IsNullOrEmpty(source.Codename))
			{
				target.DeviceCodename = source.Codename;
			}
			else if (!string.IsNullOrEmpty(source.Device))
			{
				target.DeviceCodename = source.Device;
			}
			else if (!string.IsNullOrEmpty(source.DeviceName))
			{
				target.DeviceCodename = source.DeviceName;
			}
		}
		if (!string.IsNullOrEmpty(source.AndroidVersion) && string.IsNullOrEmpty(target.AndroidVersion))
		{
			target.AndroidVersion = source.AndroidVersion;
		}
		if (!string.IsNullOrEmpty(source.SdkVersion) && string.IsNullOrEmpty(target.SdkVersion))
		{
			target.SdkVersion = source.SdkVersion;
		}
		if (!string.IsNullOrEmpty(source.SecurityPatch) && string.IsNullOrEmpty(target.SecurityPatch))
		{
			target.SecurityPatch = source.SecurityPatch;
		}
		if (!string.IsNullOrEmpty(source.BuildId) && string.IsNullOrEmpty(target.BuildId))
		{
			target.BuildId = source.BuildId;
		}
		if (!string.IsNullOrEmpty(source.Fingerprint) && string.IsNullOrEmpty(target.Fingerprint))
		{
			target.Fingerprint = source.Fingerprint;
		}
		if (!string.IsNullOrEmpty(source.DisplayId) && string.IsNullOrEmpty(target.DisplayId))
		{
			target.DisplayId = source.DisplayId;
		}
		if (!string.IsNullOrEmpty(source.OtaVersion) && string.IsNullOrEmpty(target.OtaVersion))
		{
			string text = source.OtaVersion;
			if (text.StartsWith(".") && !string.IsNullOrEmpty(target.AndroidVersion))
			{
				text = target.AndroidVersion + ".0.0" + text;
			}
			target.OtaVersion = text;
		}
		if (!string.IsNullOrEmpty(source.BuildDate))
		{
			target.BuiltDate = source.BuildDate;
		}
		if (!string.IsNullOrEmpty(source.BuildUtc))
		{
			target.BuildTimestamp = source.BuildUtc;
		}
		if (!string.IsNullOrEmpty(source.BootSlot) && string.IsNullOrEmpty(target.CurrentSlot))
		{
			target.CurrentSlot = source.BootSlot;
			target.IsAbDevice = true;
		}
		if (!string.IsNullOrEmpty(source.OplusCpuInfo) && string.IsNullOrEmpty(target.OplusCpuInfo))
		{
			target.OplusCpuInfo = source.OplusCpuInfo;
		}
		if (!string.IsNullOrEmpty(source.OplusNvId) && string.IsNullOrEmpty(target.OplusNvId))
		{
			target.OplusNvId = source.OplusNvId;
		}
		if (!string.IsNullOrEmpty(source.OplusProject) && string.IsNullOrEmpty(target.OplusProject))
		{
			target.OplusProject = source.OplusProject;
		}
	}
}





