using Computerwelt.Emulation.Python.Runtime;
using Computerwelt.Playwright.Interop;

namespace Computerwelt.Playwright.Api;

/// <summary>
/// Base of every object <c>playwright.sync_api</c> hands a script.
/// </summary>
/// <remarks>
/// <para>
/// Each is a thin view onto one driver object. They hold no copies of what the browser
/// knows — <c>page.url</c> asks the driver every time — because a script's picture of the
/// page has to be the page's, not a snapshot from whenever the wrapper was built.
/// </para>
/// <para>
/// Attributes are answered by name rather than reflected, which is what keeps the reachable
/// surface a list somebody wrote: a method absent from the switch is absent from the API,
/// and a script gets <c>AttributeError</c> instead of something half-implemented.
/// </para>
/// </remarks>
internal abstract class PlaywrightObject : PyObject
{
    protected PlaywrightObject(Bridge bridge) => Bridge = bridge;

    /// <summary>The run's environment and the host's browser session.</summary>
    protected Bridge Bridge { get; }

    /// <inheritdoc />
    public override string Repr() => $"<{TypeName}>";

    /// <summary>Builds a bound method, named as a traceback would spell it.</summary>
    protected PyObject Method(string name, Func<Arguments, PyObject> body) =>
        new PyBuiltinFunction(
            $"{TypeName}.{name}",
            (positional, keywords) => body(new Arguments($"{TypeName}.{name}", positional, keywords)));

    /// <summary>Builds a bound method that returns nothing, as most actions do.</summary>
    protected PyObject Action(string name, Action<Arguments> body) =>
        Method(name, arguments =>
        {
            body(arguments);
            return PyNone.Instance;
        });

    /// <summary>
    /// Reports that a member exists upstream and is not implemented here.
    /// </summary>
    /// <remarks>
    /// Used where the absence would otherwise be mistaken for a typo. A script that reaches
    /// for one of these is told what it asked for and why it is not available, which is a
    /// better answer than an <c>AttributeError</c> naming a method the documentation says
    /// exists.
    /// </remarks>
    protected PyObject Unsupported(string name, string reason) =>
        new PyBuiltinFunction($"{TypeName}.{name}", (_, _) =>
            throw Errors.Fail($"{TypeName}.{name}() is not available in this sandbox: {reason}"));
}
