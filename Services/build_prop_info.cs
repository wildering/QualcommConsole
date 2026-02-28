using System.Collections.Generic;

namespace WackeEdl.Qualcomm.Services;

public class BuildPropInfo
{
	public string Brand { get; set; }

	public string Model { get; set; }

	public string Product { get; set; }

	public string DevProduct { get; set; }

	public string Device { get; set; }

	public string DeviceName { get; set; }

	public string Codename { get; set; }

	public string MarketName { get; set; }

	public string MarketNameEn { get; set; }

	public string MarketRegion { get; set; }

	public string Region { get; set; }

	public string Manufacturer { get; set; }

	public string AndroidVersion { get; set; }

	public string SdkVersion { get; set; }

	public string SecurityPatch { get; set; }

	public string BuildId { get; set; }

	public string Fingerprint { get; set; }

	public string DisplayId { get; set; }

	public string OtaVersion { get; set; }

	public string OtaVersionFull { get; set; }

	public string Incremental { get; set; }

	public string BuildDate { get; set; }

	public string BuildUtc { get; set; }

	public string BootSlot { get; set; }

	public string OplusCpuInfo { get; set; }

	public string OplusNvId { get; set; }

	public string OplusProject { get; set; }

	public string LenovoSeries { get; set; }

	public string SourceEngine { get; set; }

	public string SourcePartition { get; set; }

	public string SourcePath { get; set; }

	public int SourceConfidence { get; set; }

	public Dictionary<string, string> AllProperties { get; set; }

	public BuildPropInfo()
	{
		Brand = "";
		Model = "";
		Device = "";
		DeviceName = "";
		Codename = "";
		MarketName = "";
		MarketNameEn = "";
		Manufacturer = "";
		AndroidVersion = "";
		SdkVersion = "";
		SecurityPatch = "";
		BuildId = "";
		Fingerprint = "";
		DisplayId = "";
		OtaVersion = "";
		OtaVersionFull = "";
		Incremental = "";
		BuildDate = "";
		BuildUtc = "";
		BootSlot = "";
		OplusCpuInfo = "";
		OplusNvId = "";
		OplusProject = "";
		LenovoSeries = "";
		SourceEngine = "";
		SourcePartition = "";
		SourcePath = "";
		SourceConfidence = 0;
		AllProperties = new Dictionary<string, string>();
	}
}
