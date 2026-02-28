namespace WackeEdl.Qualcomm.Protocol;

public struct VipSpoofStrategy
{
    public string Filename { get; private set; }

    public string Label { get; private set; }

    public int Priority { get; private set; }

    public VipSpoofStrategy(string filename, string label, int priority)
    {
        Filename = filename;
        Label = label;
        Priority = priority;
    }

    public override string ToString()
    {
        return $"{Label}/{Filename}";
    }
}
