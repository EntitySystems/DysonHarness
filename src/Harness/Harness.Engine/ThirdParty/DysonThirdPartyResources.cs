namespace DysonHarness;

/// <summary>
/// Hardcoded third-party resource pins. Edit the release tag URL here to retarget downloads.
/// </summary>
public static class DysonThirdPartyResources
{
    public static class CliProxyApi
    {
        /// <summary>Pinned GitHub release tag page. Bump this to retarget downloads.</summary>
        public const string ReleaseTagUrl =
            "https://github.com/router-for-me/CLIProxyAPI/releases/tag/v7.2.102";

        public static string Tag => ParseTag(ReleaseTagUrl);

        /// <summary>Semver without leading <c>v</c> (e.g. <c>7.2.102</c>).</summary>
        public static string Version => Tag.StartsWith('v') || Tag.StartsWith('V')
            ? Tag[1..]
            : Tag;

        public static string DownloadBaseUrl =>
            $"https://github.com/router-for-me/CLIProxyAPI/releases/download/{Tag}/";

        /// <summary>Parse <c>vX.Y.Z</c> / <c>X.Y.Z</c> from a GitHub release tag URL.</summary>
        public static string ParseTag(string releaseTagUrl)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(releaseTagUrl);
            var trimmed = releaseTagUrl.Trim().TrimEnd('/');
            var slash = trimmed.LastIndexOf('/');
            if (slash < 0 || slash == trimmed.Length - 1)
                throw new ArgumentException($"Cannot parse release tag from URL: {releaseTagUrl}", nameof(releaseTagUrl));

            var tag = trimmed[(slash + 1)..];
            if (tag.Length == 0)
                throw new ArgumentException($"Cannot parse release tag from URL: {releaseTagUrl}", nameof(releaseTagUrl));

            return tag;
        }
    }
}
