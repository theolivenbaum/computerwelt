using System.Globalization;
using System.Text;
using Bashkit.Parsing;

namespace Bashkit.Interpreter;

/// <summary>
/// Performs word expansion in POSIX order: brace, tilde, parameter, command substitution,
/// arithmetic, word splitting, pathname expansion, then quote removal.
/// </summary>
/// <remarks>
/// <para>
/// The order is not arbitrary and cannot be rearranged. Word splitting happens <i>after</i>
/// parameter expansion, which is why <c>x="a b"; f $x</c> passes two arguments while
/// <c>f "$x"</c> passes one; and pathname expansion happens after splitting, which is why
/// a variable holding <c>*</c> globs but a quoted one does not.
/// </para>
/// <para>
/// Expansion produces a <i>list</i> of fields, not a string. A single word can vanish
/// (<c>$empty</c> unquoted) or become many (<c>"${arr[@]}"</c>), and only a list-valued
/// result can express that.
/// </para>
/// </remarks>
public sealed class Expander
{
    private readonly ShellState _state;
    private readonly IFileSystem _fileSystem;
    private readonly ExecutionBudget _budget;
    private readonly Func<string, CancellationToken, ValueTask<ExecResult>> _runCommandSubstitution;

    /// <summary>Creates an expander bound to one execution.</summary>
    public Expander(
        ShellState state,
        IFileSystem fileSystem,
        ExecutionBudget budget,
        Func<string, CancellationToken, ValueTask<ExecResult>> runCommandSubstitution)
    {
        _state = state;
        _fileSystem = fileSystem;
        _budget = budget;
        _runCommandSubstitution = runCommandSubstitution;
    }

    /// <summary>Expands one word into zero or more fields.</summary>
    public async ValueTask<List<string>> ExpandAsync(Word word, CancellationToken cancellationToken = default)
    {
        var fields = await ExpandToFieldsAsync(word, splitting: true, cancellationToken);
        return await GlobFieldsAsync(fields, cancellationToken);
    }

    /// <summary>
    /// Expands a word to exactly one field, as assignments, redirection targets, <c>case</c>
    /// subjects and <c>[[ ]]</c> operands require. No splitting or globbing occurs.
    /// </summary>
    public async ValueTask<string> ExpandToStringAsync(Word word, CancellationToken cancellationToken = default)
    {
        var fields = await ExpandToFieldsAsync(word, splitting: false, cancellationToken);
        return fields.Count == 0 ? string.Empty : string.Concat(fields);
    }

    /// <summary>
    /// Expands a word but leaves glob metacharacters intact, for contexts such as
    /// <c>case</c> patterns and <c>${v#pat}</c> where the result is a pattern, not a path.
    /// </summary>
    public async ValueTask<string> ExpandToPatternAsync(Word word, CancellationToken cancellationToken = default)
    {
        var builder = new StringBuilder();

        foreach (var part in word.Parts)
        {
            var values = await ExpandPartAsync(part, splitting: false, cancellationToken);

            // A quoted part contributes literal text, so its metacharacters must be
            // escaped or `case $x in "*") ` would match everything.
            if (part.Quoted)
            {
                foreach (var value in values)
                {
                    builder.Append(EscapePattern(value));
                }
            }
            else
            {
                builder.Append(string.Concat(values));
            }
        }

        return builder.ToString();
    }

    /// <summary>Expands every word in a list, concatenating the resulting fields.</summary>
    public async ValueTask<List<string>> ExpandAllAsync(IReadOnlyList<Word> words, CancellationToken cancellationToken = default)
    {
        var result = new List<string>();
        foreach (var word in words)
        {
            result.AddRange(await ExpandAsync(word, cancellationToken));
        }

        return result;
    }

