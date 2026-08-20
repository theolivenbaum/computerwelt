using Computerwelt.Emulation.Python;

// A thin driver over the library: runs a script and reports the same way CPython does.
// It grants no capability the library does not already have — there is still no
// filesystem, no environment and no network inside the sandbox.
if (args.Length == 0)
{
    Console.Error.WriteLine("usage: monty <script.py> | monty -c <source>");
    return 2;
}

var source = args[0] == "-c" && args.Length > 1
    ? args[1]
    : File.ReadAllText(args[0]);

var name = args[0] == "-c" ? "<stdin>" : Path.GetFileName(args[0]);

var result = new MontyRunner().Run(source, name);

Console.Out.Write(result.Stdout);
Console.Error.Write(result.Stderr);

if (result.Succeeded)
{
    return 0;
}

Console.Error.WriteLine(result.Traceback);
return 1;
