using System.Text;
using Computerwelt.Emulation.Python;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Playwright.Interop;

/// <summary>
/// One run's view of the browser backend.
/// </summary>
/// <remarks>
/// <para>
/// Built when a program first imports <c>playwright</c> and discarded when the run ends, so
/// it carries no state between tenants. What it holds is the run's environment — its virtual
/// filesystem, its limits — plus the host's session, which is shared and read-only from
/// here.
/// </para>
/// <para>
/// It is also where the two halves of the impedance mismatch are absorbed: Playwright's .NET
/// API is asynchronous and Python's <c>sync_api</c> is not, and every file path a script
/// hands to Playwright names a file in the sandbox rather than on the host's disk.
/// </para>
/// </remarks>
internal sealed class Bridge
{
    private readonly CancellationToken _cancellationToken;

    public Bridge(PythonHostContext context, PlaywrightSession session, CancellationToken cancellationToken)
    {
        Context = context;
        Session = session;
        _cancellationToken = cancellationToken;
    }

    /// <summary>The run's environment: its filesystem, working directory and limits.</summary>
    public PythonHostContext Context { get; }

    /// <summary>The host's browser backend, shared with every other run.</summary>
    public PlaywrightSession Session { get; }

    /// <summary>The host's configuration.</summary>
    public PlaywrightOptions Options => Session.Options;

    /// <summary>What the browser may reach.</summary>
    public NavigationPolicy Policy => Session.Policy;

    /// <summary>Every browser context this run opened, so all of them can be closed at once.</summary>
    public List<Api.PyBrowserContext> Contexts { get; } = [];

    /// <summary>
    /// Closes every context this run opened and hands its slot back to the session.
    /// </summary>
    /// <remarks>
    /// Reached three ways, and the third is the one that matters: <c>p.stop()</c>, the exit
    /// of a <c>with sync_playwright()</c> block, and the end of the run itself. The first two
    /// are the program tidying up after itself; the last is what happens when it did not,
    /// which is the case a shared browser needs protecting from.
    /// </remarks>
    public void ReleaseAll()
    {
        // Copied first: closing a context takes it out of this list.
        foreach (var context in Contexts.ToArray())
        {
            context.CloseQuietly();
        }

        Contexts.Clear();
    }

    /// <summary>
    /// Runs an asynchronous driver call to completion and reports its result to the script.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The VM is a synchronous interpreter — <c>page.click()</c> is one bytecode call that
    /// must have finished by the time the next instruction runs — so the wait happens here,
    /// once, at the boundary. The work is started on the thread pool rather than awaited in
    /// place: blocking on a task whose continuations want the calling context is how a
    /// deadlock is written, and the calling context here belongs to whatever host thread is
    /// running the script.
    /// </para>
    /// <para>
    /// Anything thrown is translated on the way out, so no .NET exception type is ever
    /// visible to a program.
    /// </para>
    /// </remarks>
    public T Block<T>(Func<Task<T>> work)
    {
        try
        {
            return Task.Run(work, _cancellationToken).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            throw Errors.Translate(exception);
        }
    }

    /// <summary>Runs an asynchronous driver call that produces nothing.</summary>
    public void Block(Func<Task> work)
    {
        try
        {
            Task.Run(work, _cancellationToken).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            throw Errors.Translate(exception);
        }
    }

    /// <summary>
    /// Clamps a timeout a script asked for to the host's ceiling.
    /// </summary>
    /// <remarks>
    /// A script may wait for less than the host allows and never for more: the ceiling is
    /// what keeps a run inside its budget, and a <c>timeout=</c> argument is a request, not
    /// a grant.
    /// </remarks>
    public float? Timeout(double? milliseconds) =>
        milliseconds is null ? null : (float)Math.Min(milliseconds.Value, Options.DefaultTimeout);

    /// <summary>Refuses a URL the host's policy does not allow.</summary>
    public void RequireAllowed(string url)
    {
        if (!Policy.IsAllowed(url))
        {
            throw Errors.Fail(Policy.Refusal(url));
        }
    }

    /// <summary>
    /// Reads a file from the <i>sandbox's</i> filesystem.
    /// </summary>
    /// <remarks>
    /// Every path in this API is a virtual one. A script that uploads a file with
    /// <c>set_input_files('/tmp/report.csv')</c> uploads the bytes the sandbox holds under
    /// that name, and the browser is handed those bytes rather than a path it could open —
    /// which is what keeps a path traversal from becoming a read of the host's disk.
    /// </remarks>
    public byte[] Read(string path)
    {
        var bytes = Context.RequireFileSystem().Read(path);

        if (bytes.Length > Options.MaxTransferBytes)
        {
            throw Errors.Fail(
                $"{path} is {bytes.Length} bytes, over PlaywrightOptions.MaxTransferBytes "
                + $"({Options.MaxTransferBytes}).");
        }

        return bytes;
    }

    /// <summary>Reads a file from the sandbox as UTF-8 text.</summary>
    public string ReadText(string path) => Encoding.UTF8.GetString(Read(path));

    /// <summary>
    /// Writes bytes the browser produced — a screenshot, a PDF, a trace — into the sandbox.
    /// </summary>
    /// <remarks>
    /// Playwright would happily write these to a host path itself; it is never asked to.
    /// The bytes come back to this process and go out through the run's filesystem, so a
    /// screenshot lands where the surrounding shell can <c>ls</c> it and nowhere else.
    /// </remarks>
    public void Write(string path, byte[] bytes)
    {
        if (bytes.Length > Options.MaxTransferBytes)
        {
            throw Errors.Fail(
                $"{bytes.Length} bytes is over PlaywrightOptions.MaxTransferBytes "
                + $"({Options.MaxTransferBytes}).");
        }

        Context.RequireFileSystem().Write(path, bytes);
    }
}
