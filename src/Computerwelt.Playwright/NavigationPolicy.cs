using System.Globalization;

namespace Computerwelt.Playwright;

/// <summary>
/// Decides which URLs a page in the sandbox may load.
/// </summary>
/// <remarks>
/// The sandbox's rule for the network is that nothing is reachable until a host names it,
/// and a browser does not change that — it only makes the surface wider, because a page
/// fetches on its own behalf. So the policy is applied to every request the browser makes
/// and not only to the navigation a script typed.
/// </remarks>
public sealed class NavigationPolicy
{
    private readonly string[] _hosts;
    private readonly HashSet<string> _schemes;
    private readonly bool _anyHost;

    /// <summary>Builds the policy an <see cref="PlaywrightOptions"/> describes.</summary>
    public NavigationPolicy(PlaywrightOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _hosts = [.. options.AllowedHosts.Select(static h => h.Trim().ToLowerInvariant()).Where(static h => h.Length > 0)];
        _anyHost = _hosts.Contains("*", StringComparer.Ordinal);
        _schemes = new HashSet<string>(
            options.AllowedSchemes.Select(static s => s.Trim().TrimEnd(':').ToLowerInvariant()),
            StringComparer.Ordinal);
    }

    /// <summary>True when no host at all was allowed, so the browser can reach nothing.</summary>
    public bool IsClosed => _hosts.Length == 0;

    /// <summary>
    /// Whether <paramref name="url"/> may be loaded.
    /// </summary>
    /// <remarks>
    /// A URL that will not parse is refused. That is not pedantry: the check exists to be
    /// the thing that decides, and something it cannot read is something it cannot decide
    /// about.
    /// </remarks>
    public bool IsAllowed(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }

        // `about:blank` is the page a fresh tab already shows, and no request leaves the
        // browser for it.
        if (string.Equals(url, "about:blank", StringComparison.OrdinalIgnoreCase))
        {
            return _schemes.Contains("about");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            return false;
        }

        var scheme = parsed.Scheme.ToLowerInvariant();

        if (!_schemes.Contains(scheme))
        {
            return false;
        }

        // A `data:` or `blob:` URL carries its own content and names no host, so the host
        // list has nothing to say about it.
        if (scheme is "data" or "blob" or "about")
        {
            return true;
        }

        if (_anyHost)
        {
            return true;
        }

        var host = parsed.Host.ToLowerInvariant();
        var port = parsed.IsDefaultPort
            ? string.Empty
            : parsed.Port.ToString(CultureInfo.InvariantCulture);

        foreach (var pattern in _hosts)
        {
            if (Matches(pattern, host, port))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The sentence a refusal reports, which names the setting that would allow it.</summary>
    public string Refusal(string url) =>
        IsClosed
            ? $"navigating to {url} is denied: this sandbox has no allowed hosts. "
              + "Set PlaywrightOptions.AllowedHosts to name the ones it may reach."
            : $"navigating to {url} is denied: it is not in PlaywrightOptions.AllowedHosts.";

    private static bool Matches(string pattern, string host, string port)
    {
        var patternPort = string.Empty;
        var colon = pattern.LastIndexOf(':');

        // Only a trailing `:digits` is a port; an IPv6 literal is full of colons.
        if (colon > 0 && pattern[(colon + 1)..].All(char.IsAsciiDigit) && colon + 1 < pattern.Length)
        {
            patternPort = pattern[(colon + 1)..];
            pattern = pattern[..colon];
        }

        // A pattern with no port allows any port on that host; one with a port pins it.
        if (patternPort.Length > 0 && !string.Equals(patternPort, port, StringComparison.Ordinal))
        {
            return false;
        }

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = pattern[1..];

            // `*.example.com` covers `a.example.com` and `example.com` itself, which is what
            // a reader of that pattern expects it to mean.
            return host.EndsWith(suffix, StringComparison.Ordinal)
                || string.Equals(host, pattern[2..], StringComparison.Ordinal);
        }

        return string.Equals(pattern, host, StringComparison.Ordinal);
    }
}
