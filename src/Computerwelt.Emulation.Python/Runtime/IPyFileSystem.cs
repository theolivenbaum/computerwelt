namespace Computerwelt.Emulation.Python.Runtime;

/// <summary>
/// The filesystem a Python program can reach.
/// </summary>
/// <remarks>
/// <para>
/// Nothing is reachable by default: a <see cref="MontyRunner"/> with no filesystem raises
/// <c>ModuleNotFoundError</c> for <c>os</c> and <c>pathlib</c> and <c>NameError</c> for
/// <c>open</c>, which is the honest report — the program is not being denied access to a
/// real filesystem, there is no filesystem at all.
/// </para>
/// <para>
/// A host that wants to expose storage implements this over whatever it likes: an in-memory
/// tree, a shell's virtual filesystem, a subdirectory of a real one. Paths arrive as POSIX
/// strings, already absolute or relative to <see cref="WorkingDirectory"/>.
/// </para>
/// </remarks>
public interface IPyFileSystem
{
    /// <summary>The directory relative paths resolve against.</summary>
    string WorkingDirectory { get; }

    /// <summary>The environment <c>os.environ</c> and <c>os.getenv</c> report.</summary>
    IReadOnlyDictionary<string, string> Environment { get; }

    /// <summary>True when anything exists at <paramref name="path"/>.</summary>
    bool Exists(string path);

    /// <summary>True when <paramref name="path"/> names a regular file.</summary>
    bool IsFile(string path);

    /// <summary>True when <paramref name="path"/> names a directory.</summary>
    bool IsDirectory(string path);

    /// <summary>Reads a file's bytes.</summary>
    /// <exception cref="PyRaise">Raises <c>FileNotFoundError</c> when it does not exist.</exception>
    byte[] Read(string path);

    /// <summary>Writes a file, replacing it if it exists.</summary>
    void Write(string path, byte[] content);

    /// <summary>Appends to a file, creating it if needed.</summary>
    void Append(string path, byte[] content);

    /// <summary>Removes a file.</summary>
    void Remove(string path);

    /// <summary>Creates a directory.</summary>
    /// <param name="path">Where to create it.</param>
    /// <param name="parents">Whether to create missing parents.</param>
    /// <param name="existsOk">Whether an existing directory is acceptable.</param>
    void CreateDirectory(string path, bool parents, bool existsOk);

    /// <summary>Removes an empty directory.</summary>
    void RemoveDirectory(string path);

    /// <summary>Lists a directory's entry names, unordered.</summary>
    IReadOnlyList<string> List(string path);

    /// <summary>The size of a file in bytes.</summary>
    long Size(string path);

    /// <summary>Renames a file or directory.</summary>
    void Rename(string from, string to);

    /// <summary>The POSIX mode bits of a file or directory, including its type bits.</summary>
    int Mode(string path);

    /// <summary>The modification time, as a Unix timestamp in seconds.</summary>
    double ModifiedAt(string path);
}
