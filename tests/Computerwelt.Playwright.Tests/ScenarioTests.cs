using System.Text;
using Computerwelt.Emulation.Bash;

using Xunit;

namespace Computerwelt.Playwright.Tests;

/// <summary>
/// The package as a script sees it: Python programs run through the shell's
/// <c>python</c> command, against one virtual filesystem and one host-owned browser.
/// </summary>
/// <remarks>
/// <para>
/// Written as fixtures rather than as C# assertions, for the same reason the two conformance
/// corpora are: the thing under test is a Python API, so the test should be the Python a
/// caller would write. A scenario passes when its program runs to completion, and every
/// claim it makes is an <c>assert</c> a reader can check against upstream's documentation.
/// </para>
/// <para>
/// No scenario reaches the network. The pages are <c>set_content</c> and <c>data:</c> URLs,
/// and the one that names a real host is there to be refused.
/// </para>
/// </remarks>
[Collection(BrowserCollection.Name)]
public sealed class ScenarioTests
{
    /// <summary>
    /// The notice that stands in for the corpus when the suite is switched off.
    /// </summary>
    /// <remarks>
    /// The convention the extension corpus already uses: xunit 2 cannot skip at run time,
    /// and a case that quietly returns is a green tick for work that did not happen, so the
    /// opt-out replaces the data with one case whose name says what happened.
    /// </remarks>
    private const string SkipNotice = "(skipped: COMPUTERWELT_SKIP_PLAYWRIGHT=1)";

    private readonly BrowserFixture _browser;

    public ScenarioTests(BrowserFixture browser) => _browser = browser;

    private static bool Skipped =>
        Environment.GetEnvironmentVariable("COMPUTERWELT_SKIP_PLAYWRIGHT") == "1";

    public static TheoryData<string> Names =>
        Skipped ? [SkipNotice] : [.. Scenarios.Keys];

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Scenario_passes(string name)
    {
        if (name == SkipNotice)
        {
            return;
        }

        Assert.True(_browser.Unavailable is null, _browser.Unavailable);

        var bash = _browser.NewShell();

        await bash.FileSystem.WriteFileAsync(
            VPath.Parse("/scenario.py"),
            Encoding.UTF8.GetBytes(Preamble + "\n" + Scenarios[name]));

        var result = await bash.ExecAsync("python /scenario.py");

        Assert.True(
            result.ExitCode == 0,
            $"{name} failed:\n{result.Stderr}\nstdout:\n{result.Stdout}");
    }

    /// <summary>
    /// The opening every scenario shares: a browser, a context and a page.
    /// </summary>
    /// <remarks>
    /// Written the way the upstream documentation opens every example, because that is the
    /// shape this port has to accept — <c>with sync_playwright() as p:</c> is also what
    /// returns the context to the pool when the program ends.
    /// </remarks>
    private const string Preamble = """
        from playwright.sync_api import sync_playwright, expect, Error, TimeoutError

        def run(body):
            with sync_playwright() as p:
                browser = p.chromium.launch()
                page = browser.new_page()
                body(p, browser, page)

        """;