    private async ValueTask<List<string>> ExpandToFieldsAsync(Word word, bool splitting, CancellationToken cancellationToken)
    {
        _budget.ThrowIfExpired();

        // Brace expansion runs on the whole word's source text before anything else, so it
        // is applied to the reconstructed literal form and each result re-expanded.
        var braceExpanded = ExpandBraces(word);
        if (braceExpanded is not null)
        {
            var expanded = new List<string>();
            foreach (var text in braceExpanded)
            {
                expanded.AddRange(await ExpandToFieldsAsync(WordParser.Parse(text), splitting, cancellationToken));
            }

            return expanded;
        }

        // Fields accumulate as a list of (text, camePartlyFromQuotes) so that splitting can
        // tell `$x` from `"$x"` at the level of individual characters.
        var fields = new List<FieldBuilder> { new() };

        foreach (var part in word.Parts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (part is WordPart.Parameter { Name: "@" or "*" } splat && !splat.LengthOf)
            {
                await AppendSplatAsync(fields, splat, splitting, cancellationToken);
                continue;
            }

            var values = await ExpandPartAsync(part, splitting, cancellationToken);
            if (values.Count == 0)
            {
                continue;
            }

            var shouldSplit = splitting && !part.Quoted && PartIsSplittable(part);

            for (var i = 0; i < values.Count; i++)
            {
                if (i > 0)
                {
                    fields.Add(new FieldBuilder());
                }

                if (shouldSplit)
                {
                    AppendSplit(fields, values[i]);
                }
                else
                {
                    fields[^1].Append(values[i], quoted: part.Quoted);
                }
            }
        }

        var result = new List<string>(fields.Count);
        foreach (var field in fields)
        {
            // An unquoted expansion that produced nothing contributes no field at all;
            // a quoted one contributes an empty field.
            if (field.IsEmpty && !field.HasQuotedContent)
            {
                continue;
            }

            result.Add(field.ToString());
        }

        return result;
    }

    /// <summary>
    /// Only expansions are subject to word splitting; literal text in the script is not.
    /// <c>echo a b</c> is two words because the parser said so, not because of splitting.
    /// </summary>
    private static bool PartIsSplittable(WordPart part) =>
        part is WordPart.Parameter or WordPart.CommandSubstitution or WordPart.Arithmetic;

    private List<string>? ExpandBraces(Word word)
    {
        // Braces only expand when they appear unquoted in the word's literal text.
        var text = new StringBuilder();
        var sawBrace = false;

        foreach (var part in word.Parts)
        {
            if (part is not WordPart.Literal literal)
            {
                return null;
            }

            if (part.Quoted)
            {
                text.Append(EscapeBraces(literal.Text));
                continue;
            }

            if (literal.Text.Contains('{', StringComparison.Ordinal))
            {
                sawBrace = true;
            }

            text.Append(literal.Text);
        }

        if (!sawBrace)
        {
            return null;
        }

        var expansions = BraceExpander.Expand(text.ToString(), _budget.Limits);
        return expansions.Count > 1 ? expansions : null;
    }

    private static string EscapeBraces(string text) =>
        text.Replace("{", "\\{", StringComparison.Ordinal)
            .Replace("}", "\\}", StringComparison.Ordinal)
            .Replace(",", "\\,", StringComparison.Ordinal);

    private async ValueTask<List<string>> ExpandPartAsync(WordPart part, bool splitting, CancellationToken cancellationToken)
    {
        switch (part)
        {
            case WordPart.Literal literal:
                return [literal.Text];

            case WordPart.Tilde tilde:
                return [ExpandTilde(tilde.User)];

            case WordPart.Arithmetic arithmetic:
                return [ArithmeticEvaluator.Evaluate(_state, arithmetic.Expression).ToString(CultureInfo.InvariantCulture)];

            case WordPart.CommandSubstitution substitution:
            {
                var result = await _runCommandSubstitution(substitution.Script, cancellationToken);
                // Command substitution strips every trailing newline.
                return [result.Stdout.ToString().TrimEnd('\n')];
            }

            case WordPart.ProcessSubstitution:
                // Requires a real descriptor to hand to the command; not yet supported.
                return [string.Empty];

            case WordPart.Parameter parameter:
                return await ExpandParameterAsync(parameter, splitting, cancellationToken);

            default:
                return [];
        }
    }

