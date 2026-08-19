using System.Globalization;
using System.Text;
using Bashkit.Interpreter;

namespace Bashkit.Builtins;

/// <summary>
/// <c>find</c> — walks a directory tree, testing each entry against an expression.
/// </summary>
/// <remarks>
/// <c>find</c>'s arguments are an expression, not a flag list, so it is parsed into a
/// predicate tree rather than scanned with <see cref="ArgCursor"/>. Tests are implicitly
/// joined with <c>-a</c>, and <c>-o</c> binds more loosely — parsing it any other way gets
/// <c>-name a -o -name b -print</c> wrong.
/// </remarks>
public sealed class FindBuiltin : IBuiltin
{
    /// <inheritdoc />
    public string Name => "find";

    /// <inheritdoc />
    public string? LlmHint =>
        "find: Walks directories. Supports -name -iname -path -type -size -maxdepth -mindepth -empty -not -o -print -delete -exec.";

    /// <inheritdoc />
    public async ValueTask<ExecResult> ExecuteAsync(BuiltinContext context, CancellationToken cancellationToken = default)
    {
        var arguments = context.Arguments;
        var roots = new List<string>();
        var index = 0;

        // Everything before the first operator is a starting point.
        while (index < arguments.Count && !IsExpressionStart(arguments[index]))
        {
            roots.Add(arguments[index++]);
        }

        if (roots.Count == 0)
        {
            roots.Add(".");
        }

        FindExpression expression;
        try
        {
            expression = FindExpressionParser.Parse(arguments, index);
        }
        catch (FormatException e)
        {
            return ExecResult.Usage("find", e.Message);
        }

        var builder = new StringBuilder();
        var errors = new StringBuilder();

        foreach (var root in roots)
        {
            var path = context.ResolvePath(root);

            if (!await context.FileSystem.ExistsAsync(path, cancellationToken))
            {
                errors.Append("find: '").Append(root).Append("': No such file or directory\n");
                continue;
            }

            await WalkAsync(context, path, root.TrimEnd('/'), 0, expression, builder, cancellationToken);
        }

        return errors.Length == 0
            ? ExecResult.Ok(builder.ToString())
            : new ExecResult
            {
                Stdout = StreamData.FromText(builder.ToString()),
                Stderr = StreamData.FromText(errors.ToString()),
                ExitCode = ExitCodes.Failure,
            };
    }

    private static bool IsExpressionStart(string argument) =>
        argument.StartsWith('-') || argument is "(" or ")" or "!";

    private static async ValueTask WalkAsync(
        BuiltinContext context,
        VPath path,
        string display,
        int depth,
        FindExpression expression,
        StringBuilder output,
        CancellationToken cancellationToken)
    {
        context.Budget.ChargeWork(1);

        FileMetadata metadata;
        try
        {
            metadata = await context.FileSystem.StatLinkAsync(path, cancellationToken);
        }
        catch (FileSystemException)
        {
            return;
        }

        var entry = new FindEntry(path, display, depth, metadata, context);

        if (depth >= expression.MinDepth && await expression.MatchesAsync(entry, cancellationToken))
        {
            await expression.ActAsync(entry, output, cancellationToken);
        }

        if (!metadata.IsDirectory || depth >= expression.MaxDepth)
        {
            return;
        }

        IReadOnlyList<DirectoryEntry> children;
        try
        {
            children = await context.FileSystem.ReadDirectoryAsync(path, cancellationToken);
        }
        catch (FileSystemException)
        {
            return;
        }

        foreach (var child in children)
        {
            await WalkAsync(
                context,
                path.Join(child.Name),
                display + "/" + child.Name,
                depth + 1,
                expression,
                output,
                cancellationToken);
        }
    }

    /// <summary>One entry being tested, with everything the predicates need.</summary>
    internal sealed record FindEntry(VPath Path, string Display, int Depth, FileMetadata Metadata, BuiltinContext Context);

