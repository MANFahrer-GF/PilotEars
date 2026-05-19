namespace PilotEars;

// String-only DTO so we can safely shuttle device info between threads
// without dragging COM-bound MMDevice objects across apartments.
public sealed record DeviceItem(string Id, string FriendlyName)
{
    public override string ToString() => FriendlyName;
}
