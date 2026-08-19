namespace Bashkit.SpecTests;

/// <summary>
/// Parses the upstream <c>.test.sh</c> format.
/// </summary>
/// <remarks>
/// The format is deliberately language-agnostic — a name, a script, an expected stdout —
/// which is why the Rust suite transfers to the port unchanged and remains the single
/// definition of correct behaviour for both implementations.
/// <code>
/// ### test_name
/// # optional description
/// echo hello
/// ### expect
/// hello
/// ### end
/// </code>
/// Directives: <c>### exit_code: N</c>, <c>### skip: reason</c>,
/// <c>### bash_diff: reason</c>, <c>### paused_time</c>.
/// </remarks>
public static class SpecFileParser
{
    /// <summary>Parses every case in one spec file.</summary>
    public static List<SpecCase> Parse(string file, string content)
    {
        var cases = new List<SpecCase>();

        string? name = null;
        var description = string.Empty;
        var script = new List<string>();
        var expected = new List<string>();
        int? exitCode = null;
        var skip = false;
        string? skipReason = null;
        var bashDiff = false;
        var pausedTime = false;

        var inScript = false;
        var inExpect = false;

        void Flush()
        {
            if (name is null)
            {
                return;
            }

            var stdout = string.Join('\n', expected);
            if (stdout.Length > 0)
            {
                stdout += "\n";
            }

            cases.Add(new SpecCase
            {
                Name = name,
                File = file,
                Description = description,
                Script = string.Join('\n', script),
                ExpectedStdout = stdout,
                ExpectedExitCode = exitCode,
                Skip = skip,
                SkipReason = skipReason,
                BashDiff = bashDiff,
                PausedTime = pausedTime,
            });

            name = null;
            description = string.Empty;
            script.Clear();
            expected.Clear();
            exitCode = null;
            skip = false;
            skipReason = null;
            bashDiff = false;
            pausedTime = false;
        }

        foreach (var line in content.Split('\n'))
        {
            var trimmedLine = line.TrimEnd('\r');

            if (trimmedLine.StartsWith("### ", StringComparison.Ordinal))
            {
                var directive = trimmedLine[4..].Trim();

                switch (directive)
                {
                    case "expect":
                        inScript = false;
                        inExpect = true;
                        continue;

                    case "end":
                        Flush();
                        inScript = false;
                        inExpect = false;
                        continue;

                    case "skip":
                        skip = true;
                        continue;

                    case "bash_diff":
                        bashDiff = true;
                        continue;

                    case "paused_time":
                        pausedTime = true;
                        continue;
                }

                if (directive.StartsWith("exit_code:", StringComparison.Ordinal))
                {
                    if (int.TryParse(directive[10..].Trim(), out var parsed))
                    {
                        exitCode = parsed;
                    }

                    continue;
                }

                if (directive.StartsWith("skip:", StringComparison.Ordinal))
                {
                    skip = true;
                    skipReason = directive[5..].Trim();
                    continue;
                }

                if (directive.StartsWith("bash_diff:", StringComparison.Ordinal))
                {
                    bashDiff = true;
                    continue;
                }

                // Anything else on a `### ` line starts a new case.
                Flush();
                name = directive;
                inScript = true;
                inExpect = false;
                continue;
            }

            if (inScript)
            {
                // The first `# ` comment in a case is its description, not script text.
                if (trimmedLine.StartsWith("# ", StringComparison.Ordinal)
                    && script.Count == 0
                    && description.Length == 0)
                {
                    description = trimmedLine[2..];
                    continue;
                }

                script.Add(trimmedLine);
                continue;
            }

            if (inExpect)
            {
                expected.Add(trimmedLine);
            }
        }

        Flush();
        return cases;
    }
}