    private static readonly Dictionary<string, string> Scenarios = new(StringComparer.Ordinal)
    {
        ["reads_a_page"] = """
            def body(p, browser, page):
                page.set_content("<h1 id='t'>Hello</h1><p class='x'>one</p><p class='x'>two</p>")

                assert page.text_content('#t') == 'Hello'
                assert page.inner_text('#t') == 'Hello'
                assert page.locator('#t').inner_text() == 'Hello'
                assert page.locator('.x').count() == 2
                assert page.locator('.x').all_text_contents() == ['one', 'two']
                assert page.locator('.x').first.text_content() == 'one'
                assert page.locator('.x').nth(1).text_content() == 'two'
                assert '<h1 id="t">Hello</h1>' in page.content()

            run(body)
            """,

        ["drives_a_form"] = """
            def body(p, browser, page):
                page.set_content('''
                    <input id="n">
                    <input type="checkbox" id="c">
                    <select id="s"><option value="a">A</option><option value="b">B</option></select>
                    <button id="go" onclick="out.textContent = n.value + s.value + c.checked">go</button>
                    <p id="out"></p>
                ''')

                page.fill('#n', 'abc')
                page.check('#c')
                page.select_option('#s', 'b')
                page.click('#go')

                assert page.text_content('#out') == 'abcbtrue'
                assert page.input_value('#n') == 'abc'
                assert page.is_checked('#c')
                assert page.is_visible('#go')
                assert not page.is_visible('#nothing')

            run(body)
            """,

        ["uses_locators_and_roles"] = """
            def body(p, browser, page):
                page.set_content('''
                    <button>Save</button>
                    <button>Cancel</button>
                    <label for="e">Email</label><input id="e" placeholder="you@example.com">
                    <img alt="a picture" src="data:image/gif;base64,R0lGODlhAQABAAAAACw=">
                    <span data-testid="badge">7</span>
                ''')

                assert page.get_by_role('button').count() == 2
                assert page.get_by_role('button', name='Save').count() == 1
                assert page.get_by_text('Cancel').count() == 1
                assert page.get_by_label('Email').count() == 1
                assert page.get_by_placeholder('you@example.com').count() == 1
                assert page.get_by_alt_text('a picture').count() == 1
                assert page.get_by_test_id('badge').text_content() == '7'

                # Chained, filtered and combined - the shapes a real script uses.
                assert page.locator('button').filter(has_text='Save').count() == 1
                assert page.get_by_role('button', name='Save').or_(
                    page.get_by_role('button', name='Cancel')).count() == 2

            run(body)
            """,

        ["asserts_with_expect"] = """
            def body(p, browser, page):
                page.set_content("<h1 id='t'>Hello</h1><button>Save</button><button>Cancel</button>")

                expect(page.locator('#t')).to_have_text('Hello')
                expect(page.locator('#t')).to_contain_text('ell')
                expect(page.locator('#t')).to_be_visible()
                expect(page.get_by_role('button')).to_have_count(2)
                expect(page.locator('#missing')).not_to_be_visible()
                expect(page.locator('#t')).to_have_attribute('id', 't')

                # A failure is an AssertionError, so a program reads it like any other.
                try:
                    expect(page.locator('#t')).to_have_text('Goodbye', timeout=500)
                    raise Exception('the assertion should have failed')
                except AssertionError:
                    pass

            run(body)
            """,

        ["evaluates_javascript"] = """
            def body(p, browser, page):
                page.set_content("<p id='p'>text</p>")

                assert page.evaluate('1 + 1') == 2
                assert page.evaluate('() => document.getElementById("p").textContent') == 'text'
                assert page.evaluate('([a, b]) => a + b', [2, 3]) == 5
                assert page.evaluate('x => x.n * 2', {'n': 21}) == 42

                data = page.evaluate('() => ({name: "x", tags: ["a", "b"], n: 3, ok: true, missing: null})')
                assert data['name'] == 'x'
                assert data['tags'] == ['a', 'b']
                assert data['n'] == 3
                assert data['ok'] is True
                assert data['missing'] is None

                assert page.locator('#p').evaluate('el => el.tagName') == 'P'

            run(body)
            """,

        ["navigates_to_a_data_url"] = """
            def body(p, browser, page):
                page.goto("data:text/html,<title>Doc</title><p id='p'>from a data url</p>")

                assert page.text_content('#p') == 'from a data url'
                assert page.title() == 'Doc'
                assert page.url.startswith('data:text/html')

            run(body)
            """,

        ["refuses_a_host_the_policy_does_not_allow"] = """
            def body(p, browser, page):
                try:
                    page.goto('https://example.com/')
                    raise Exception('the navigation should have been refused')
                except Error as failure:
                    assert 'AllowedHosts' in str(failure)

            run(body)
            """,

        ["writes_a_screenshot_into_the_sandbox"] = """
            import os

            def body(p, browser, page):
                page.set_content("<h1>shot</h1>")

                data = page.screenshot(path='/shot.png')

                assert os.path.exists('/shot.png')
                assert data[:4] == b'\x89PNG'
                assert open('/shot.png', 'rb').read() == data

            run(body)
            """,

        ["uploads_a_file_from_the_sandbox"] = """
            def body(p, browser, page):
                f = open('/data.csv', 'w')
                f.write('a,b\n1,2\n')
                f.close()

                page.set_content("<input type='file' id='f'>")
                page.set_input_files('#f', '/data.csv')

                assert page.evaluate('() => document.getElementById("f").files[0].name') == 'data.csv'
                assert page.evaluate('() => document.getElementById("f").files[0].size') == 8

            run(body)
            """,

        ["reports_an_unknown_keyword"] = """
            def body(p, browser, page):
                page.set_content("<button id='b'>go</button>")

                try:
                    page.click('#b', nonsense=1)
                    raise Exception('the typo should have been reported')
                except TypeError as failure:
                    assert 'nonsense' in str(failure)

            run(body)
            """,

        ["reports_a_bad_literal"] = """
            def body(p, browser, page):
                try:
                    page.set_content('<p>x</p>', wait_until='eventually')
                    raise Exception('the literal should have been rejected')
                except ValueError as failure:
                    assert 'wait_until' in str(failure)

            run(body)
            """,

        ["times_out_catchably"] = """
            def body(p, browser, page):
                page.set_content("<p>nothing to click</p>")

                try:
                    page.click('#never', timeout=300)
                    raise Exception('the click should have timed out')
                except TimeoutError:
                    pass

                # TimeoutError is an Error, as it is upstream, so a broad handler catches it.
                try:
                    page.click('#never', timeout=300)
                except Error:
                    pass

            run(body)
            """,

        ["keeps_launch_options_with_the_host"] = """
            def body(p, browser, page):
                try:
                    p.chromium.launch(headless=False)
                    raise Exception('the option should have been refused')
                except Error as failure:
                    assert 'fixed by the host' in str(failure)

            run(body)
            """,

        ["offers_only_the_browsers_the_host_configured"] = """
            def body(p, browser, page):
                try:
                    p.firefox.launch()
                    raise Exception('firefox should not be available')
                except Error as failure:
                    assert 'PlaywrightOptions.Browsers' in str(failure)

            run(body)
            """,

        ["refuses_the_async_api"] = """
            try:
                import playwright.async_api
                raise Exception('the async API should not be importable')
            except ImportError as failure:
                assert 'sync_api' in str(failure)
            """,

        ["refuses_to_write_outside_the_sandbox"] = """
            def body(p, browser, page):
                try:
                    browser.new_context(record_video_dir='/videos')
                    raise Exception('recording to the host should have been refused')
                except Error as failure:
                    assert "host's filesystem" in str(failure)

                try:
                    browser.new_context(proxy={'server': 'http://127.0.0.1:1'})
                    raise Exception('a proxy should have been refused')
                except Error as failure:
                    assert 'AllowedHosts' in str(failure)

            run(body)
            """,

        ["writes_storage_state_into_the_sandbox"] = """
            import json
            import os

            def body(p, browser, page):
                state = browser.new_context().storage_state(path='/state.json')

                assert 'cookies' in state
                assert os.path.exists('/state.json')
                assert json.loads(open('/state.json').read()) == state

            run(body)
            """,

        ["reads_the_pages_console"] = """
            def body(p, browser, page):
                page.set_content("<script>console.log('hello from the page')</script>")

                messages = page.console_messages()
                assert any('hello from the page' in m.text for m in messages)
                assert any(m.type == 'log' for m in messages)

                page.clear_console_messages()
                assert page.console_messages() == []

            run(body)
            """,

        ["keeps_two_contexts_apart"] = """
            def body(p, browser, page):
                first = browser.new_context()
                second = browser.new_context()

                first.add_cookies([{'name': 'who', 'value': 'first', 'url': 'https://example.com/'}])

                assert [c['value'] for c in first.cookies()] == ['first']
                assert second.cookies() == []

                first.close()
                second.close()
                assert first.is_closed()

            run(body)
            """,

        ["enforces_the_context_cap"] = """
            def body(p, browser, page):
                opened = []

                try:
                    for _ in range(64):
                        opened.append(browser.new_context())
                    raise Exception('the cap should have been reached')
                except Error as failure:
                    assert 'MaxContexts' in str(failure)

            run(body)
            """,

        ["uses_the_keyboard_and_the_mouse"] = """
            def body(p, browser, page):
                page.set_content('''
                    <input id="n">
                    <div id="box"
                         style="position:absolute;left:0;top:200px;width:100px;height:100px"
                         onclick="out.textContent = 'clicked'"></div>
                    <p id="out"></p>
                ''')

                page.focus('#n')
                page.keyboard.type('typed')
                assert page.input_value('#n') == 'typed'

                page.mouse.click(50, 250)
                assert page.text_content('#out') == 'clicked'

            run(body)
            """,
    };
}
