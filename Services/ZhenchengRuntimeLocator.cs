using System.IO;

namespace SpeedEmulator.Services;

public static class ZhenchengRuntimeLocator
{
    public const string VendorDirEnvName = "SPEEDEMULATOR_ZHENCHENG_DIR";
    public const string OutputDirectoryName = "zhencheng-runtime";
    public const string ProjectRuntimeDirectory = "VendorRuntime\\Zhencheng";
    public const string MainDllName = "\u771F\u8BDA\u8D22\u52A1\u8F6F\u4EF6.dll";

    private const string DefaultVendorDir = "D:\\\u771F\u8BDA\u8D22\u52A1\u8F6F\u4EF6";

    public static string ResolveRequired()
    {
        return Resolve()
            ?? throw new DirectoryNotFoundException(
                $"Zhencheng runtime was not found. Put authorized runtime files under {ProjectRuntimeDirectory} so build/publish can copy them to {OutputDirectoryName}, or set {VendorDirEnvName}.");
    }

    public static string? Resolve()
    {
        foreach (var directory in GetCandidateDirectories())
        {
            if (IsValidRuntimeDirectory(directory))
            {
                return directory;
            }
        }

        return null;
    }

    public static IReadOnlyList<string> GetCandidateDirectories()
    {
        var candidates = new List<string>();
        AddIfNotBlank(candidates, Environment.GetEnvironmentVariable(VendorDirEnvName));
        AddIfNotBlank(candidates, Path.Combine(AppContext.BaseDirectory, OutputDirectoryName));
        AddIfNotBlank(candidates, Path.Combine(AppContext.BaseDirectory, ProjectRuntimeDirectory));
        AddIfNotBlank(candidates, Path.Combine(Directory.GetCurrentDirectory(), OutputDirectoryName));
        AddIfNotBlank(candidates, Path.Combine(Directory.GetCurrentDirectory(), ProjectRuntimeDirectory));
        AddIfNotBlank(candidates, DefaultVendorDir);
        return candidates;
    }

    private static bool IsValidRuntimeDirectory(string directory)
    {
        return Directory.Exists(directory)
            && File.Exists(Path.Combine(directory, MainDllName));
    }

    private static void AddIfNotBlank(ICollection<string> candidates, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var fullPath = Path.GetFullPath(value);
        if (!candidates.Any(candidate => string.Equals(candidate, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            candidates.Add(fullPath);
        }
    }
}
