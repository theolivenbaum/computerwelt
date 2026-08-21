using Xunit;

namespace Computerwelt.AgentTests;

/// <summary>
/// Rewriting source files with a short Python program.
/// </summary>
/// <remarks>
/// <para>
/// This is the operation <c>sed</c> cannot do: a multi-line, indentation-sensitive
/// replacement in a C# file, applied only if the text it expects is actually there. The
/// canonical shape is a heredoc — <c>python3 - &lt;&lt;'PY'</c> — holding a read, an
/// <c>assert old in text</c>, a replace and a write.
/// </para>
/// <para>
/// The assertion is the part that matters. A patch script that replaces nothing and exits
/// 0 is worse than one that fails, because the build that follows is then testing the old
/// code. These tests pin both directions.
/// </para>
/// </remarks>
public sealed class PythonPatchingTests
{
    [Fact]
    public async Task Heredoc_patch_applies_a_multi_line_replacement()
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync("""
            python3 - <<'PY'
            p = '/work/src/Widget.cs'
            s = open(p, encoding='utf-8').read()
            old = '''    public string Describe() => $"widget {Count}";'''
            new = '''    public string Describe() => $"widget #{Count}";

                public bool IsEmpty => Count == 0;'''
            assert old in s, p
            open(p, 'w', encoding='utf-8').write(s.replace(old, new))
            print('patched')
            PY
            """);

        Assert.Equal("patched\n", result.Stdout.ToString());
        Assert.Equal(0, result.ExitCode);

