using Xunit;

namespace Computerwelt.Playwright.Tests;

/// <summary>
/// What the browser may reach, decided without a browser.
/// </summary>
/// <remarks>
/// The policy is the security boundary of this package, so it is tested on its own rather
/// than only through a page: these cases run in microseconds and cover the shapes that a
/// scenario test would never think to type.
/// </remarks>
public sealed class NavigationPolicyTests
{
    private static NavigationPolicy Policy(params string[] hosts) =>
        new(new PlaywrightOptions { AllowedHosts = hosts });

    [Fact]
    public void Nothing_is_reachable_by_default()
    {
        var policy = Policy();

        Assert.True(policy.IsClosed);
        Assert.False(policy.IsAllowed("https://example.com/"));
        Assert.Contains("AllowedHosts", policy.Refusal("https://example.com/"), StringComparison.Ordinal);
    }

    [Fact]
    public void An_allowed_host_is_reachable_over_http_and_https()
    {
        var policy = Policy("example.com");

        Assert.True(policy.IsAllowed("https://example.com/page"));
        Assert.True(policy.IsAllowed("http://example.com/page"));
        Assert.False(policy.IsAllowed("https://evil.com/page"));
    }

    [Fact]
    public void A_host_does_not_allow_its_subdomains()
    {
        var policy = Policy("example.com");

        // The bare name is exact. `a.example.com` is a different server and often a
        // different owner, so it has to be asked for.
        Assert.False(policy.IsAllowed("https://a.example.com/"));
    }

    [Fact]
    public void A_wildcard_allows_the_domain_and_its_subdomains()
    {
        var policy = Policy("*.example.com");

        Assert.True(policy.IsAllowed("https://a.example.com/"));
        Assert.True(policy.IsAllowed("https://deep.a.example.com/"));
        Assert.True(policy.IsAllowed("https://example.com/"));
        Assert.False(policy.IsAllowed("https://notexample.com/"));
    }

    [Fact]
    public void A_suffix_that_is_not_a_label_boundary_does_not_match()
    {
        var policy = Policy("*.example.com");

        // `evil-example.com` ends with the same letters and is not a subdomain of anything.
        Assert.False(policy.IsAllowed("https://evilexample.com/"));
    }

    [Fact]
    public void A_port_in_the_pattern_pins_the_port()
    {
        var policy = Policy("localhost:8080");

        Assert.True(policy.IsAllowed("http://localhost:8080/app"));
        Assert.False(policy.IsAllowed("http://localhost:9090/app"));
    }

    [Fact]
    public void A_pattern_without_a_port_allows_any_port()
    {
        var policy = Policy("localhost");

        Assert.True(policy.IsAllowed("http://localhost:8080/"));
        Assert.True(policy.IsAllowed("http://localhost/"));
    }

    [Fact]
    public void A_star_allows_every_host()
    {
        var policy = Policy("*");

        Assert.False(policy.IsClosed);
        Assert.True(policy.IsAllowed("https://anything.example/"));
    }

    [Fact]
    public void The_file_scheme_is_refused_even_with_a_star()
    {
        var policy = Policy("*");

        // The one that matters: `file:` would hand the browser the host's disk, which
        // nothing else in this sandbox can reach.
        Assert.False(policy.IsAllowed("file:///etc/passwd"));
        Assert.False(policy.IsAllowed("file://localhost/etc/passwd"));
    }

    [Fact]
    public void Schemes_outside_the_list_are_refused()
    {
        var policy = Policy("*");

        Assert.False(policy.IsAllowed("ftp://example.com/x"));
        Assert.False(policy.IsAllowed("chrome://settings"));
        Assert.False(policy.IsAllowed("javascript:alert(1)"));
    }

    [Fact]
    public void Content_bearing_urls_need_no_host()
    {
        var policy = Policy();

        // `data:` and `about:blank` name no server, so a closed policy still allows them —
        // which is what makes `set_content` and a blank page usable with no allowlist.
        Assert.True(policy.IsAllowed("about:blank"));
        Assert.True(policy.IsAllowed("data:text/html,<p>hi</p>"));
    }

    [Fact]
    public void Something_that_will_not_parse_is_refused()
    {
        var policy = Policy("*");

        Assert.False(policy.IsAllowed("not a url"));
        Assert.False(policy.IsAllowed(string.Empty));
        Assert.False(policy.IsAllowed(null));
    }

    [Fact]
    public void Matching_ignores_case_in_the_host()
    {
        var policy = Policy("Example.COM");

        Assert.True(policy.IsAllowed("https://EXAMPLE.com/"));
    }
}
