using Computerwelt.Emulation.Bash;
using Xunit;

namespace Computerwelt.Playwright.Tests;

/// <summary>
/// One browser for the whole suite.
/// </summary>
/// <remarks>
/// <para>
/// A browser costs a second to start and the tests share it exactly as two tenants would,
/// which is the arrangement the package is designed around: one process, one context per
/// script, nothing shared that a script can observe.
/// </para>
/// <para>
/// The pages the scenarios drive are <c>data:</c> URLs and <c>set_content</c>, so the suite
/// needs no network and no server — and the one navigation that does name a host is there to
/// be refused.
/// </para>
/// </remarks>
public sealed class BrowserFixture : IAsyncLifetime
{
    /// <summary>The host's browser session, or null when no browser could be started.</summary>
    public PlaywrightSession? Session { get; private set; }

    /// <summary>Why there is no session, when there is none.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>The options the suite runs under: a browser, and no host reachable.</summary>
    public static PlaywrightOptions Options => new()
    {
        Browsers = [PlaywrightBrowser.Chromium],
        Headless = true,
        DefaultTimeout = 10_000,

        // Deliberately closed. Every scenario that needs content carries it in the page,
        // and the one that names a host is asserting that it is refused.
        AllowedHosts = [],
    };

    /// <summary>A joined session whose <c>python</c> can drive the browser.</summary>
    public Bash NewShell() =>
        Bash.CreateBuilder()
            .WithWorkingDirectory("/")
            .WithPlaywright(Session ?? throw new InvalidOperationException(Unavailable))
            .Build();

    public async Task InitializeAsync()
    {
        try
        {
            var session = await PlaywrightSession.CreateAsync(Options);

            try
            {
                // Launched here rather than lazily, so that a missing browser is reported
                // once, by name, instead of failing every scenario with the same message.
                await session.GetBrowserAsync("chromium");
            }
            catch (Microsoft.Playwright.PlaywrightException)
            {
                // The package's own installer, used the way a host would use it — which is
                // also the only way this suite dogfoods it.
                var install = await PlaywrightBrowsers.InstallAsync(Options.Browsers);

                if (!install.Succeeded)
                {
                    Unavailable = $"no chromium, and installing one failed: {install}";
                    await session.DisposeAsync();
                    return;
                }

                await session.GetBrowserAsync("chromium");
            }

            Session = session;
        }
        catch (Exception exception)
        {
            Unavailable = $"the Playwright driver did not start: {exception.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (Session is { } session)
        {
            await session.DisposeAsync();
        }
    }
}

/// <summary>The collection that shares one browser across the suite's classes.</summary>
[CollectionDefinition(Name)]
public sealed class BrowserCollection : ICollectionFixture<BrowserFixture>
{
    public const string Name = "browser";
}