    private string ExpandTilde(string user)
    {
        if (user.Length == 0)
        {
            return _state.Get("HOME") ?? "/root";
        }

        if (user == "+")
        {
            return _state.WorkingDirectory.Value;
        }

        if (user == "-")
        {
            return _state.Get("OLDPWD") ?? _state.WorkingDirectory.Value;
        }

        // There is no user database in the sandbox, so a named tilde stays literal.
        return "~" + user;
    }

    private async ValueTask AppendSplatAsync(List<FieldBuilder> fields, WordPart.Parameter parameter, bool splitting, CancellationToken cancellationToken)
    {
        var values = GetSplatValues(parameter);

        if (parameter.Name == "*")
        {
            var joined = string.Join(_state.FirstIfsCharacter(), values);

            if (parameter.Quoted || !splitting)
            {
                fields[^1].Append(joined, quoted: true);
            }
            else
            {
                AppendSplit(fields, joined);
            }

            return;
        }

        // `"$@"` produces one field per positional parameter, however they are quoted.
        for (var i = 0; i < values.Count; i++)
        {
            if (i > 0)
            {
                fields.Add(new FieldBuilder());
            }

            if (parameter.Quoted || !splitting)
            {
                fields[^1].Append(values[i], quoted: true);
            }
            else
            {
                AppendSplit(fields, values[i]);
            }
        }

        await ValueTask.CompletedTask;
    }

    private IReadOnlyList<string> GetSplatValues(WordPart.Parameter parameter)
    {
        if (parameter.Index is "@" or "*")
        {
            return _state.Lookup(parameter.Name)?.Elements ?? [];
        }

        return _state.Positional;
    }

    private async ValueTask<List<string>> ExpandParameterAsync(WordPart.Parameter parameter, bool splitting, CancellationToken cancellationToken)
    {
        // `${!prefix*}` lists variable names; `${!name}` is indirection.
        if (parameter.IndirectRef)
        {
            var target = _state.Get(parameter.Name);
            if (string.IsNullOrEmpty(target))
            {
                return [string.Empty];
            }

            return [_state.Get(target) ?? string.Empty];
        }

        // Array splats are handled by the caller, but `${arr[@]}` reaching here means it
        // was used in a single-field context.
        if (parameter.Index is "@" or "*")
        {
            var variable = _state.Lookup(parameter.Name);
            var elements = variable?.Elements ?? [];

            if (parameter.LengthOf)
            {
                return [elements.Count.ToString(CultureInfo.InvariantCulture)];
            }

            return parameter.Name is "@" or "*"
                ? [.. _state.Positional]
                : [.. elements];
        }

        var value = ResolveParameterValue(parameter, out var isSet);

        if (parameter.LengthOf)
        {
            if (parameter.Name is "@" or "*")
            {
                return [_state.Positional.Count.ToString(CultureInfo.InvariantCulture)];
            }

            var applied = await ApplyOperatorAsync(parameter, value, isSet, cancellationToken);
            return [applied.Length.ToString(CultureInfo.InvariantCulture)];
        }

        var result = await ApplyOperatorAsync(parameter, value, isSet, cancellationToken);

        if (!isSet && parameter.Operation == ParameterOp.None && _state.Options.NoUnset
            && !IsSpecialName(parameter.Name))
        {
            throw new BashkitException(BashkitErrorKind.Internal, $"{parameter.Name}: unbound variable");
        }

        return [result];
    }

    private static bool IsSpecialName(string name) =>
        name.Length == 1 && !char.IsAsciiLetter(name[0]) && name[0] != '_';

