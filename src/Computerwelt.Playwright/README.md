# Computerwelt.Playwright

Playwright's **Python `sync_api`** inside the Computerwelt sandbox, driven by Microsoft's
.NET Playwright bindings.

A script running under the sandbox's `python` command writes what it would write anywhere
else:

```python
from playwright.sync_api import sync_playwright, expect

with sync_playwright() as p:
    browser = p.chromium.launch()
    page = browser.new_page()
    page.goto("https://example.com/")

    expect(page.get_by_role("heading")).to_have_text("Example Domain")
    page.screenshot(path="/report/home.png")
```

…and `/report/home.png` is a file in the **virtual** filesystem, which the shell around the
script can `ls`, `cat` and pipe. Nothing was written to the host's disk.

## Adding it

```csharp
// Once, at start-up: put the browsers on the machine. Skip it if your image has them,
// or if PLAYWRIGHT_BROWSERS_PATH points at a shared directory.
await PlaywrightBrowsers.InstallAsync([PlaywrightBrowser.Chromium]);

// Host-owned, shared, and disposed by you. This is the only thing that starts a process.
await using var session = await PlaywrightSession.CreateAsync(new PlaywrightOptions
{
    Browsers = [PlaywrightBrowser.Chromium],
    AllowedHosts = ["*.example.com", "localhost:8080"],
    DefaultTimeout = 15_000,
});

var bash = Bash.CreateBuilder()
    .WithWorkingDirectory("/work")
    .WithPlaywright(session)      // = WithPython(options.WithPlaywright(session))
    .Build();
```

The same session works at the other two configuration points:

```csharp
new PythonRunner().WithPlaywright(session);            // Python on its own
options.WithPlaywright(session);                       // an existing PythonOptions
```

A sandbox that does not register it has no importable `playwright` and no route to a
browser, exactly as a sandbox with no filesystem has no `open`.

## The one deliberate exception, stated plainly

Computerwelt's first rule is **no process spawning**, and a browser is a process. This
package does not weaken that rule so much as move it outside the sandbox, and the
distinction is worth being precise about:

- The browser is started by **host code**, from `PlaywrightSession`, before any script runs.
- `p.chromium.launch()` does **not** launch anything. It hands back a handle to what the
  host already started, and a launch option a script passes — `headless=False`, `args=…`,
  `executable_path=…` — is refused by name rather than ignored, because those are decisions
  the host already made.
- A browser type absent from `PlaywrightOptions.Browsers` is not launchable at all.
- Nothing a script does outlives `PlaywrightSession.DisposeAsync()`.

Everything else the sandbox promises still holds, and holds *because* of how this package is
written rather than by luck:

| Promise | How it survives a browser |
|---|---|
| No ambient network | `AllowedHosts` is empty by default, so a sandbox nobody configured reaches nothing. The check runs on `page.goto` for the error message **and** as a request filter on every context — so a redirect, an iframe, an image or a `fetch()` the page performs cannot reach somewhere the script could not have navigated to itself. `file:` is not an allowed scheme, with or without a wildcard. |
| No ambient filesystem | Every path in this API is a virtual one. A screenshot, a PDF and a storage state come back to this process as bytes and go out through the run's `IFileSystem`; an upload or an init script is read from it. The driver is never given a path it could open, so a traversal has nothing to reach. `record_video_dir`, `record_har_path` and `downloads_path` are refused. |
| Deterministic limits | `MaxContexts`, `MaxPagesPerContext` and `MaxTransferBytes` are enforced and raise when reached. `DefaultTimeout` is a ceiling, not a default: a script may wait for less and never for more, including through `set_default_timeout` and `wait_for_timeout`. |
| Multi-tenant isolation | One browser, one **context** per script — which is the unit Playwright isolates cookies, storage and cache on. Two tenants over one session share nothing they can observe. `browser.close()` closes what the calling script opened and leaves the browser running for everyone else, and the run's contexts go back at the end of the run whether the script asked or not — a clean finish, an uncaught exception and a limit reached all release them. |
| No host exception reaches a script | Every driver call is translated: a Playwright failure becomes `playwright.sync_api.Error`, a wait that ran out becomes `TimeoutError`, and anything else becomes `Error` with the type name kept in the message. Neither class is a builtin, so an unregistered sandbox cannot even name them. |

