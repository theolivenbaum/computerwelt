using Computerwelt.Emulation.Python.Runtime;
using Xunit;

namespace Computerwelt.Emulation.Python.Tests;

/// <summary>
/// The surface a host uses to add a Python library to the sandbox.
/// </summary>
/// <remarks>
/// A library extends the vocabulary, and these tests pin the two properties that keep it
/// from extending anything else: an unregistered name is still unimportable, and one run's
/// module state never becomes another's.
/// </remarks>
public sealed class ExtensibilityTests
{
    /// <summary>A host library written in C#: a couple of functions over a module.</summary>
    private static PythonLibrary Metrics() => PythonLibrary.FromFunctions("metrics", new Dictionary<string, Func<PyObject[], PyObject>>
    {
        ["mean"] = arguments =>
        {
            var values = arguments[0].Iterate() ?? throw new PyRaise(PyErrors.TypeError("mean() needs an iterable"));
            var numbers = values.Select(value => value is PyInt integer
                ? (double)integer.Value
                : value is PyFloat real
                    ? real.Value
                    : throw new PyRaise(PyErrors.TypeError($"mean() got a '{value.TypeName}'"))).ToList();

            return numbers.Count == 0
                ? throw new PyRaise(PyErrors.ValueError("mean() of an empty sequence"))
                : new PyFloat(numbers.Sum() / numbers.Count);
        },
    });

    /// <summary>A host library written in Python.</summary>
    private static PythonLibrary Greeting() => PythonLibrary.FromSource("greeting", """
        SALUTATION = 'Hello'

        _greeted = []

        def greet(name):
            _greeted.append(name)
            return f'{SALUTATION}, {name}!'

        def greeted():
            return list(_greeted)
        """);

    private static PythonRunner Runner(params PythonLibrary[] libraries)
    {
        var runner = new PythonRunner();
        runner.Libraries.AddRange(libraries);
        return runner;
    }

    [Fact]
    public void Imports_a_host_library()
    {
        var result = Runner(Metrics()).Run("""
            import metrics
            print(metrics.mean([1, 2, 6]))
            """);

        Assert.Null(result.Traceback);
        Assert.Equal("3.0\n", result.Stdout);
    }

    [Fact]
    public void Imports_names_from_a_host_library()
    {
        var result = Runner(Metrics()).Run("""
            from metrics import mean
            print(mean([2, 4]))
            """);

        Assert.Null(result.Traceback);
        Assert.Equal("3.0\n", result.Stdout);
    }

    [Fact]
    public void Imports_a_library_written_in_python()
    {
        var result = Runner(Greeting()).Run("""
            import greeting
            print(greeting.greet('Ada'))
            print(greeting.SALUTATION)
            """);

        Assert.Null(result.Traceback);
        Assert.Equal("Hello, Ada!\nHello\n", result.Stdout);
    }

    [Fact]
    public void A_python_library_is_a_module_not_a_script()
    {
        // Its `__name__` is its own, so the usual main guard does not fire on import.
        var result = Runner(PythonLibrary.FromSource("tool", """
            NAME = __name__

            if __name__ == '__main__':
                raise RuntimeError('ran as a script')
            """)).Run("""
            import tool
            print(tool.NAME)
            """);

        Assert.Null(result.Traceback);
        Assert.Equal("tool\n", result.Stdout);
    }

    [Fact]
    public void A_library_keeps_its_own_globals()
    {
        // The module's names are its own: importing it does not pour them into the program,
        // and the program's names do not reach into it.
        var result = Runner(Greeting()).Run("""
            import greeting
            SALUTATION = 'Goodbye'
            print(greeting.greet('Alan'))
            try:
                print(_greeted)
            except NameError as error:
                print('not mine')
            """);

        Assert.Null(result.Traceback);
        Assert.Equal("Hello, Alan!\nnot mine\n", result.Stdout);
    }

    [Fact]
    public void One_run_sees_one_module_however_often_it_imports_it()
    {
        var result = Runner(Greeting()).Run("""
            import greeting
            import greeting as again

            greeting.greet('Ada')
            again.greet('Alan')
            print(greeting.greeted())
            """);

        Assert.Null(result.Traceback);
        Assert.Equal("['Ada', 'Alan']\n", result.Stdout);
    }