    private string ResolveParameterValue(WordPart.Parameter parameter, out bool isSet)
    {
        if (_state.GetSpecial(parameter.Name) is { } special)
        {
            isSet = true;
            return special;
        }

        var variable = _state.Lookup(parameter.Name);

        if (parameter.Index is { } subscript)
        {
            if (variable is null)
            {
                isSet = false;
                return string.Empty;
            }

            var key = variable.IsAssociative
                ? subscript
                : ArithmeticEvaluator.Evaluate(_state, subscript).ToString(CultureInfo.InvariantCulture);

            var element = variable.GetElement(key);
            isSet = element is not null;
            return element ?? string.Empty;
        }

        if (variable is null || variable.IsUnset)
        {
            isSet = false;
            return string.Empty;
        }

        isSet = true;
        return variable.Value;
    }

    private async ValueTask<string> ApplyOperatorAsync(WordPart.Parameter parameter, string value, bool isSet, CancellationToken cancellationToken)
    {
        var op = parameter.Operation;
        if (op == ParameterOp.None)
        {
            return value;
        }

        var argument = parameter.Argument;
        var isNullOrUnset = !isSet || value.Length == 0;

        switch (op)
        {
            case ParameterOp.UseDefault:
                return isNullOrUnset && argument is not null
                    ? await ExpandToStringAsync(argument, cancellationToken)
                    : value;

            case ParameterOp.UseDefaultUnsetOnly:
                return !isSet && argument is not null
                    ? await ExpandToStringAsync(argument, cancellationToken)
                    : value;

            case ParameterOp.AssignDefault or ParameterOp.AssignDefaultUnsetOnly:
            {
                var shouldAssign = op == ParameterOp.AssignDefault ? isNullOrUnset : !isSet;
                if (!shouldAssign)
                {
                    return value;
                }

                var replacement = argument is null ? string.Empty : await ExpandToStringAsync(argument, cancellationToken);
                _state.Set(parameter.Name, replacement);
                return replacement;
            }

            case ParameterOp.ErrorIfUnset or ParameterOp.ErrorIfUnsetOnly:
            {
                var shouldError = op == ParameterOp.ErrorIfUnset ? isNullOrUnset : !isSet;
                if (!shouldError)
                {
                    return value;
                }

                var message = argument is null || argument.Parts.Count == 0
                    ? "parameter null or not set"
                    : await ExpandToStringAsync(argument, cancellationToken);

                throw new BashkitException(BashkitErrorKind.Internal, $"{parameter.Name}: {message}");
            }

            case ParameterOp.UseAlternate:
                return isNullOrUnset
                    ? string.Empty
                    : argument is null ? string.Empty : await ExpandToStringAsync(argument, cancellationToken);

            case ParameterOp.UseAlternateSetOnly:
                return !isSet
                    ? string.Empty
                    : argument is null ? string.Empty : await ExpandToStringAsync(argument, cancellationToken);

            case ParameterOp.RemoveSmallestPrefix or ParameterOp.RemoveLargestPrefix:
            {
                var pattern = argument is null ? string.Empty : await ExpandToPatternAsync(argument, cancellationToken);
                var length = PatternMatcher.MatchPrefix(value, pattern, op == ParameterOp.RemoveLargestPrefix, extGlob: _state.Options.ExtGlob);
                return length > 0 ? value[length..] : value;
            }

            case ParameterOp.RemoveSmallestSuffix or ParameterOp.RemoveLargestSuffix:
            {
                var pattern = argument is null ? string.Empty : await ExpandToPatternAsync(argument, cancellationToken);
                var start = PatternMatcher.MatchSuffix(value, pattern, op == ParameterOp.RemoveLargestSuffix, extGlob: _state.Options.ExtGlob);
                return start >= 0 && start < value.Length ? value[..start] : value;
            }

            case ParameterOp.ReplaceFirst or ParameterOp.ReplaceAll
                or ParameterOp.ReplacePrefix or ParameterOp.ReplaceSuffix:
            {
                var pattern = argument is null ? string.Empty : await ExpandToPatternAsync(argument, cancellationToken);
                var replacement = parameter.Replacement is null
                    ? string.Empty
                    : await ExpandToStringAsync(parameter.Replacement, cancellationToken);
                return Replace(value, pattern, replacement, op);
            }

            case ParameterOp.Substring:
            {
                var spec = argument is null ? string.Empty : await ExpandToStringAsync(argument, cancellationToken);
                return Substring(value, spec);
            }

            case ParameterOp.UpperFirst or ParameterOp.UpperAll
                or ParameterOp.LowerFirst or ParameterOp.LowerAll:
            {
                var pattern = argument is null || argument.Parts.Count == 0
                    ? "?"
                    : await ExpandToPatternAsync(argument, cancellationToken);
                return ChangeCase(value, pattern, op);
            }

            case ParameterOp.Transform:
            {
                var spec = argument is null ? string.Empty : await ExpandToStringAsync(argument, cancellationToken);
                return Transform(value, spec);
            }

            default:
                return value;
        }
    }

