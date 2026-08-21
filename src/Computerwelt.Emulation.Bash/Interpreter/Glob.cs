namespace Computerwelt.Emulation.Bash.Interpreter;

/// <summary>
/// Pathname expansion: turns a pattern into the sorted list of paths it matches.
/// </summary>
/// <remarks>
/// <para>
/// Globbing walks the virtual filesystem component by component rather than listing
/// everything and filtering, because a pattern such as <c>a/*/b</c> must only read the
/// directories it can actually reach. That keeps the cost proportional to the matched
/// subtree, not to the whole tree.
/// </para>
/// <para>
/// A leading dot is only matched by an explicit dot in the pattern, so <c>*</c> never
/// returns <c>.bashrc</c> unless <c>dotglob</c> is set.
/// </para>
/// </remarks>
public static class Glob
{
    /// <summary>Expands <paramref name="pattern"/> against the filesystem.</summary>
    public static async ValueTask<List<string>> ExpandAsync(
        IFileSystem fileSystem,
        ShellState state,
        string pattern,
        ExecutionBudget budget,
        CancellationToken cancellationToken = default)
    {
        var absolute = pattern.StartsWith('/');
        var segments = pattern.Split('/', StringSplitOptions.None);

        // A trailing slash constrains the match to directories.
        var directoriesOnly = segments.Length > 1 && segments[^1].Length == 0;
        if (directoriesOnly)
        {
            segments = segments[..^1];
        }

        var startPath = absolute ? VPath.Root : state.WorkingDirectory;
        var startPrefix = absolute ? "/" : string.Empty;

        var frontier = new List<(VPath Path, string Display)> { (startPath, startPrefix) };
        var first = true;

        foreach (var segment in segments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            budget.ChargeWork(1);

            if (segment.Length == 0)
            {
                first = false;
                continue;
            }

            var next = new List<(VPath Path, string Display)>();

            foreach (var (path, display) in frontier)
            {
                if (!PatternMatcher.HasMetacharacters(segment, state.Options.ExtGlob))
                {
                    var literal = Unescape(segment);
                    var candidate = path.Join(literal);
                    if (await fileSystem.ExistsAsync(candidate, cancellationToken))
                    {
                        next.Add((candidate, Combine(display, literal, first && absolute)));
                    }

                    continue;
                }

                if (segment == "**" && state.Options.GlobStar)
                {
                    await CollectRecursiveAsync(fileSystem, path, display, next, budget, cancellationToken);
                    continue;
                }

                await CollectMatchesAsync(fileSystem, state, path, display, segment, next, budget, cancellationToken);
            }

            frontier = next;
            first = false;

            if (frontier.Count > budget.Limits.MaxGlobMatches)
            {
                throw new LimitExceededException("max_glob_matches", budget.Limits.MaxGlobMatches);
            }
        }

        var results = new List<string>(frontier.Count);
        foreach (var (path, display) in frontier)
        {
            if (directoriesOnly)
            {
                var metadata = await TryStatAsync(fileSystem, path, cancellationToken);
                if (metadata is not { IsDirectory: true })
                {
                    continue;
                }

                results.Add(display + "/");
                continue;
            }

            results.Add(display);
        }

        results.Sort(StringComparer.Ordinal);
        return results;
    }

    private static async ValueTask CollectMatchesAsync(
        IFileSystem fileSystem,
        ShellState state,
        VPath path,
        string display,
        string segment,
        List<(VPath Path, string Display)> output,
        ExecutionBudget budget,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<DirectoryEntry> entries;
        try
        {
            entries = await fileSystem.ReadDirectoryAsync(path, cancellationToken);
        }
        catch (FileSystemException)
        {
            return;
        }

        var matchesDot = state.Options.DotGlob || segment.StartsWith('.');

        foreach (var entry in entries)
        {
            budget.ChargeWork(1);

            if (!matchesDot && entry.Name.StartsWith('.'))
            {
                continue;
            }

            if (!PatternMatcher.IsMatch(entry.Name, segment, state.Options.NoCaseGlob, state.Options.ExtGlob))
            {
                continue;
            }

            output.Add((path.Join(entry.Name), Combine(display, entry.Name, absoluteRoot: false)));
        }
    }

    /// <summary>Collects this directory and every descendant, for <c>**</c>.</summary>
    private static async ValueTask CollectRecursiveAsync(
        IFileSystem fileSystem,
        VPath path,
        string display,
        List<(VPath Path, string Display)> output,
        ExecutionBudget budget,
        CancellationToken cancellationToken)
    {
        output.Add((path, display));

        IReadOnlyList<DirectoryEntry> entries;
        try
        {
            entries = await fileSystem.ReadDirectoryAsync(path, cancellationToken);
        }
        catch (FileSystemException)
        {
            return;
        }

        foreach (var entry in entries)
        {
            budget.ChargeWork(1);

            if (entry.Name.StartsWith('.'))
            {
                continue;
            }

            var child = path.Join(entry.Name);
            var childDisplay = Combine(display, entry.Name, absoluteRoot: false);

            if (entry.IsDirectory)
            {
                await CollectRecursiveAsync(fileSystem, child, childDisplay, output, budget, cancellationToken);
            }
            else
            {
                output.Add((child, childDisplay));
            }
        }
    }

    private static async ValueTask<FileMetadata?> TryStatAsync(IFileSystem fileSystem, VPath path, CancellationToken cancellationToken)
    {
        try
        {
            return await fileSystem.StatAsync(path, cancellationToken);
        }
        catch (FileSystemException)
        {
            return null;
        }
    }

    private static string Combine(string display, string name, bool absoluteRoot)
    {
        if (display.Length == 0)
        {
            return name;
        }

        if (display == "/")
        {
            return "/" + name;
        }

        _ = absoluteRoot;
        return display + "/" + name;
    }

    private static string Unescape(string segment)
    {
        if (!segment.Contains('\\', StringComparison.Ordinal))
        {
            return segment;
        }

        var builder = new System.Text.StringBuilder(segment.Length);
        for (var i = 0; i < segment.Length; i++)
        {
            if (segment[i] == '\\' && i + 1 < segment.Length)
            {
                builder.Append(segment[++i]);
                continue;
            }

            builder.Append(segment[i]);
        }

        return builder.ToString();
    }
}