    /// <summary>A parsed <c>find</c> expression: a predicate plus the actions to take.</summary>
    internal sealed class FindExpression
    {
        public required Predicate Root { get; set; }

        public int MaxDepth { get; set; } = int.MaxValue;

        public int MinDepth { get; set; }

        public List<FindAction> Actions { get; } = [];

        public ValueTask<bool> MatchesAsync(FindEntry entry, CancellationToken cancellationToken) =>
            Root.EvaluateAsync(entry, cancellationToken);

        public async ValueTask ActAsync(FindEntry entry, StringBuilder output, CancellationToken cancellationToken)
        {
            // With no explicit action, `find` prints — which is why bare `find .` works.
            if (Actions.Count == 0)
            {
                output.Append(entry.Display).Append('\n');
                return;
            }

            foreach (var action in Actions)
            {
                await action.RunAsync(entry, output, cancellationToken);
            }
        }
    }

    internal abstract class Predicate
    {
        public abstract ValueTask<bool> EvaluateAsync(FindEntry entry, CancellationToken cancellationToken);

        public sealed class True : Predicate
        {
            public override ValueTask<bool> EvaluateAsync(FindEntry entry, CancellationToken cancellationToken) =>
                ValueTask.FromResult(true);
        }

        public sealed class Not(Predicate inner) : Predicate
        {
            public override async ValueTask<bool> EvaluateAsync(FindEntry entry, CancellationToken cancellationToken) =>
                !await inner.EvaluateAsync(entry, cancellationToken);
        }

        public sealed class And(Predicate left, Predicate right) : Predicate
        {
            public override async ValueTask<bool> EvaluateAsync(FindEntry entry, CancellationToken cancellationToken) =>
                await left.EvaluateAsync(entry, cancellationToken)
                && await right.EvaluateAsync(entry, cancellationToken);
        }

        public sealed class Or(Predicate left, Predicate right) : Predicate
        {
            public override async ValueTask<bool> EvaluateAsync(FindEntry entry, CancellationToken cancellationToken) =>
                await left.EvaluateAsync(entry, cancellationToken)
                || await right.EvaluateAsync(entry, cancellationToken);
        }

        public sealed class Test(Func<FindEntry, CancellationToken, ValueTask<bool>> test) : Predicate
        {
            public override ValueTask<bool> EvaluateAsync(FindEntry entry, CancellationToken cancellationToken) =>
                test(entry, cancellationToken);
        }
    }

    internal abstract class FindAction
    {
        public abstract ValueTask RunAsync(FindEntry entry, StringBuilder output, CancellationToken cancellationToken);

        public sealed class Print(char terminator = '\n') : FindAction
        {
            public override ValueTask RunAsync(FindEntry entry, StringBuilder output, CancellationToken cancellationToken)
            {
                output.Append(entry.Display).Append(terminator);
                return ValueTask.CompletedTask;
            }
        }

        public sealed class Delete : FindAction
        {
            public override async ValueTask RunAsync(FindEntry entry, StringBuilder output, CancellationToken cancellationToken)
            {
                try
                {
                    await entry.Context.FileSystem.RemoveAsync(entry.Path, recursive: false, cancellationToken);
                }
                catch (FileSystemException)
                {
                    // `find -delete` reports nothing for a directory it cannot empty.
                }
            }
        }

        /// <summary>
        /// <c>-exec cmd {} \;</c> — runs a command per match. It dispatches through the
        /// shell hooks, so the command is still a registered builtin and no process is
        /// created.
        /// </summary>
        public sealed class Exec(IReadOnlyList<string> template) : FindAction
        {
            public override async ValueTask RunAsync(FindEntry entry, StringBuilder output, CancellationToken cancellationToken)
            {
                var words = template
                    .Select(word => word.Replace("{}", entry.Display, StringComparison.Ordinal))
                    .ToList();

                var result = await entry.Context.Hooks.RunCommand(words, null, cancellationToken);
                output.Append(result.Stdout.ToString());
            }
        }
    }

