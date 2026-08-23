using System.Text;
using Computerwelt.Emulation.Python.Modules;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Emulation.Python;

/// <summary>
/// The sandbox environment, as host C# sees it.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes "implement it in C#" a real option rather than a way to compute
/// values in a vacuum: a host function or a host library is handed the running program's
/// filesystem, its working directory, its environment and its clock, so it can do the work
/// a builtin would do — read a file the shell wrote a moment ago, walk a directory, stamp a
/// record — while staying inside the sandbox.
/// </para>
/// <para>
/// It is the environment of the <i>current</i> run, handed over per run and never captured
/// globally. Over a joined session the filesystem is the shell's, the working directory
/// follows a <c>cd</c> the script performed, and the environment is what the script
/// exported. There is nothing ambient here: a context with no filesystem means the sandbox
/// genuinely has none, and host code must say so rather than reach for the host's disk.
/// </para>
/// </remarks>
public sealed class PythonHostContext
{
    /// <summary>Creates a context for one library or one host function.</summary>
    public PythonHostContext(
        string name,
        VirtualMachine machine,
        IPyFileSystem? fileSystem,
        TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(timeProvider);

        Name = name;
        Machine = machine;
        FileSystem = fileSystem;
        TimeProvider = timeProvider;
    }

    /// <summary>The name being built or called — a module's, or a function's.</summary>
    public string Name { get; }

    /// <summary>
    /// The running machine, for host code that calls back into the interpreter — invoking a
    /// callable a program passed it, or writing to the program's output.
    /// </summary>
    public VirtualMachine Machine { get; }

    /// <summary>
    /// The run's filesystem, or <see langword="null"/> when the sandbox has none.
    /// </summary>
    /// <remarks>
    /// Over a joined session this is the shell's virtual filesystem, so host C# reads and
    /// writes exactly the files the surrounding script does. Use
    /// <see cref="RequireFileSystem"/> where the absence of one is an error worth reporting.
    /// </remarks>
    public IPyFileSystem? FileSystem { get; }

    /// <summary>The clock the run reads, which a host may have pinned.</summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>The run's resource caps, for host work that is unbounded in its input.</summary>
    public ExecutionLimits Limits => Machine.Limits;

    /// <summary>
    /// The directory relative paths resolve against, or <c>/</c> when there is no filesystem.
    /// </summary>
    public string WorkingDirectory => FileSystem?.WorkingDirectory ?? "/";

    /// <summary>The environment the program can see, empty when there is no filesystem.</summary>
    public IReadOnlyDictionary<string, string> Environment =>
        FileSystem?.Environment ?? new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>An empty module named after the library, ready to have members added.</summary>
    public PyModuleObject Module() => new(Name);

    /// <summary>
    /// The run's filesystem, raising the Python error a program can catch when there is none.
    /// </summary>
    /// <exception cref="PyRaise">
    /// Raises <c>OSError</c>, because a host exception must not cross into a sandboxed
    /// program.
    /// </exception>
    public IPyFileSystem RequireFileSystem() =>
        FileSystem ?? throw new PyRaise(new PyException(
            PyExceptionType.OSError, $"{Name}: this sandbox has no filesystem"));

    /// <summary>Reads a text file from the sandbox, decoded as UTF-8.</summary>
    /// <exception cref="PyRaise">Raises the same error <c>open()</c> would.</exception>
    public string ReadText(string path) => Encoding.UTF8.GetString(RequireFileSystem().Read(path));

    /// <summary>Writes a text file to the sandbox as UTF-8, replacing it if it exists.</summary>
    /// <exception cref="PyRaise">Raises the same error <c>open()</c> would.</exception>
    public void WriteText(string path, string text) =>
        RequireFileSystem().Write(path, Encoding.UTF8.GetBytes(text));
}
