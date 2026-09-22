using System.Reflection;

namespace WakePcBridge;

public static class BridgeVersion
{
    public static string Current { get; } = Resolve();

    private static string Resolve()
    {
        Assembly assembly = typeof(BridgeVersion).Assembly;
        string? informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion
            .Split('+', 2)[0];
        if (!string.IsNullOrWhiteSpace(informational))
        {
            return informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "Unknown";
    }
}