    private string Replace(string value, string pattern, string replacement, ParameterOp op)
    {
        switch (op)
        {
            case ParameterOp.ReplacePrefix:
            {
                var length = PatternMatcher.MatchPrefix(value, pattern, longest: true, extGlob: _state.Options.ExtGlob);
                return length >= 0 ? replacement + value[length..] : value;
            }

            case ParameterOp.ReplaceSuffix:
            {
                var start = PatternMatcher.MatchSuffix(value, pattern, longest: true, extGlob: _state.Options.ExtGlob);
                return start >= 0 ? value[..start] + replacement : value;
            }
        }

        var builder = new StringBuilder();
        var position = 0;

        while (position <= value.Length)
        {
            var match = PatternMatcher.FindSubstring(value, pattern, position, extGlob: _state.Options.ExtGlob);
            if (match is not { } found)
            {
                break;
            }

            builder.Append(value, position, found.Start - position);
            builder.Append(replacement);

            // A zero-length match would loop forever, so always advance at least one char.
            var next = found.Start + Math.Max(found.Length, 1);
            if (found.Length == 0 && found.Start < value.Length)
            {
                builder.Append(value[found.Start]);
            }

            position = next;

            if (op == ParameterOp.ReplaceFirst)
            {
                break;
            }
        }

        if (position <= value.Length)
        {
            builder.Append(value, position, value.Length - position);
        }

        return builder.ToString();
    }

    private string Substring(string value, string spec)
    {
        var parts = SplitSubstringSpec(spec);
        var offset = (int)ArithmeticEvaluator.Evaluate(_state, parts.Offset);

        if (offset < 0)
        {
            offset = Math.Max(0, value.Length + offset);
        }

        if (offset >= value.Length)
        {
            return string.Empty;
        }

        if (parts.Length is null)
        {
            return value[offset..];
        }

        var length = (int)ArithmeticEvaluator.Evaluate(_state, parts.Length);

        // A negative length names an offset from the end rather than a count.
        if (length < 0)
        {
            var end = value.Length + length;
            return end <= offset ? string.Empty : value[offset..end];
        }

        return value[offset..Math.Min(value.Length, offset + length)];
    }

    private static (string Offset, string? Length) SplitSubstringSpec(string spec)
    {
        var depth = 0;
        for (var i = 0; i < spec.Length; i++)
        {
            switch (spec[i])
            {
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    depth--;
                    break;
                case ':' when depth == 0:
                    return (spec[..i], spec[(i + 1)..]);
            }
        }

        return (spec, null);
    }