    /// <summary>
    /// Parses a find expression into a predicate tree and an action list.
    /// </summary>
    /// <remarks>
    /// The position is carried in a mutable cursor rather than a <c>ref int</c> because
    /// several predicates capture state in lambdas, which C# forbids around by-ref
    /// parameters.
    /// </remarks>
    private static class FindExpressionParser
    {
        private sealed class Cursor(IReadOnlyList<string> arguments, int index)
        {
            public IReadOnlyList<string> Arguments { get; } = arguments;

            public int Index { get; set; } = index;

            public bool AtEnd => Index >= Arguments.Count;

            public string? Current => Index < Arguments.Count ? Arguments[Index] : null;

            public string Take() => Arguments[Index++];
        }

        public static FindExpression Parse(IReadOnlyList<string> arguments, int index)
        {
            var cursor = new Cursor(arguments, index);
            var expression = new FindExpression { Root = new Predicate.True() };
            expression.Root = ParseOr(cursor, expression);
            return expression;
        }

        private static Predicate ParseOr(Cursor cursor, FindExpression expression)
        {
            var left = ParseAnd(cursor, expression);

            while (cursor.Current is "-o" or "-or")
            {
                cursor.Index++;
                left = new Predicate.Or(left, ParseAnd(cursor, expression));
            }

            return left;
        }

        private static Predicate ParseAnd(Cursor cursor, FindExpression expression)
        {
            var left = ParseUnary(cursor, expression);

            while (!cursor.AtEnd && cursor.Current is not (")" or "-o" or "-or"))
            {
                if (cursor.Current is "-a" or "-and")
                {
                    cursor.Index++;
                }

                if (cursor.AtEnd || cursor.Current is ")" or "-o" or "-or")
                {
                    break;
                }

                left = new Predicate.And(left, ParseUnary(cursor, expression));
            }

            return left;
        }

        private static Predicate ParseUnary(Cursor cursor, FindExpression expression)
        {
            if (cursor.AtEnd)
            {
                return new Predicate.True();
            }

            var token = cursor.Current!;

            if (token is "!" or "-not")
            {
                cursor.Index++;
                return new Predicate.Not(ParseUnary(cursor, expression));
            }

            if (token == "(")
            {
                cursor.Index++;
                var inner = ParseOr(cursor, expression);
                if (cursor.Current == ")")
                {
                    cursor.Index++;
                }

                return inner;
            }

            return ParsePrimary(cursor, expression);
        }

