namespace Torrentarr.Core;

public static class InstanceContext
{
    private static readonly AsyncLocal<string?> _instanceName = new();
    
    public static string? Current
    {
        get => _instanceName.Value;
        set => _instanceName.Value = value;
    }
}
