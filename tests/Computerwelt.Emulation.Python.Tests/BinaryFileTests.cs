using Computerwelt.Emulation.Python.Runtime;
using Xunit;

namespace Computerwelt.Emulation.Python.Tests;

/// <summary>
/// A file opened in binary mode carries bytes, not decoded text.
/// </summary>
/// <remarks>
/// The corpus reads and writes text, so nothing in it noticed that a binary file was being
/// round-tripped through UTF-8 — which replaces every byte that is not valid UTF-8 with
/// U+FFFD and silently changes the file's length. A program that writes a PNG and reads it
/// back has to get the PNG.
/// </remarks>
public sealed class BinaryFileTests
{
    /// <summary>Bytes no UTF-8 decoder can round trip: a lone 0x89, a 0xFF, an 0xFE.</summary>
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0xFE, 0x41, 0x0A, 0xC3, 0x0A];

    private static RunResult Run(string source, ByteFileSystem files) =>
        new PythonRunner { FileSystem = files }.Run(source);

    [Fact]
    public void A_binary_file_round_trips_every_byte()
    {
        var files = new ByteFileSystem();

        var result = Run(
            """
            raw = bytes([0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF, 0xFE, 0x41, 0x0A, 0xC3, 0x0A])

            f = open('/x.bin', 'wb')
            f.write(raw)
            f.close()

            assert open('/x.bin', 'rb').read() == raw
            """,
            files);

        Assert.True(result.Succeeded, result.Traceback);
        Assert.Equal(Png, files.Bytes("/x.bin"));
    }

    [Fact]
    public void A_sized_binary_read_counts_bytes()
    {
        var files = new ByteFileSystem();
        files.Write("/x.bin", Png);

        var result = Run(
            """
            with open('/x.bin', 'rb') as f:
                head = f.read(4)
                assert head == b'\x89PNG', head
                assert f.tell() == 4
                assert f.read() == bytes([0x00, 0xFF, 0xFE, 0x41, 0x0A, 0xC3, 0x0A])
            """,
            files);

        Assert.True(result.Succeeded, result.Traceback);
    }

    [Fact]
    public void Binary_lines_split_on_the_newline_byte()
    {
        var files = new ByteFileSystem();
        files.Write("/x.bin", Png);

        var result = Run(
            """
            lines = open('/x.bin', 'rb').readlines()

            assert len(lines) == 2
            assert len(lines[0]) == 9
            assert len(lines[1]) == 2
            assert [len(line) for line in open('/x.bin', 'rb')] == [9, 2]
            assert open('/x.bin', 'rb').readline() == lines[0]
            """,
            files);

        Assert.True(result.Succeeded, result.Traceback);
    }

    [Fact]
    public void Seeking_from_the_end_of_a_binary_file_counts_bytes()
    {
        var files = new ByteFileSystem();
        files.Write("/x.bin", Png);

        var result = Run(
            """
            with open('/x.bin', 'rb') as f:
                f.seek(-2, 2)
                assert f.tell() == 9
                assert f.read() == bytes([0xC3, 0x0A])
            """,
            files);

        Assert.True(result.Succeeded, result.Traceback);
    }

    [Fact]
    public void Writelines_keeps_bytes_as_they_were_given()
    {
        var files = new ByteFileSystem();

        var result = Run(
            """
            with open('/x.bin', 'wb') as f:
                f.writelines([bytes([0xFF, 0x0A]), bytes([0xFE])])
            """,
            files);

        Assert.True(result.Succeeded, result.Traceback);
        Assert.Equal(new byte[] { 0xFF, 0x0A, 0xFE }, files.Bytes("/x.bin"));
    }

    [Fact]
    public void A_text_file_still_reads_as_text()
    {
        var files = new ByteFileSystem();

        var result = Run(
            """
            with open('/t.txt', 'w') as f:
                f.write('one\ntwo\n')

            assert open('/t.txt').read() == 'one\ntwo\n'
            assert open('/t.txt').readlines() == ['one\n', 'two\n']
            assert list(open('/t.txt')) == ['one\n', 'two\n']
            assert open('/t.txt').readline() == 'one\n'
            """,
            files);

        Assert.True(result.Succeeded, result.Traceback);
    }

    /// <summary>A filesystem that stores bytes, which is the point of these tests.</summary>
    private sealed class ByteFileSystem : IPyFileSystem
    {
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

        public string WorkingDirectory => "/";

        public IReadOnlyDictionary<string, string> Environment { get; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public byte[] Bytes(string path) => _files[path];

        public bool Exists(string path) => _files.ContainsKey(path);

        public bool IsFile(string path) => _files.ContainsKey(path);

        public bool IsDirectory(string path) => !_files.ContainsKey(path);

        public byte[] Read(string path) => _files.TryGetValue(path, out var content)
            ? content
            : throw new PyRaise(new PyException(PyExceptionType.FileNotFoundError, $"No such file: '{path}'"));

        public void Write(string path, byte[] content) => _files[path] = content;

        public void Append(string path, byte[] content) =>
            _files[path] = [.. _files.GetValueOrDefault(path, []), .. content];

        public void Remove(string path) => _files.Remove(path);

        public void CreateDirectory(string path, bool parents, bool existsOk)
        {
        }

        public void RemoveDirectory(string path)
        {
        }

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
}
