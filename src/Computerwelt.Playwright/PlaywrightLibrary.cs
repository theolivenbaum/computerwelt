using System.Runtime.CompilerServices;
using Computerwelt.Emulation.Python;
using Computerwelt.Emulation.Python.Modules;
using Computerwelt.Emulation.Python.Runtime;
using Computerwelt.Playwright.Api;
using Computerwelt.Playwright.Interop;

namespace Computerwelt.Playwright;

/// <summary>
/// The <c>playwright</c> package, as the sandbox can import it.
/// </summary>
/// <remarks>
/// <para>
/// Registering this is the whole of the opt-in: a sandbox that does not register it has no
/// importable <c>playwright</c> and no route to a browser, exactly as a sandbox with no
/// filesystem has no <c>open</c>.
/// </para>
/// <para>
/// Three names are registered, because <c>from playwright.sync_api import sync_playwright</c>
/// imports the dotted name and <c>import playwright.sync_api</c> binds the package. The third
/// is <c>playwright.async_api</c>, which exists only to fail with a sentence explaining why —
/// this interpreter runs a program to completion synchronously, so the asynchronous API has
/// nothing to schedule against.
/// </para>
/// </remarks>
public sealed class PlaywrightLibrary
{
    // One bridge per run, keyed by the machine running it, so that `playwright` and
    // `playwright.sync_api` are two views of one thing: `p.stop()` reached through either
    // has to close the contexts opened through the other.
    private readonly ConditionalWeakTable<VirtualMachine, Bridge> _bridges = [];

    // And one module object per run, so that the name reached through the package and the
    // name imported directly are the same object — as they are in CPython.
    private readonly ConditionalWeakTable<VirtualMachine, PyObject> _modules = [];
    private readonly PlaywrightSession _session;
    private readonly CancellationToken _cancellationToken;

    /// <summary>Builds the library over a host-owned browser session.</summary>
    /// <param name="session">The browser backend, which the host owns and disposes.</param>
    /// <param name="cancellationToken">
    /// Stops further driver calls once it is cancelled. It does not interrupt one already in
    /// flight — the driver has no way to abandon a call it has made — so a host that needs a
    /// hard bound sets <see cref="PlaywrightOptions.DefaultTimeout"/> as well.
    /// </param>
    public PlaywrightLibrary(PlaywrightSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        _session = session;
        _cancellationToken = cancellationToken;
    }

    /// <summary>The modules to register, for <c>PythonRunner.Libraries</c> or <c>PythonOptions.Libraries</c>.</summary>
    public IReadOnlyList<PythonLibrary> Modules =>
    [
        PythonLibrary.FromFactory("playwright.sync_api", SyncApi),
        PythonLibrary.FromFactory("playwright", Package),
        PythonLibrary.FromFactory("playwright.async_api", static _ => throw new PyRaise(new PyException(
            PyExceptionType.ImportError,
            "playwright.async_api is not available: this interpreter runs a program to "
            + "completion synchronously, so there is no event loop for it to await on. "
            + "Use playwright.sync_api, which does the waiting for you."))),
    ];

    /// <summary>The modules to register, for a host that wants one expression.</summary>
    public static IReadOnlyList<PythonLibrary> For(
        PlaywrightSession session,
        CancellationToken cancellationToken = default) =>
        new PlaywrightLibrary(session, cancellationToken).Modules;

    private Bridge BridgeFor(PythonHostContext context) =>
        _bridges.GetValue(context.Machine, _ => new Bridge(context, _session, _cancellationToken));

    private PyObject SyncApi(PythonHostContext context) =>
        _modules.GetValue(context.Machine, _ => BuildSyncApi(context));

    private PyObject BuildSyncApi(PythonHostContext context)
    {
        var bridge = BridgeFor(context);
        var module = new PyModuleObject("playwright.sync_api");

        module.Add("sync_playwright", new PyBuiltinFunction("sync_playwright", (positional, keywords) =>
        {
            var arguments = new Arguments("sync_playwright", positional, keywords);
            arguments.Done(0);

            return new PyPlaywrightContextManager(bridge);
        }));

        module.Add("expect", PyExpect.Callable(bridge));

        // The exception classes, which are the only way a program can catch what the driver
        // reports: they are not builtins and not in any registry, so an unregistered sandbox
        // cannot even name them.
        module.Add("Error", Errors.Error);
        module.Add("TimeoutError", Errors.TimeoutError);

        return module;
    }

    private PyObject Package(PythonHostContext context)
    {
        var module = new PyModuleObject("playwright");

        // `import playwright.sync_api` binds `playwright`, and the program then reaches the
        // submodule through it — so the package has to carry the attribute.
        module.Add("sync_api", SyncApi(context));

        return module;
    }
}
