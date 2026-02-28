namespace WackeEdl.Qualcomm.Services;

public class LpPartitionInfo
{
	public string Name { get; set; }

	public uint Attrs { get; set; }

	public long RelativeSector { get; set; }

	public long AbsoluteSector { get; set; }

	public long SizeInSectors { get; set; }

	public long Size { get; set; }

	public string FileSystem { get; set; }

	public LpPartitionInfo()
	{
		Name = "";
		FileSystem = "unknown";
	}
}
