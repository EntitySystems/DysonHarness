using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace DysonHarness;

/// <summary>Shared HTTP + SSRF guard for in-process search / fetch tools.</summary>
public static class SearchHttp
{
    public static readonly HttpClient Client = CreateClient();

    private static readonly HashSet<string> BlockedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "127.0.0.1",
        "0.0.0.0",
        "::1",
        "169.254.169.254",
        "metadata.google.internal",
        "100.100.100.200",
        "kubernetes.default.svc",
    };

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
        })
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        // Browser UA keeps HTML SERP scrapes working; Api-User-Agent identifies us to Wikipedia.
        // ponytail: if MediaWiki starts rejecting browser UA, switch User-Agent to the Api-User-Agent value.
        const string identity = "DysonHarness/1.0 (+https://github.com/EntitySystems/DysonHarness)";
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Api-User-Agent", identity);
        return client;
    }

    /// <summary>Rejects non-http(s), localhost, metadata hosts, and private IP ranges.</summary>
    public static VoidResult<string> ValidateUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return new VoidResult<string>("Invalid URL");

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return new VoidResult<string>("Invalid URL");

        if (uri.Scheme is not ("http" or "https"))
            return new VoidResult<string>("Only http/https protocols allowed");

        var hostname = uri.IdnHost;
        if (hostname.StartsWith('[') && hostname.EndsWith(']'))
            hostname = hostname[1..^1];

        if (BlockedHosts.Contains(hostname))
            return new VoidResult<string>("Blocked host");

        if (hostname.StartsWith("::ffff:", StringComparison.OrdinalIgnoreCase))
        {
            var mapped = DecodeMappedV4(hostname["::ffff:".Length..]);
            if (mapped is not null)
            {
                if (BlockedHosts.Contains(mapped) || IsBlockedIpv4(mapped))
                    return new VoidResult<string>("Blocked IPv4-mapped IPv6 host");
            }
        }

        if (IsBlockedIpv4(hostname))
            return new VoidResult<string>("Blocked IP range");

        if (IPAddress.TryParse(hostname, out var ip) && IsPrivateOrLoopback(ip))
            return new VoidResult<string>("Blocked IP range");

        return VoidResult<string>.Success;
    }

    private static string? DecodeMappedV4(string suffix)
    {
        // ::ffff:7f00:1 or ::ffff:127.0.0.1
        if (IPAddress.TryParse("::ffff:" + suffix, out var ip))
        {
            var bytes = ip.MapToIPv4().GetAddressBytes();
            return $"{bytes[0]}.{bytes[1]}.{bytes[2]}.{bytes[3]}";
        }

        var parts = suffix.Split(':');
        if (parts.Length != 2)
            return null;

        try
        {
            var a = Convert.ToInt32(parts[0].PadLeft(4, '0')[..2], 16);
            var b = Convert.ToInt32(parts[0].PadLeft(4, '0')[2..4], 16);
            var c = Convert.ToInt32(parts[1].PadLeft(4, '0')[..2], 16);
            var d = Convert.ToInt32(parts[1].PadLeft(4, '0')[2..4], 16);
            return $"{a}.{b}.{c}.{d}";
        }
        catch
        {
            return null;
        }
    }

    private static bool IsBlockedIpv4(string hostname)
    {
        if (!Regex.IsMatch(hostname, @"^\d{1,3}(\.\d{1,3}){3}$"))
            return false;

        return Regex.IsMatch(hostname, @"^10\.")
            || Regex.IsMatch(hostname, @"^172\.(1[6-9]|2\d|3[01])\.")
            || Regex.IsMatch(hostname, @"^192\.168\.")
            || Regex.IsMatch(hostname, @"^127\.")
            || hostname.StartsWith("169.254.", StringComparison.Ordinal);
    }

    private static bool IsPrivateOrLoopback(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal)
                return true;
            if (ip.IsIPv4MappedToIPv6)
                return IsPrivateOrLoopback(ip.MapToIPv4());
            return false;
        }

        var b = ip.GetAddressBytes();
        if (b.Length != 4)
            return false;

        return b[0] == 10
            || b[0] == 127
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }
}
