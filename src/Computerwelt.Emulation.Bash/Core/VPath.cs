using System.Diagnostics.CodeAnalysis;

namespace Computerwelt.Emulation.Bash;

/// <summary>
/// An immutable POSIX path inside the virtual filesystem.
/// </summary>
/// <remarks>
/// <para>
/// The sandbox must behave identically on Linux, macOS and Windows, so virtual paths
/// deliberately do <b>not</b> use <see cref="System.IO.Path"/>. On Windows
/// <c>Path.Combine("/etc", "C:\\secrets")</c> yields <c>C:\secrets</c> and
/// <c>Path.GetFullPath</c> resolves against the host drive — either would breach the
/// sandbox. <see cref="VPath"/> implements POSIX semantics directly instead.
/// </para>
/// <para>
/// A <see cref="VPath"/> is always stored in normalized form: <c>/</c> separators, no
/// empty or <c>.</c> segments, no trailing slash (except for the root <c>/</c>), and
/// <c>..</c> resolved lexically where possible. Lexical resolution matches bash's own
/// <c>cd -L</c> default and is what upstream <c>fs/posix.rs</c> does.
/// </para>
/// </remarks>
public readonly struct VPath : IEquatable<VPath>, IComparable<VPath>
{
    /// <summary>The filesystem root, <c>/</c>.</summary>
    public static readonly VPath Root = new("/");

    /// <summary>The empty path. Distinct from <see cref="Root"/>.</summary>
    public static readonly VPath Empty = default;

    private readonly string? _value;

    private VPath(string normalized) => _value = normalized;

    /// <summary>The normalized textual form of the path.</summary>
    public string Value => _value ?? string.Empty;

    /// <summary>True when this path has no segments and no root.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(_value);

    /// <summary>True when the path starts at the root.</summary>
    public bool IsAbsolute => _value is { Length: > 0 } v && v[0] == '/';

    /// <summary>True when the path is exactly the root.</summary>
    public bool IsRoot => _value is "/";

    /// <summary>Parses and normalizes <paramref name="path"/>.</summary>
    public static VPath Parse(string? path) => new(Normalize(path));

    /// <summary>Implicitly parses a string into a <see cref="VPath"/>.</summary>
    public static implicit operator VPath(string path) => Parse(path);

    /// <summary>
    /// Joins <paramref name="relative"/> onto this path. An absolute
    /// <paramref name="relative"/> replaces this path entirely, matching POSIX.
    /// </summary>
    public VPath Join(string relative)
    {
        if (string.IsNullOrEmpty(relative))
        {
            return this;
        }

        if (relative[0] == '/')
        {
            return Parse(relative);
        }

        var basePart = Value;
        return basePart.Length == 0
            ? Parse(relative)
            : Parse(basePart.EndsWith('/') ? basePart + relative : basePart + "/" + relative);
    }

    /// <summary>Joins another path onto this one.</summary>
    public VPath Join(VPath relative) => Join(relative.Value);

    /// <summary>
    /// Resolves <paramref name="path"/> against <paramref name="cwd"/>, producing an
    /// absolute normalized path. Relative inputs are anchored at <paramref name="cwd"/>.
    /// </summary>
    public static VPath Resolve(VPath cwd, string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return cwd;
        }

        return path[0] == '/' ? Parse(path) : cwd.Join(path);
    }

    /// <summary>
    /// The parent directory, or <see cref="Root"/> for top-level absolute paths.
    /// Returns <see langword="null"/> when there is no parent (root, or a bare relative name).
    /// </summary>
    public VPath? Parent
    {
        get
        {
            var v = Value;
            if (v.Length == 0 || v == "/")
            {
                return null;
            }

            var idx = v.LastIndexOf('/');
            return idx switch
            {
                < 0 => (VPath?)null,
                0 => Root,
                _ => new VPath(v[..idx]),
            };
        }
    }

    /// <summary>The final path segment, or an empty string for the root.</summary>
    public string FileName
    {
        get
        {
            var v = Value;
            if (v.Length == 0 || v == "/")
            {
                return string.Empty;
            }

            var idx = v.LastIndexOf('/');
            return idx < 0 ? v : v[(idx + 1)..];
        }
    }

    /// <summary>
    /// The final segment with its last extension removed. <c>.bashrc</c> is treated as a
    /// name, not an extension, matching POSIX tooling.
    /// </summary>
    public string FileNameWithoutExtension
    {
        get
        {
            var name = FileName;
            var dot = name.LastIndexOf('.');
            return dot > 0 ? name[..dot] : name;
        }
    }

    /// <summary>The final extension including the leading dot, or an empty string.</summary>
    public string Extension
    {
        get
        {
            var name = FileName;
            var dot = name.LastIndexOf('.');
            return dot > 0 ? name[dot..] : string.Empty;
        }
    }

    /// <summary>The path segments, excluding the root marker.</summary>
    public string[] Segments =>
        Value.Split('/', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>
    /// True when this path is <paramref name="ancestor"/> or lies beneath it. Used for
    /// jail-escape checks; both paths must already be normalized and absolute.
    /// </summary>
    public bool IsUnder(VPath ancestor)
    {
        var a = ancestor.Value;
        var v = Value;

        if (a.Length == 0)
        {
            return false;
        }

        if (a == "/")
        {
            return v.Length > 0 && v[0] == '/';
        }

        return v.Length >= a.Length
            && v.StartsWith(a, StringComparison.Ordinal)
            && (v.Length == a.Length || v[a.Length] == '/');
    }

    /// <summary>
    /// Returns this path expressed relative to <paramref name="basePath"/>, or
    /// <see langword="null"/> when it does not lie beneath it.
    /// </summary>
    public VPath? RelativeTo(VPath basePath)
    {
        if (!IsUnder(basePath))
        {
            return null;
        }

        var b = basePath.Value;
        var v = Value;
        if (v.Length == b.Length)
        {
            return Parse(".");
        }

        var start = b == "/" ? 1 : b.Length + 1;
        return new VPath(v[start..]);
    }

    /// <summary>
    /// Normalizes a raw path string: collapses duplicate separators, drops <c>.</c>
    /// segments, and resolves <c>..</c> lexically. Leading <c>..</c> on a relative path is
    /// preserved; on an absolute path it collapses into the root, as POSIX requires.
    /// </summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var absolute = path[0] == '/';
        var segments = new List<string>();

        var span = path.AsSpan();
        var start = 0;
        for (var i = 0; i <= span.Length; i++)
        {
            if (i != span.Length && span[i] != '/')
            {
                continue;
            }

            var segment = span[start..i];
            start = i + 1;

            if (segment.Length == 0 || segment is ".")
            {
                continue;
            }

            if (segment is "..")
            {
                if (segments.Count > 0 && segments[^1] != "..")
                {
                    segments.RemoveAt(segments.Count - 1);
                }
                else if (!absolute)
                {
                    segments.Add("..");
                }

                // On an absolute path, `/..` is `/`; the segment is simply dropped.
                continue;
            }

            segments.Add(segment.ToString());
        }

        if (segments.Count == 0)
        {
            return absolute ? "/" : ".";
        }

        var joined = string.Join('/', segments);
        return absolute ? "/" + joined : joined;
    }

    /// <inheritdoc />
    public bool Equals(VPath other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals([NotNullWhen(true)] object? obj) => obj is VPath other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    /// <inheritdoc />
    public int CompareTo(VPath other) => string.CompareOrdinal(Value, other.Value);

    /// <inheritdoc />
    public override string ToString() => Value;

    /// <summary>Compares two paths for equality.</summary>
    public static bool operator ==(VPath left, VPath right) => left.Equals(right);

    /// <summary>Compares two paths for inequality.</summary>
    public static bool operator !=(VPath left, VPath right) => !left.Equals(right);

    /// <summary>Orders two paths.</summary>
    public static bool operator <(VPath left, VPath right) => left.CompareTo(right) < 0;

    /// <summary>Orders two paths.</summary>
    public static bool operator >(VPath left, VPath right) => left.CompareTo(right) > 0;

    /// <summary>Orders two paths.</summary>
    public static bool operator <=(VPath left, VPath right) => left.CompareTo(right) <= 0;

    /// <summary>Orders two paths.</summary>
    public static bool operator >=(VPath left, VPath right) => left.CompareTo(right) >= 0;
}
