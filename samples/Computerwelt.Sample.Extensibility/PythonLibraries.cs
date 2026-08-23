using System.Text;
using Computerwelt.Emulation.Python;
using Computerwelt.Emulation.Python.Runtime;

namespace Computerwelt.Sample.Extensibility;

/// <summary>
/// The two ways a host adds a Python library, side by side.
/// </summary>
/// <remarks>
/// Both are registered the same way and both are built per run, so neither can carry one
/// script's data into the next. What differs is only what they are written in: C# when the
/// library needs the host's own capabilities, Python when it does not.
/// </remarks>
public static class PythonLibraries
{
    /// <summary>
    /// <c>tickets</c> — written in C#, because it reads the sandbox's filesystem.
    /// </summary>
    /// <remarks>
    /// It is handed the run's <see cref="IPyFileSystem"/> and reaches nothing else: over a
    /// joined session that filesystem is the shell's, so this reads exactly the tickets the
    /// <c>ticket</c> command wrote, and there is no route to the host's disk in either half.
    /// </remarks>
    public static PythonLibrary Tickets() => PythonLibrary.FromFactory("tickets", context =>
    {
        var module = context.Module();

        module.Add("all", _ =>
        {
            // `context.FileSystem` is the shell's, so this reads the very file the `ticket`
            // command wrote — and nothing outside the sandbox is reachable from here.
            if (context.FileSystem is not { } fileSystem || !fileSystem.IsFile(TicketsExtension.Store))
            {
                return new PyList();
            }

            var text = Encoding.UTF8.GetString(fileSystem.Read(TicketsExtension.Store));
            var rows = new List<PyObject>();

            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split('\t');

                if (fields.Length != 3)
                {
                    continue;
                }

                var ticket = new PyDict();
                ticket.Set(new PyStr("id"), new PyInt(int.Parse(fields[0], System.Globalization.CultureInfo.InvariantCulture)));
                ticket.Set(new PyStr("status"), new PyStr(fields[1]));
                ticket.Set(new PyStr("title"), new PyStr(fields[2]));
                rows.Add(ticket);
            }

            return new PyList(rows);
        });

        return module;
    });

    /// <summary>
    /// Two functions the host implements in C#, callable from any Python program without
    /// an import — the shortest route to custom functionality over the live environment.
    /// </summary>
    /// <remarks>
    /// Each call is handed the run's <see cref="PythonHostContext"/>: the shell's filesystem,
    /// the working directory as it stands after any <c>cd</c> the script performed, and the
    /// environment the script exported. That is the whole of what host code can see, and it
    /// is why implementing something in C# does not weaken the sandbox.
    /// </remarks>
    public static IReadOnlyDictionary<string, Func<PythonHostContext, PyObject[], PyObject>> HostFunctions() =>
        new Dictionary<string, Func<PythonHostContext, PyObject[], PyObject>>(StringComparer.Ordinal)
        {
            // Where am I, and as whom? Read from the environment, not captured at build time.
            ["whereami"] = (context, _) =>
                new PyStr($"{context.Environment.GetValueOrDefault("USER", "?")}:{context.WorkingDirectory}"),

            // Real work over the sandbox's storage, in C#: total up a directory's files.
            ["disk_usage"] = (context, arguments) =>
            {
                var fileSystem = context.RequireFileSystem();
                var root = arguments.Length > 0 ? arguments[0].Display() : context.WorkingDirectory;
                var total = 0L;

                foreach (var entry in fileSystem.List(root))
                {
                    var path = root.TrimEnd('/') + "/" + entry;

                    if (fileSystem.IsFile(path))
                    {
                        total += fileSystem.Size(path);
                    }
                }

                return new PyInt(total);
            },
        };

    /// <summary>
    /// <c>formatting</c> — written in Python, because nothing about it needs C#.
    /// </summary>
    /// <remarks>
    /// The source is compiled and run inside the sandbox on first import, under the same
    /// limits as the program that imported it. It is ordinary sandboxed code: shipping it
    /// adds vocabulary and no authority at all.
    /// </remarks>
    public static PythonLibrary Formatting() => PythonLibrary.FromSource("formatting", """
        def table(headers, rows):
            '''Renders rows as a column-aligned table.'''
            columns = [[str(header) for header in headers]]
            columns.extend([str(cell) for cell in row] for row in rows)
            widths = [max(len(column[i]) for column in columns) for i in range(len(headers))]

            lines = []
            for column in columns:
                lines.append('  '.join(cell.ljust(widths[i]) for i, cell in enumerate(column)).rstrip())

            lines.insert(1, '  '.join('-' * width for width in widths))
            return '\n'.join(lines)


        def bullets(items):
            return '\n'.join(f'- {item}' for item in items)
        """);
}
