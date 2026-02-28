using System.Collections.Generic;
using System.Text;

namespace WackeEdl.Qualcomm.Services;

public class DeviceFullInfo
{
	public string ChipSerial { get; set; }

	public string ChipName { get; set; }

	public string HwId { get; set; }

	public string PkHash { get; set; }

	public string Vendor { get; set; }

	public string Brand { get; set; }

	public string Model { get; set; }

	public string Product { get; set; }

	public string DevProduct { get; set; }

	public string MarketName { get; set; }

	public string MarketNameEn { get; set; }

	public string MarketRegion { get; set; }

	public string Region { get; set; }

	public string DeviceCodename { get; set; }

	public string AndroidVersion { get; set; }

	public string SdkVersion { get; set; }

	public string SecurityPatch { get; set; }

	public string BuildId { get; set; }

	public string Fingerprint { get; set; }

	public string OtaVersion { get; set; }

	public string OtaVersionFull { get; set; }

	public string DisplayId { get; set; }

	public string BuiltDate { get; set; }

	public string BuildTimestamp { get; set; }

	public string StorageType { get; set; }

	public int SectorSize { get; set; }

	public bool IsAbDevice { get; set; }

	public string CurrentSlot { get; set; }

	public string OplusCpuInfo { get; set; }

	public string OplusNvId { get; set; }

	public string OplusProject { get; set; }

	public string LenovoSeries { get; set; }

	public string BuildPropEngine { get; set; }

	public string BuildPropSourcePartition { get; set; }

	public string BuildPropSourcePath { get; set; }

	public int BuildPropConfidence { get; set; }

	public string HardwareSn { get; set; }

	public string Imei1 { get; set; }

	public string Imei2 { get; set; }

	public bool? BootloaderUnlocked { get; set; }

	public bool? BootloaderTampered { get; set; }

	public bool? UnlockCritical { get; set; }

	public bool? ChargerScreenEnabled { get; set; }

	public bool? VerityMode { get; set; }

	public string DisplayPanel { get; set; }

	public string BootloaderVersion { get; set; }

	public string RadioVersion { get; set; }

	public bool? ConfigOemUnlocked { get; set; }

	public Dictionary<string, string> Sources { get; set; }

	public string DisplayName
	{
		get
		{
			if (!string.IsNullOrEmpty(MarketName))
			{
				return MarketName;
			}
			if (!string.IsNullOrEmpty(MarketNameEn))
			{
				return MarketNameEn;
			}
			if (!string.IsNullOrEmpty(Brand) && !string.IsNullOrEmpty(Model))
			{
				return Brand + " " + Model;
			}
			return Model;
		}
	}

	public DeviceFullInfo()
	{
		ChipSerial = "";
		ChipName = "";
		HwId = "";
		PkHash = "";
		Vendor = "";
		Brand = "";
		Model = "";
		MarketName = "";
		MarketNameEn = "";
		DeviceCodename = "";
		AndroidVersion = "";
		SdkVersion = "";
		SecurityPatch = "";
		BuildId = "";
		Fingerprint = "";
		OtaVersion = "";
		DisplayId = "";
		StorageType = "";
		CurrentSlot = "";
		OplusCpuInfo = "";
		OplusNvId = "";
		OplusProject = "";
		LenovoSeries = "";
		BuildPropEngine = "";
		BuildPropSourcePartition = "";
		BuildPropSourcePath = "";
		BuildPropConfidence = 0;
		HardwareSn = "";
		Imei1 = "";
		Imei2 = "";
		DisplayPanel = "";
		BootloaderVersion = "";
		RadioVersion = "";
		Sources = new Dictionary<string, string>();
	}

	public string GetSummary()
	{
		StringBuilder stringBuilder = new StringBuilder();
		if (!string.IsNullOrEmpty(DisplayName))
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(4, 1, stringBuilder2);
			handler.AppendLiteral("设备: ");
			handler.AppendFormatted(DisplayName);
			stringBuilder3.AppendLine(ref handler);
		}
		if (!string.IsNullOrEmpty(Model) && Model != DisplayName)
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(4, 1, stringBuilder2);
			handler.AppendLiteral("型号: ");
			handler.AppendFormatted(Model);
			stringBuilder4.AppendLine(ref handler);
		}
		if (!string.IsNullOrEmpty(ChipName) && ChipName != "Unknown")
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(4, 1, stringBuilder2);
			handler.AppendLiteral("芯片: ");
			handler.AppendFormatted(ChipName);
			stringBuilder5.AppendLine(ref handler);
		}
		if (!string.IsNullOrEmpty(AndroidVersion))
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(9, 1, stringBuilder2);
			handler.AppendLiteral("Android: ");
			handler.AppendFormatted(AndroidVersion);
			stringBuilder6.AppendLine(ref handler);
		}
		if (!string.IsNullOrEmpty(OtaVersion))
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder7 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(4, 1, stringBuilder2);
			handler.AppendLiteral("版本: ");
			handler.AppendFormatted(OtaVersion);
			stringBuilder7.AppendLine(ref handler);
		}
		if (!string.IsNullOrEmpty(StorageType))
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder8 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(4, 1, stringBuilder2);
			handler.AppendLiteral("存储: ");
			handler.AppendFormatted(StorageType.ToUpper());
			stringBuilder8.AppendLine(ref handler);
		}
		if (!string.IsNullOrEmpty(OplusProject))
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder9 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(6, 1, stringBuilder2);
			handler.AppendLiteral("项目ID: ");
			handler.AppendFormatted(OplusProject);
			stringBuilder9.AppendLine(ref handler);
		}
		if (!string.IsNullOrEmpty(OplusNvId))
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder10 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder2);
			handler.AppendLiteral("NV ID: ");
			handler.AppendFormatted(OplusNvId);
			stringBuilder10.AppendLine(ref handler);
		}
		if (!string.IsNullOrEmpty(LenovoSeries))
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder11 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(6, 1, stringBuilder2);
			handler.AppendLiteral("联想系列: ");
			handler.AppendFormatted(LenovoSeries);
			stringBuilder11.AppendLine(ref handler);
		}
		if (!string.IsNullOrEmpty(HardwareSn))
		{
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder12 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(7, 1, stringBuilder2);
			handler.AppendLiteral("硬件序列号: ");
			handler.AppendFormatted(HardwareSn);
			stringBuilder12.AppendLine(ref handler);
		}
		if (BootloaderUnlocked.HasValue)
			stringBuilder.AppendLine($"BL解锁: {(BootloaderUnlocked.Value ? "是" : "否")}");
		if (!string.IsNullOrEmpty(BootloaderVersion))
			stringBuilder.AppendLine($"引导版本: {BootloaderVersion}");
		if (!string.IsNullOrEmpty(RadioVersion))
			stringBuilder.AppendLine($"基带版本: {RadioVersion}");
		if (!string.IsNullOrEmpty(DisplayPanel))
			stringBuilder.AppendLine($"显示面板: {DisplayPanel}");
		if (ConfigOemUnlocked.HasValue)
			stringBuilder.AppendLine($"OEM解锁: {(ConfigOemUnlocked.Value ? "是" : "否")}");
		return stringBuilder.ToString().TrimEnd();
	}
}
