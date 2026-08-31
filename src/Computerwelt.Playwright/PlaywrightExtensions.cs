using Computerwelt.Emulation.Bash;
using Computerwelt.Emulation.Python;

namespace Computerwelt.Playwright;

/// <summary>
/// Adds the browser to a sandbox, at each of the three places one is configured.
/// </summary>
/// <remarks>
/// The shape mirrors how Python itself is added: <c>builder.WithPython()</c> joins the two
/// interpreters, and <c>builder.WithPlaywright(session)</c> joins a browser to them. Nothing
/// here changes what the sandbox may reach except by adding the module — the limits, the
/// filesystem and the allowlist are all decided elsewhere and read from there.
/// </remarks>
public static class PlaywrightExtensions
{
    /// <summary>
    /// Makes <c>playwright</c> importable from a bare <see cref="PythonRunner"/>.
    /// </summary>
    /// <remarks>
    /// For a host running Python on its own. Over a joined session, prefer
    /// <see cref="WithPlaywright(BashBuilder, PlaywrightSession, PythonOptions?)"/> so that
    /// the shell's <c>python</c> command gets it too.
    /// </remarks>
    public static PythonRunner WithPlaywright(
        this PythonRunner runner,
        PlaywrightSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(session);

        runner.Libraries.AddRange(PlaywrightLibrary.For(session, cancellationToken));
        return runner;
    }

    /// <summary>
    /// Returns options whose <c>python</c> command can import <c>playwright</c>.
    /// </summary>
    /// <remarks>
    /// <see cref="PythonOptions"/> is a record and this returns a new one, so a host that
    /// keeps a template of options is not surprised by one of them growing a browser.
    /// </remarks>
    public static PythonOptions WithPlaywright(
        this PythonOptions options,
        PlaywrightSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(session);

        return options with
        {
            Libraries = [.. options.Libraries, .. PlaywrightLibrary.For(session, cancellationToken)],
        };
    }

    /// <summary>
    /// Builds a shell whose <c>python</c> command can drive the host's browser.
    /// </summary>
    /// <remarks>
    /// The one-line form: <c>new BashBuilder().WithPlaywright(session).Build()</c> gives a
    /// session where <c>python -c "from playwright.sync_api import sync_playwright"</c> works
    /// against the same virtual filesystem every other command sees — so a screenshot the
    /// script takes is a file the next <c>ls</c> lists.
    /// </remarks>
    public static BashBuilder WithPlaywright(
        this BashBuilder builder,
        PlaywrightSession session,
        PythonOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(session);

        return builder.WithPython((options ?? new PythonOptions()).WithPlaywright(session));
    }
}