    private string ChangeCase(string value, string pattern, ParameterOp op)
    {
        var toUpper = op is ParameterOp.UpperFirst or ParameterOp.UpperAll;
        var all = op is ParameterOp.UpperAll or ParameterOp.LowerAll;
        var builder = new StringBuilder(value.Length);
        var done = false;

        foreach (var c in value)
        {
            if (!done && PatternMatcher.IsMatch(c.ToString(), pattern, extGlob: _state.Options.ExtGlob))
            {
                builder.Append(toUpper ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c));
                if (!all)
                {
                    done = true;
                }

                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string Transform(string value, string spec) => spec switch
    {
        "Q" => Quote(value),
        "E" => WordParser.DecodeAnsiC(value),
        "U" => value.ToUpperInvariant(),
        "L" => value.ToLowerInvariant(),
        "u" => value.Length == 0 ? value : char.ToUpperInvariant(value[0]) + value[1..],
        "P" => value,
        _ => value,
    };

    /// <summary>Quotes a value so re-reading it by the shell yields the same string.</summary>
    internal static string Quote(string value)
    {
        if (value.Length == 0)
        {
            return "''";
        }

        if (value.All(static c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '/' or '-' or ':' or '=' or '@' or '+' or ','))
        {
            return value;
        }

        return "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
    }

    /// <summary>Splits <paramref name="text"/> on <c>IFS</c> and appends the fields.</summary>
    private void AppendSplit(List<FieldBuilder> fields, string text)
    {
        var ifs = _state.Ifs;

        if (ifs.Length == 0 || text.Length == 0)
        {
            fields[^1].Append(text, quoted: false);
            return;
        }

        var whitespace = ifs.Where(char.IsWhiteSpace).ToArray();
        var separators = ifs.Where(static c => !char.IsWhiteSpace(c)).ToArray();

        var position = 0;
        var first = true;

        // Leading and trailing IFS whitespace is discarded; a non-whitespace IFS character
        // always delimits, even when it produces an empty field.
        while (position < text.Length && whitespace.Contains(text[position]))
        {
            position++;
        }

        while (position < text.Length)
        {
            var start = position;
            while (position < text.Length && !ifs.Contains(text[position]))
            {
                position++;
            }

            if (!first)
            {
                fields.Add(new FieldBuilder());
            }

            fields[^1].Append(text[start..position], quoted: false);
            first = false;

            if (position >= text.Length)
            {
                break;
            }

            var sawSeparator = false;
            while (position < text.Length && ifs.Contains(text[position]))
            {
                if (separators.Contains(text[position]))
                {
                    if (sawSeparator)
                    {
                        // Two non-whitespace separators in a row delimit an empty field.
                        fields.Add(new FieldBuilder());
                        fields[^1].MarkQuoted();
                    }

                    sawSeparator = true;
                }

                position++;
            }
        }
    }

    /// <summary>Expands each field as a pathname pattern where it contains metacharacters.</summary>
    private async ValueTask<List<string>> GlobFieldsAsync(List<string> fields, CancellationToken cancellationToken)
    {
        if (_state.Options.NoGlob)
        {
            return fields;
        }

        var result = new List<string>(fields.Count);

        foreach (var field in fields)
        {
            if (!PatternMatcher.HasMetacharacters(field, _state.Options.ExtGlob))
            {
                result.Add(field);
                continue;
            }

            var matches = await Glob.ExpandAsync(_fileSystem, _state, field, _budget, cancellationToken);

            if (matches.Count == 0)
            {
                if (_state.Options.FailGlob)
                {
                    throw new BashkitException(BashkitErrorKind.Internal, $"no match: {field}");
                }

                if (!_state.Options.NullGlob)
                {
                    result.Add(field);
                }

                continue;
            }

            result.AddRange(matches);
        }

        return result;
    }

    private static string EscapePattern(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '*' or '?' or '[' or ']' or '\\')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// One field under construction. It tracks whether any quoted content contributed,
    /// which decides whether an empty result survives as an empty field or disappears.
    /// </summary>
    private sealed class FieldBuilder
    {
        private readonly StringBuilder _text = new();

        public bool HasQuotedContent { get; private set; }

        public bool IsEmpty => _text.Length == 0;

        public void Append(string value, bool quoted)
        {
            _text.Append(value);
            if (quoted)
            {
                HasQuotedContent = true;
            }
        }

        public void MarkQuoted() => HasQuotedContent = true;

        public override string ToString() => _text.ToString();
    }
}
