namespace PilotEars;

public sealed class PresetData
{
    public string Name { get; set; } = "";
    public float TargetDb { get; set; } = -18f;
    public float CeilingDb { get; set; } = -1f;
    public float ReleaseMs { get; set; } = 50f;
    public float LookaheadMs { get; set; } = 5f;
    public float Pan { get; set; } = 0f;
}
