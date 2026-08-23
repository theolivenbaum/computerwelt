using BenchmarkDotNet.Attributes;
using Computerwelt.Emulation.Bash;

namespace Computerwelt.Benchmarks;

/// <summary>
/// The virtual filesystem, and the shell operations that walk it.
/// </summary>
/// <remarks>
/// Every builtin reaches storage through <see cref="IFileSystem"/>, so path resolution and
/// directory listing sit under <c>ls</c>, <c>find</c>, <c>grep -r</c> and globbing alike.
/// The tree is small but deep enough that a per-segment cost shows.
/// </remarks>
[MemoryDiagnoser]
public class FileSystemBenchmarks
{
    private readonly InMemoryFileSystem _fs = new();
    private Bash _bash = null!;
    private static readonly byte[] Content = System.Text.Encoding.UTF8.GetBytes(
        string.Join('\n', Enumerable.Range(0, 200).Select(i => $"line {i} of a source file")));

    [GlobalSetup]
    public async Task SetupAsync()
    {
        for (var dir = 0; dir < 8; dir++)
        {
            await _fs.CreateDirectoryAsync($"/work/pkg{dir}/src", recursive: true);
            for (var file = 0; file < 8; file++)
            {
                await _fs.WriteFileAsync($"/work/pkg{dir}/src/file{file}.txt", Content);
            }
        }

        _bash = Bash.CreateBuilder().WithFileSystem(_fs).WithWorkingDirectory("/work").Build();
    }

    [Benchmark]
    public async Task<int> ReadFile() => (await _fs.ReadFileAsync("/work/pkg3/src/file4.txt")).Length;

    [Benchmark]
    public async Task<int> WriteFile()
    {
        await _fs.WriteFileAsync("/work/scratch.txt", Content);
        return Content.Length;
    }

    [Benchmark]
    public async Task<int> ListDirectory() => (await _fs.ReadDirectoryAsync("/work/pkg3/src")).Count;

    [Benchmark]
    public async Task<int> GlobRecursive() => (await _bash.ExecAsync("echo /work/*/src/*.txt")).ExitCode;

    [Benchmark]
    public async Task<int> FindTree() => (await _bash.ExecAsync("find /work -name '*.txt'")).ExitCode;

    [Benchmark]
    public async Task<int> GrepTree() => (await _bash.ExecAsync("grep -rn 'line 199' /work")).ExitCode;

    [Benchmark]
    public async Task<int> CatAndCount() => (await _bash.ExecAsync("cat /work/pkg1/src/*.txt | wc -l")).ExitCode;
}
