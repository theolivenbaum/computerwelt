using System.Globalization;

namespace Computerwelt.Playwright;

/// <summary>What a browser installation attempt did.</summary>
/// <param name="ExitCode">Zero when the driver reported success.</param>
/// <param name="StandardOutput">Everything the driver printed.</param>
/// <param name="StandardError">Everything the driver reported as an error.</param>
public readonly record struct PlaywrightInstallResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>True when the driver reported success.</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>The driver's own words, for a host that wants to log or surface them.</summary>
    public override string ToString() =>
        Succeeded
            ? StandardOutput
            : string.Format(
                CultureInfo.InvariantCulture,
                "playwright install failed with exit code {0}.{1}{2}{3}",
                ExitCode,
                Environment.NewLine,
                StandardOutput,
                StandardError);
}

/// <summary>
/// Puts the browsers Playwright drives onto the host machine.
/// </summary>
/// <remarks>
/// <para>
/// Playwright's .NET package ships a driver and a command line but no browsers; they are
/// downloaded once, per machine, by <c>playwright install</c>. That command is normally run
/// from a shell — which is exactly what a host embedding this library does not want to
/// arrange — so it is wrapped here as an ordinary method: the driver's entry point is called
/// in-process and its output is returned rather than printed.
/// </para>
/// <para>
/// This is host setup and nothing in the sandbox can reach it. Call it once at start-up,
/// before <see cref="PlaywrightSession.CreateAsync"/>, or leave it out entirely if the
/// machine is provisioned some other way — a container image with the browsers baked in, or
/// a <c>PLAYWRIGHT_BROWSERS_PATH</c> pointing at a shared directory.
/// </para>
/// </remarks>
public static class PlaywrightBrowsers
{
    /// <summary>
    /// Installs the browsers, and optionally their operating-system dependencies.
    /// </summary>
    /// <param name="browsers">
    /// Which browsers to fetch. Empty installs every browser Playwright knows, which is what
    /// the bare <c>playwright install</c> does and is usually more than a host needs.
    /// </param>
    /// <param name="withDependencies">
    /// Also install the system packages the browsers need. Requires root on Linux and is
    /// ignored elsewhere, so it is off by default.
    /// </param>
    /// <param name="cancellationToken">Abandons the wait; the driver itself is not interruptible.</param>
    /// <remarks>
    /// The work is a synchronous call into Playwright's own command line, so it is moved off
    /// the caller's thread rather than pretending to be asynchronous in place — a download
    /// of a few hundred megabytes is not something to run on a request thread.
    /// </remarks>
    public static Task<PlaywrightInstallResult> InstallAsync(
        IEnumerable<PlaywrightBrowser>? browsers = null,
        bool withDependencies = false,
        CancellationToken cancellationToken = default)
    {
        var arguments = new List<string> { "install" };

        if (withDependencies)
        {
            arguments.Add("--with-deps");
        }

        if (browsers is not null)
        {
            foreach (var browser in browsers)
            {
                arguments.Add(PlaywrightOptions.NameOf(browser));
            }
        }

        return Task.Run(() => Install([.. arguments]), cancellationToken);
    }

    /// <summary>
    /// Installs the browsers a set of options names, so that a session built from the same
    /// options can launch every one of them.
    /// </summary>
    public static Task<PlaywrightInstallResult> InstallAsync(
        PlaywrightOptions options,
        bool withDependencies = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        return InstallAsync(options.Browsers, withDependencies, cancellationToken);
    }

    /// <summary>
    /// Runs Playwright's command line in-process with <paramref name="arguments"/>.
    /// </summary>
    /// <remarks>
    /// The escape hatch for the parts of the tool this class does not wrap —
    /// <c>install-deps</c>, <c>uninstall</c>, <c>--dry-run</c>. It is the same entry point
    /// the <c>playwright</c> executable has.
    /// </remarks>
    public static PlaywrightInstallResult Install(params string[] arguments)
    {
        var result = new Microsoft.Playwright.Program().RunWithResult(arguments);

        return new PlaywrightInstallResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }
}