        private static Predicate ParsePrimary(Cursor cursor, FindExpression expression)
        {
            var token = cursor.Take();

            string Value() => cursor.AtEnd
                ? throw new FormatException($"missing argument to `{token}'")
                : cursor.Take();

            switch (token)
            {
                case "-name":
                {
                    var pattern = Value();
                    return new Predicate.Test((entry, _) =>
                        ValueTask.FromResult(PatternMatcher.IsMatch(entry.Path.FileName, pattern)));
                }

                case "-iname":
                {
                    var pattern = Value();
                    return new Predicate.Test((entry, _) =>
                        ValueTask.FromResult(PatternMatcher.IsMatch(entry.Path.FileName, pattern, caseInsensitive: true)));
                }

                case "-path" or "-wholename":
                {
                    var pattern = Value();
                    return new Predicate.Test((entry, _) =>
                        ValueTask.FromResult(PatternMatcher.IsMatch(entry.Display, pattern)));
                }

                case "-type":
                {
                    var kind = Value();
                    return new Predicate.Test((entry, _) => ValueTask.FromResult(kind switch
                    {
                        "f" => entry.Metadata.IsFile,
                        "d" => entry.Metadata.IsDirectory,
                        "l" => entry.Metadata.IsSymlink,
                        "p" => entry.Metadata.IsFifo,
                        _ => false,
                    }));
                }

                case "-empty":
                    return new Predicate.Test(async (entry, cancellationToken) =>
                    {
                        if (entry.Metadata.IsFile)
                        {
                            return entry.Metadata.Size == 0;
                        }

                        if (!entry.Metadata.IsDirectory)
                        {
                            return false;
                        }

                        return (await entry.Context.FileSystem.ReadDirectoryAsync(entry.Path, cancellationToken)).Count == 0;
                    });

                case "-size":
                {
                    var spec = Value();
                    return new Predicate.Test((entry, _) => ValueTask.FromResult(MatchesSize(entry.Metadata.Size, spec)));
                }

                case "-maxdepth":
                    expression.MaxDepth = int.TryParse(Value(), CultureInfo.InvariantCulture, out var max) ? max : int.MaxValue;
                    return new Predicate.True();

                case "-mindepth":
                    expression.MinDepth = int.TryParse(Value(), CultureInfo.InvariantCulture, out var min) ? min : 0;
                    return new Predicate.True();

                case "-print":
                    expression.Actions.Add(new FindAction.Print());
                    return new Predicate.True();

                case "-print0":
                    expression.Actions.Add(new FindAction.Print('\0'));
                    return new Predicate.True();

                case "-delete":
                    expression.Actions.Add(new FindAction.Delete());
                    return new Predicate.True();

                case "-exec":
                {
                    var template = new List<string>();
                    while (!cursor.AtEnd && cursor.Current is not (";" or "+"))
                    {
                        template.Add(cursor.Take());
                    }

                    if (!cursor.AtEnd)
                    {
                        cursor.Index++;
                    }

                    expression.Actions.Add(new FindAction.Exec(template));
                    return new Predicate.True();
                }

                case "-newer" or "-anewer" or "-cnewer":
                {
                    var reference = Value();
                    return new Predicate.Test(async (entry, cancellationToken) =>
                    {
                        try
                        {
                            var other = await entry.Context.FileSystem.StatAsync(
                                entry.Context.ResolvePath(reference), cancellationToken);
                            return entry.Metadata.ModifiedAt > other.ModifiedAt;
                        }
                        catch (FileSystemException)
                        {
                            return false;
                        }
                    });
                }

                // Options that only affect traversal details this walker does not model.
                case "-follow" or "-nowarn" or "-depth" or "-xdev" or "-mount":
                    return new Predicate.True();

                case "-perm" or "-user" or "-group" or "-mtime" or "-atime" or "-ctime" or "-regex" or "-prune":
                    Value();
                    return new Predicate.True();

                default:
                    throw new FormatException($"unknown predicate `{token}'");
            }
        }

        /// <summary>Matches a <c>-size</c> spec such as <c>+1k</c>, <c>-2M</c> or <c>10c</c>.</summary>
        private static bool MatchesSize(long size, string spec)
        {
            if (spec.Length == 0)
            {
                return false;
            }

            var comparison = spec[0] is '+' or '-' ? spec[0] : '=';
            var body = comparison == '=' ? spec : spec[1..];

            var unit = body.Length > 0 && char.IsLetter(body[^1]) ? body[^1] : 'b';
            if (char.IsLetter(unit))
            {
                body = body[..^1];
            }

            if (!long.TryParse(body, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            var multiplier = char.ToLowerInvariant(unit) switch
            {
                'c' => 1L,
                'w' => 2L,
                'k' => 1024L,
                'm' => 1024L * 1024,
                'g' => 1024L * 1024 * 1024,
                _ => 512L,
            };

            // Sizes in blocks round up, which is why `-size 1` matches a one-byte file.
            var scaled = multiplier == 1 ? size : (size + multiplier - 1) / multiplier;
            var target = value;

            return comparison switch
            {
                '+' => scaled > target,
                '-' => scaled < target,
                _ => scaled == target,
            };
        }
    }
}