## What is implemented

The mapping is from the **Python** `sync_api` to the .NET driver, method by method:
`sync_playwright`, `Playwright`, `BrowserType`, `Browser`, `BrowserContext`, `Page`,
`Locator`, `FrameLocator`, `ElementHandle`, `Request`, `Response`, `ConsoleMessage`,
`Keyboard`, `Mouse`, and `expect()` with its locator and page assertions in both the
`to_*` and `not_to_*` forms.

Keyword arguments are read by name and **anything left over is a `TypeError`**, as CPython
gives you. That is deliberate: silently dropping `wait_until="networkidle"` would leave a
program believing it waited.

Arguments upstream types as `str | Pattern` take either, and the distinction is kept because
the two mean different things — a string matches loosely, case-insensitively and by
substring, a pattern matches as written:

```python
import re

page.get_by_role("button", name=re.compile(r"^Save"))
page.locator("p").filter(has_text=re.compile(r"\d+"))
expect(page).to_have_url(re.compile(r"^https://example\.com/orders/\d+$"))
```

A sequence mixing strings and patterns is refused by name: the driver has an overload for a
sequence of each and none for a mixture, and flattening one would pick a meaning silently.

### What is not, and why

| Absent | Why |
|---|---|
| `playwright.async_api` | The interpreter runs a program to completion synchronously; there is no event loop to await on. The import fails with a sentence saying so. |
| `page.on(...)`, `expect_event`, `wait_for_event` | Nothing can call into the interpreter while a script is blocked in it. Use the pull-based accessors the driver records for you: `page.console_messages()`, `page.requests()`, `page.page_errors()`. |
| `page.route(...)`, `context.route(...)` | Request filtering is the host's, and it is already installed. A script-installed handler would sit in front of it. |
| `expose_function`, `expose_binding`, `add_locator_handler` | The same reason as events: they call back from the browser's thread. |
| `launch_persistent_context`, `connect`, `connect_over_cdp` | A profile on the host's disk, and a browser the host did not launch. |
| `expect.set_options(...)` | The driver holds that default in a process-wide static, so one script setting it would change another tenant's assertions. Per-call `timeout=` does the same job for one caller. |
| A callable where upstream accepts one — `wait_for_url(lambda url: …)` | It would have to run on the driver's thread while the interpreter is blocked, which is the same obstacle the event API hits. A string or a pattern covers it. |
| Frames, workers, tracing, CDP | Not modelled. `page.frame_locator(selector)` covers reaching into an `iframe`. |

Each of these reports what was asked for and why it is unavailable, rather than being
missing and looking like a typo.

## Testing it

`tests/Computerwelt.Playwright.Tests/` has three parts. `NavigationPolicyTests` covers the
security boundary with no browser at all. `ScenarioTests` is a corpus of Python programs run
through the shell's `python` command against a real Chromium — written as fixtures, because
the thing under test is a Python API and the test should be the Python a caller would write.
`LifetimeTests` covers what happens to a context when the script that opened it stops caring:
no `close()`, an uncaught exception, a limit reached.

No scenario reaches the network: the pages are `set_content` and `data:` URLs, and the one
that names a real host is asserting that it is refused. The fixture installs a browser with
`PlaywrightBrowsers.InstallAsync` if the machine has none, which is also the only place this
repository dogfoods that helper.

```bash
dotnet test tests/Computerwelt.Playwright.Tests
COMPUTERWELT_SKIP_PLAYWRIGHT=1 dotnet test    # no browser available
```
