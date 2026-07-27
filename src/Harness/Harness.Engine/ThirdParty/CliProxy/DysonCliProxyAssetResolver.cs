using System.Runtime.InteropServices;

namespace DysonHarness;

/// <summary>Map OS/arch → CLIProxyAPI release asset file name.</summary>
public static class DysonCliProxyAssetResolver
{
    public static string ResolveAssetFileName(
        string version,
        OSPlatform? os = null,
        Architecture? arch = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        var osToken = ResolveOsToken(os);
        var archToken = ResolveArchToken(arch);
        var ext = osToken == "windows" ? "zip" : "tar.gz";
        return $"CLIProxyAPI_{version}_{osToken}_{archToken}.{ext}";
    }

    public static string ResolveDownloadUrl(string version, OSPlatform? os = null, Architecture? arch = null)
    {
        var fileName = ResolveAssetFileName(version, os, arch);
        return DysonThirdPartyResources.CliProxyApi.DownloadBaseUrl + fileName;
    }

    public static string ResolveOsToken(OSPlatform? os = null)
    {
        if (os is { } explicitOs)
        {
            if (explicitOs == OSPlatform.Windows) return "windows";
            if (explicitOs == OSPlatform.Linux) return "linux";
            if (explicitOs == OSPlatform.OSX) return "darwin";
            throw new PlatformNotSupportedException($"Unsupported OS platform: {explicitOs}");
        }

        if (OperatingSystem.IsWindows()) return "windows";
        if (OperatingSystem.IsLinux()) return "linux";
        if (OperatingSystem.IsMacOS()) return "darwin";
        throw new PlatformNotSupportedException($"Unsupported OS: {RuntimeInformation.OSDescription}");
    }

    public static string ResolveArchToken(Architecture? arch = null)
    {
        var a = arch ?? RuntimeInformation.ProcessArchitecture;
        return a switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "aarch64",
            _ => throw new PlatformNotSupportedException($"Unsupported architecture: {a}"),
        };
    }
}
