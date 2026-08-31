using System.Text;
using Computerwelt.Emulation.Bash;
using Xunit;

namespace Computerwelt.Playwright.Tests;

/// <summary>
/// What happens to a browser context when the script that opened it stops caring.
/// </summary>
/// <remarks>
/// The scenarios all use <c>with sync_playwright()</c>, because that is what a person
/// writes. These cover what a person writes when they are in a hurry, or when the program
/// ends on an exception — the cases a shared browser actually needs protecting from, and the
/// ones that would leak a context per run until the session was disposed.
/// </remarks>
[Collection(BrowserCollection.Name)]
public sealed class LifetimeTests
{
    private readonly BrowserFixture _browser;

    public LifetimeTests(BrowserFixture browser) => _browser = browser;

    private static bool Skipped =>
        Environment.GetEnvironmentVariable("COMPUTERWELT_SKIP_PLAYWRIGHT") == "1";

    private async Task<ExecResult> RunAsync(string program)
    {
        var bash = _browser.NewShell();

        await bash.FileSystem.WriteFileAsync(VPath.Parse("/run.py"), Encoding.UTF8.GetBytes(program));
        return await bash.ExecAsync("python /run.py");
    }

    [Fact]
    public async Task A_script_that_never_closes_anything_still_gives_its_contexts_back()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        var session = _browser.Session!;
        var before = session.OpenContexts;

        // No `with`, no `stop()`, no `close()`: three contexts and a page, abandoned.
        var result = await RunAsync("""
            from playwright.sync_api import sync_playwright

            p = sync_playwright().start()
            browser = p.chromium.launch()

            browser.new_context()
            browser.new_context()
            browser.new_page()

            print('done')
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("done\n", result.Stdout.ToString());

        // The run ended, so the contexts went back whether the program asked or not.
        Assert.Equal(before, session.OpenContexts);
    }

    [Fact]
    public async Task A_script_that_raises_still_gives_its_contexts_back()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        var session = _browser.Session!;
        var before = session.OpenContexts;

        var result = await RunAsync("""
            from playwright.sync_api import sync_playwright

            p = sync_playwright().start()
            p.chromium.launch().new_page()

            raise RuntimeError('the program gave up here')
            """);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("the program gave up here", result.Stderr.ToString(), StringComparison.Ordinal);
        Assert.Equal(before, session.OpenContexts);
    }

    [Fact]
    public async Task A_run_that_hit_its_instruction_limit_still_gives_its_contexts_back()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        var session = _browser.Session!;
        var before = session.OpenContexts;

        // The case with no program left to run anything: the run is stopped from outside.
        var result = await RunAsync("""
            from playwright.sync_api import sync_playwright

            p = sync_playwright().start()
            p.chromium.launch().new_page()

            while True:
                pass
            """);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(before, session.OpenContexts);
    }

    [Fact]
    public async Task Two_runs_do_not_see_each_others_contexts()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        var first = await RunAsync("""
            from playwright.sync_api import sync_playwright

            p = sync_playwright().start()
            browser = p.chromium.launch()
            browser.new_context()

            print(len(browser.contexts))
            """);

        var second = await RunAsync("""
            from playwright.sync_api import sync_playwright

            with sync_playwright() as p:
                browser = p.chromium.launch()

                # The browser is the one the first run used. Its contexts are not.
                print(len(browser.contexts))
            """);

        Assert.Equal("1\n", first.Stdout.ToString());
        Assert.Equal("0\n", second.Stdout.ToString());
    }

    [Fact]
    public async Task Closing_the_same_context_twice_is_not_an_error()
    {
        if (Skipped)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        // `close()`, then the `with` block's exit, then the end of the run: three attempts
        // at the same context, and only the first does anything.
        var result = await RunAsync("""
            from playwright.sync_api import sync_playwright

            with sync_playwright() as p:
                context = p.chromium.launch().new_context()
                context.close()
                context.close()
                assert context.is_closed()

            print('ok')
            """);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("ok\n", result.Stdout.ToString());
        Assert.Equal(0, _browser.Session!.OpenContexts);
    }
}