    [Fact]
    public void A_library_starts_fresh_for_every_run()
    {
        // The property multi-tenant isolation rests on: module-level state a program wrote
        // is gone by the next run, so it cannot become another caller's data.
        var runner = Runner(Greeting());

        runner.Run("import greeting\ngreeting.greet('Ada')\n");
        var second = runner.Run("import greeting\nprint(greeting.greeted())\n");

        Assert.Null(second.Traceback);
        Assert.Equal("[]\n", second.Stdout);
    }

    [Fact]
    public void A_library_is_built_only_when_a_program_imports_it()
    {
        var built = 0;

        var runner = Runner(PythonLibrary.FromFactory("expensive", context =>
        {
            built++;
            return context.Module().Add("answer", new PyInt(42));
        }));

        runner.Run("print(1 + 1)");
        Assert.Equal(0, built);

        runner.Run("import expensive\nprint(expensive.answer)");
        Assert.Equal(1, built);
    }

    [Fact]
    public void An_unregistered_name_is_still_not_importable()
    {
        var result = Runner(Metrics()).Run("import statistics");

        Assert.False(result.Succeeded);
        Assert.Equal("ModuleNotFoundError", result.ExceptionType);
    }

    [Fact]
    public void A_library_may_replace_a_standard_module()
    {
        var result = Runner(PythonLibrary.FromSource("math", "def sqrt(x):\n    return 'nope'\n")).Run("""
            import math
            print(math.sqrt(4))
            """);

        Assert.Null(result.Traceback);
        Assert.Equal("nope\n", result.Stdout);
    }

    [Fact]
    public void A_library_may_import_another_one()
    {
        var runner = Runner(
            PythonLibrary.FromSource("base", "def double(x):\n    return x * 2\n"),
            PythonLibrary.FromSource("derived", "import base\n\ndef quadruple(x):\n    return base.double(base.double(x))\n"));

        var result = runner.Run("import derived\nprint(derived.quadruple(3))");

        Assert.Null(result.Traceback);
        Assert.Equal("12\n", result.Stdout);
    }

    [Fact]
    public void A_library_may_import_the_standard_library()
    {
        var result = Runner(PythonLibrary.FromSource("slugs", """
            import re

            def slug(text):
                return re.sub(r'[^a-z0-9]+', '-', text.lower()).strip('-')
            """)).Run("""
            from slugs import slug
            print(slug('Hello, World!'))
            """);

        Assert.Null(result.Traceback);
        Assert.Equal("hello-world\n", result.Stdout);
    }

    [Fact]
    public void A_library_that_imports_itself_is_reported_rather_than_recursed()
    {
        var result = Runner(PythonLibrary.FromSource("loop", "import loop\n")).Run("import loop");

        Assert.False(result.Succeeded);
        Assert.Equal("ImportError", result.ExceptionType);
    }

