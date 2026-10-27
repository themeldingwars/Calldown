using System.Reflection;

namespace Calldown.AppHost;

internal sealed record TmwPaths(string PinRoot, string RinRoot, string DropPodRoot)
{
    public static TmwPaths FromAssembly()
    {
        var metadata = typeof(TmwPaths)
            .Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(a => a.Key, a => a.Value ?? string.Empty);

        return new TmwPaths(metadata["PinRoot"], metadata["RinRoot"], metadata["DropPodRoot"]);
    }
}
