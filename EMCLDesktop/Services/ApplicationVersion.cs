using System.Reflection;

namespace EMCLDesktop.Services;

internal static class ApplicationVersion
{
    public static string Current { get; } = GetVersion();

    private static string GetVersion()
    {
        var assembly = typeof(App).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