    [Fact]
    public void A_library_that_will_not_compile_fails_the_import()
    {
        var result = Runner(PythonLibrary.FromSource("broken", "def f(:\n")).Run("import broken");

        Assert.False(result.Succeeded);
        Assert.Equal("ImportError", result.ExceptionType);
        Assert.Contains("broken", result.Exception!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_library_that_raises_while_loading_leaves_nothing_behind()
    {
        var result = Runner(PythonLibrary.FromSource("bad", "raise ValueError('no')\n")).Run("""
            try:
                import bad
            except ValueError as error:
                print('caught', error)

            try:
                import bad
            except ValueError as error:
                print('caught again', error)
            """);

        // A half-built module must not be cached: the second import runs the body again and
        // fails the same way, rather than handing back a module that never finished loading.
        Assert.Null(result.Traceback);
        Assert.Equal("caught no\ncaught again no\n", result.Stdout);
    }

    [Fact]
    public void A_host_library_runs_under_the_program_s_limits()
    {
        var runner = new PythonRunner(new ExecutionLimits { MaxInstructions = 5_000 });

        runner.Libraries.Add(PythonLibrary.FromSource("burn", """
            def spin():
                total = 0
                for i in range(1_000_000):
                    total += i
                return total
            """));

        var result = runner.Run("import burn\nburn.spin()");

        // A library is sandboxed code like any other: it cannot spend more than the run has.
        Assert.False(result.Succeeded);
        Assert.Contains("instruction", result.Exception!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_host_library_can_call_back_into_the_interpreter()
    {
        var runner = Runner(PythonLibrary.FromFactory("apply", context => context.Module()
            .Add("twice", arguments =>
                context.Machine.Call(arguments[0], [context.Machine.Call(arguments[0], [arguments[1]])]))));

        var result = runner.Run("""
            import apply
            print(apply.twice(lambda x: x + 3, 1))
            """);

        Assert.Null(result.Traceback);
        Assert.Equal("7\n", result.Stdout);
    }

    [Fact]
    public void A_host_library_reads_the_run_s_clock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero));

        var runner = Runner(PythonLibrary.FromFactory("stamp", context => context.Module()
            .Add("now", _ => new PyStr(context.TimeProvider.GetUtcNow().ToString("O")))));

        runner.TimeProvider = clock;

        var result = runner.Run("import stamp\nprint(stamp.now())");

        Assert.Null(result.Traceback);
        Assert.StartsWith("2001-02-03T04:05:06", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void A_host_library_sees_that_there_is_no_filesystem()
    {
        var runner = Runner(PythonLibrary.FromFactory("storage", context => context.Module()
            .Add("available", _ => context.FileSystem is null ? PyBool.False : PyBool.True)));

        var result = runner.Run("import storage\nprint(storage.available())");

        Assert.Null(result.Traceback);
        Assert.Equal("False\n", result.Stdout);
    }

    [Fact]
    public void A_module_the_host_supplies_eagerly_still_works()
    {
        // `Modules` predates `Libraries` and stays supported for a module with no state.
        var runner = new PythonRunner();
        runner.Modules["constants"] = new Modules.PyModuleObject("constants").Add("answer", new PyInt(42));

        var result = runner.Run("import constants\nprint(constants.answer)");

        Assert.Null(result.Traceback);
        Assert.Equal("42\n", result.Stdout);
    }

    [Fact]
    public void A_host_function_reads_the_sandbox_filesystem()
    {
        var runner = new PythonRunner { FileSystem = new MemoryFileSystem { ["/etc/motd"] = "be excellent\n" } };

        runner.HostFunctions["motd"] = (context, _) => new PyStr(context.ReadText("/etc/motd").Trim());

        var result = runner.Run("print(motd())");

        Assert.Null(result.Traceback);
        Assert.Equal("be excellent\n", result.Stdout);
    }

    [Fact]
    public void A_host_function_writes_to_the_sandbox_filesystem()
    {
        var files = new MemoryFileSystem();
        var runner = new PythonRunner { FileSystem = files };

        runner.HostFunctions["save"] = (context, arguments) =>
        {
            context.WriteText(arguments[0].Display(), arguments[1].Display());
            return PyNone.Instance;
        };

        var result = runner.Run("save('/out.txt', 'from C#')\nprint(open('/out.txt').read())\n");

        // The host wrote it and the program read it back: one filesystem, not two.
        Assert.Null(result.Traceback);
        Assert.Equal("from C#\n", result.Stdout);
        Assert.Equal("from C#", files["/out.txt"]);
    }

    [Fact]
    public void A_host_function_sees_the_working_directory_and_the_environment()
    {
        var runner = new PythonRunner { FileSystem = new MemoryFileSystem { WorkingDirectory = "/work" } };

        runner.HostFunctions["where"] = (context, _) =>
            new PyStr($"{context.WorkingDirectory} {context.Environment["USER"]}");

        var result = runner.Run("print(where())");

        Assert.Null(result.Traceback);
        Assert.Equal("/work tester\n", result.Stdout);
    }

    [Fact]
    public void A_host_function_without_a_filesystem_reports_it_rather_than_crashing()
    {
        var runner = new PythonRunner();

        runner.HostFunctions["motd"] = (context, _) => new PyStr(context.ReadText("/etc/motd"));

        var result = runner.Run("try:\n    motd()\nexcept OSError as error:\n    print('no filesystem:', error)\n");

        // A host exception escaping into a sandboxed program would be the sandbox leaking;
        // the absence of storage arrives as an error the program can catch instead.
        Assert.Null(result.Traceback);
        Assert.Equal("no filesystem: motd: this sandbox has no filesystem\n", result.Stdout);
    }

    [Fact]
    public void A_library_of_context_functions_works_over_the_sandbox()
    {
        var files = new MemoryFileSystem { ["/data.txt"] = "alpha\nbe\ngamma-ray\n" };

        var library = PythonLibrary.FromFunctions(
            "lines",
            new Dictionary<string, Func<PythonHostContext, PyObject[], PyObject>>
            {
                ["count"] = (context, arguments) => new PyInt(
                    context.ReadText(arguments[0].Display()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length),

                ["longest"] = (context, arguments) => new PyStr(
                    context.ReadText(arguments[0].Display())
                        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        .MaxBy(line => line.Length) ?? string.Empty),
            });

        var runner = new PythonRunner { FileSystem = files };
        runner.Libraries.Add(library);

        var result = runner.Run("import lines\nprint(lines.count('/data.txt'), lines.longest('/data.txt'))\n");

        Assert.Null(result.Traceback);
        Assert.Equal("3 gamma-ray\n", result.Stdout);
    }

    [Fact]
    public void A_host_function_may_call_back_into_the_program()
    {
        var runner = new PythonRunner { FileSystem = new MemoryFileSystem { ["/n.txt"] = "7" } };

        runner.HostFunctions["with_number"] = (context, arguments) =>
            context.Machine.Call(arguments[0], [new PyInt(int.Parse(context.ReadText("/n.txt"), null))]);

        var result = runner.Run("print(with_number(lambda n: n * 3))");

        Assert.Null(result.Traceback);
        Assert.Equal("21\n", result.Stdout);
    }

    [Fact]
    public void Context_free_external_functions_still_work_alongside_them()
    {
        var runner = new PythonRunner();

        runner.ExternalFunctions["plain"] = _ => new PyStr("no context");
        runner.HostFunctions["contextual"] = (context, _) => new PyStr(context.WorkingDirectory);

        var result = runner.Run("print(plain(), contextual())");

        Assert.Null(result.Traceback);
        Assert.Equal("no context /\n", result.Stdout);
    }

    /// <summary>A filesystem held in a dictionary, enough for host code to work against.</summary>
    private sealed class MemoryFileSystem : IPyFileSystem
    {
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

        public string this[string path]
        {
            get => _files[path];
            set => _files[path] = value;
        }

        public string WorkingDirectory { get; init; } = "/";

        public IReadOnlyDictionary<string, string> Environment { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal) { ["USER"] = "tester" };

        public bool Exists(string path) => _files.ContainsKey(path);

        public bool IsFile(string path) => _files.ContainsKey(path);

        public bool IsDirectory(string path) => !_files.ContainsKey(path);

        public byte[] Read(string path) => _files.TryGetValue(path, out var text)
            ? System.Text.Encoding.UTF8.GetBytes(text)
            : throw new PyRaise(new PyException(PyExceptionType.FileNotFoundError, $"No such file: '{path}'"));

        public void Write(string path, byte[] content) => _files[path] = System.Text.Encoding.UTF8.GetString(content);

        public void Append(string path, byte[] content) =>
            _files[path] = _files.GetValueOrDefault(path, string.Empty) + System.Text.Encoding.UTF8.GetString(content);

        public void Remove(string path) => _files.Remove(path);

        public void CreateDirectory(string path, bool parents, bool existsOk) { }

        public void RemoveDirectory(string path) { }

        public IReadOnlyList<string> List(string path) => [.. _files.Keys];

        public long Size(string path) => Read(path).Length;

        public void Rename(string from, string to)
        {
            _files[to] = _files[from];
            _files.Remove(from);
        }

        public int Mode(string path) => 0b1000_000_110_100_100;

        public double ModifiedAt(string path) => 0;
    }

    /// <summary>A clock pinned to one instant, so a test can pin what a library reports.</summary>
    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FakeTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