        var patched = await session.ReadAsync("/work/src/Widget.cs");
        Assert.Contains("widget #{Count}", patched, StringComparison.Ordinal);
        Assert.Contains("public bool IsEmpty => Count == 0;", patched, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_patch_whose_anchor_is_missing_fails_loudly()
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync("""
            python3 - <<'PY'
            p = '/work/src/Widget.cs'
            s = open(p, encoding='utf-8').read()
            assert 'no such text' in s, (p, s[:40])
            PY
            """);

        // Non-zero status and an AssertionError naming the file: enough to tell a stale
        // anchor from a genuine failure without reading the file back.
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("AssertionError", result.Stderr.ToString(), StringComparison.Ordinal);
        Assert.Contains("/work/src/Widget.cs", result.Stderr.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_patch_leaves_the_file_untouched()
    {
        var session = await AgentSession.NewAsync();

        var before = await session.ReadAsync("/work/src/Widget.cs");

        await session.RunAsync("""
            python3 - <<'PY'
            p = '/work/src/Widget.cs'
            s = open(p, encoding='utf-8').read()
            assert 'absent' in s
            open(p, 'w', encoding='utf-8').write(s.replace('Count', 'Total'))
            PY
            """);

        Assert.Equal(before, await session.ReadAsync("/work/src/Widget.cs"));
    }

    [Fact]
    public async Task Reading_bytes_and_decoding_utf_8_sig_strips_a_bom()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 - <<'PY'
            raw = open('/work/src/Gadget.cs', 'rb').read()
            has_bom = raw[:3] == b'\xef\xbb\xbf'
            text = raw.decode('utf-8-sig')
            print(has_bom, text.startswith('namespace'))
            PY
            """);

        // Decoding a BOM'd file as plain utf-8 leaves U+FEFF at the front, and every
        // subsequent `text.startswith(...)` quietly disagrees with what the editor shows.
        Assert.Equal("True True\n", output);
    }

    [Fact]
    public async Task A_rewrite_preserves_the_byte_order_mark_it_found()
    {
        var session = await AgentSession.NewAsync();

        await session.RunAsync("""
            python3 - <<'PY'
            p = '/work/src/Gadget.cs'
            raw = open(p, 'rb').read()
            bom = raw[:3] == b'\xef\xbb\xbf'
            text = raw.decode('utf-8-sig').replace('gadget', 'sprocket')
            out = text.encode('utf-8')
            if bom:
                out = b'\xef\xbb\xbf' + out
            open(p, 'wb').write(out)
            PY
            """);

        var bytes = await session.Bash.FileSystem.ReadFileAsync("/work/src/Gadget.cs");

        Assert.Equal<byte>([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.Contains("sprocket {Count}", await session.ReadAsync("/work/src/Gadget.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Regex_rewrites_every_match_across_a_tree()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 - <<'PY'
            import os, re
            changed = []
            for folder in ('/work/src', '/work/tests'):
                for name in sorted(os.listdir(folder)):
                    if not name.endswith('.cs'):
                        continue
                    p = folder + '/' + name
                    s = open(p, encoding='utf-8').read()
                    t = re.sub(r'\bpublic sealed class (\w+)', r'internal sealed class \1', s)
                    if t != s:
                        open(p, 'w', encoding='utf-8').write(t)
                        changed.append(name)
            print(' '.join(changed))
            PY
            """);

        Assert.Equal("Gadget.cs Widget.cs WidgetTests.cs\n", output);
        Assert.Contains("internal sealed class Widget", await session.ReadAsync("/work/src/Widget.cs"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Json_is_read_edited_and_written_back()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 - <<'PY'
            import json
            p = '/work/config.json'
            data = json.loads(open(p, encoding='utf-8').read())
            data['version'] = '2.0.0'
            data['features'].append('sprocket')
            open(p, 'w', encoding='utf-8').write(json.dumps(data, indent=2) + '\n')
            print(data['version'], len(data['features']))
            PY
            """);

        Assert.Equal("2.0.0 3\n", output);
        Assert.Contains("\"version\": \"2.0.0\"", await session.ReadAsync("/work/config.json"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_patch_reports_which_files_it_skipped()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 - <<'PY'
            import os
            wanted = 'public int Count'
            hits, misses = [], []
            for name in sorted(os.listdir('/work/src')):
                p = '/work/src/' + name
                s = open(p, 'rb').read().decode('utf-8-sig')
                (hits if wanted in s else misses).append(name)
            print('hit:', ','.join(hits))
            print('miss:', ','.join(misses))
            PY
            """);

        // Silently skipping a file is the failure mode; saying which ones were skipped is
        // what makes a bulk edit reviewable.
        Assert.Equal("hit: Gadget.cs,Widget.cs\nmiss: Sample.csproj\n", output);
    }

    [Fact]
    public async Task Pathlib_walks_the_tree_the_shell_wrote()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            mkdir -p /work/generated
            echo 'class A {}' > /work/generated/A.cs
            python3 - <<'PY'
            from pathlib import Path
            files = sorted(str(p) for p in Path('/work/generated').iterdir())
            print(files)
            print(Path('/work/generated/A.cs').read_text().strip())
            PY
            """);

        Assert.Equal("['/work/generated/A.cs']\nclass A {}\n", output);
    }

    [Fact]
    public async Task Io_open_is_the_builtin_under_its_other_name()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 - <<'PY'
            import io
            p = '/work/src/Widget.cs'
            s = io.open(p, encoding='utf-8').read()
            io.open(p, 'w', encoding='utf-8').write(s.replace('widget', 'sprocket'))
            print('sprocket' in io.open(p, encoding='utf-8').read())
            PY
            """);

        // `io.open(path, encoding='utf-8')` is the spelling a patch script reaches for when
        // it wants the encoding stated rather than guessed; without the module the script
        // dies at the import with the file untouched.
        Assert.Equal("True\n", output);
    }

    [Fact]
    public async Task Newline_and_the_other_ignorable_open_arguments_are_refused()
    {
        var session = await AgentSession.NewAsync();

        var result = await session.RunAsync("python3 -c \"open('/work/x', 'w', newline='')\"");

        // Nothing here translates line endings, so `newline=''` would in fact describe what
        // already happens — but accepting it would mean accepting a guarantee about newline
        // handling that this `open` does not make, and upstream's corpus pins the refusal.
        // A patch script that wants exact bytes opens the file in binary mode instead.
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(
            "TypeError: 'newline' argument is not yet supported",
            result.Stderr.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Python_edits_and_the_shell_verifies_in_one_script()
    {
        var session = await AgentSession.NewAsync();

        var output = await session.OutAsync("""
            python3 - <<'PY'
            p = '/work/docs/notes.md'
            s = open(p, encoding='utf-8').read()
            open(p, 'w', encoding='utf-8').write(s.replace('TODO: describe', 'DONE: described'))
            PY
            grep -c 'DONE' /work/docs/notes.md
            """);

        // The two halves share one filesystem, so the check that follows the edit sees it
        // — which is the whole reason a patch and its verification can be one script.
        Assert.Equal("1\n", output);
    }
}
