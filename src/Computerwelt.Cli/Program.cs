using Computerwelt.Emulation.Bash;
using Computerwelt;

// A thin driver over the library: enough to try a script by hand and to smoke-test the
// runtime without a test host. It deliberately exposes no capability the library does not
// already grant by default.
var arguments = args;

if (arguments.Length > 0 && arguments[0] is "-h" or "--help")
{
    Console.WriteLine("""
        computerwelt — sandboxed bash with an embedded Python

          computerwelt -c <script> [args...]   run a script given on the command line
          computerwelt <file> [args...]        run a script from the virtual filesystem
          computerwelt                         start a REPL

        The `python` command runs against the same virtual filesystem as the shell.
        That filesystem is empty at startup, and nothing on the host is reachable.
        """);
    return 0;
}

var bash = Bash.CreateBuilder()
    .WithWorkingDirectory("/home/user")
    .WithPython()
    .Build();

await bash.FileSystem.CreateDirectoryAsync("/home/user", recursive: true);

if (arguments.Length >= 2 && arguments[0] == "-c")
{
    var options = new ExecOptions { PositionalParameters = arguments[2..] };
    return await RunAsync(bash, arguments[1], options);
}

if (arguments.Length >= 1)
{
    var path = VPath.Parse(arguments[0]);

    if (!await bash.FileSystem.ExistsAsync(path))
    {
        Console.Error.WriteLine($"computerwelt: {arguments[0]}: No such file or directory");
        return ExitCodes.NotFound;
    }

    var bytes = await bash.FileSystem.ReadFileAsync(path);
    var script = System.Text.Encoding.UTF8.GetString(bytes);
    var options = new ExecOptions { PositionalParameters = arguments[1..], ScriptName = arguments[0] };
    return await RunAsync(bash, script, options);
}

return await RunReplAsync(bash);

static async Task<int> RunAsync(Bash bash, string script, ExecOptions options)
{
    var result = await bash.ExecAsync(script, options);
    Console.Out.Write(result.Stdout.ToString());
    Console.Error.Write(result.Stderr.ToString());
    return result.ExitCode;
}

static async Task<int> RunReplAsync(Bash bash)
{
    Console.WriteLine("computerwelt — sandboxed bash + python. Ctrl-D or `exit` to quit.");

    while (true)
    {
        Console.Write($"{bash.WorkingDirectory}$ ");
        var line = Console.ReadLine();

        if (line is null)
        {
            Console.WriteLine();
            return 0;
        }

        if (line.Length == 0)
        {
            continue;
        }

        var result = await bash.ExecAsync(line);
        Console.Out.Write(result.Stdout.ToString());
        Console.Error.Write(result.Stderr.ToString());
    }
}
