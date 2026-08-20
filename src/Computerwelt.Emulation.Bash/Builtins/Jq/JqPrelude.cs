namespace Computerwelt.Emulation.Bash.Builtins.Jq;

/// <summary>
/// The part of jq's standard library that is written in jq.
/// </summary>
/// <remarks>
/// <para>
/// jq ships most of its builtins as jq source, and doing the same here is not just
/// economy — it is the only way to keep the derived filters' <i>path</i> behaviour right.
/// <c>select</c>, <c>recurse</c>, <c>first</c> and <c>map_values</c> all have to work
/// inside <c>del(...)</c> and <c>|=</c>, and they do so automatically when they are
/// ordinary definitions the path evaluator can inline.
/// </para>
/// <para>
/// The definitions are parsed once and cached; a user's filter is spliced in where the
/// trailing <c>.</c> sits.
/// </para>
/// </remarks>
internal static class JqPrelude
{
    private const string Source = """
        def not: if . then false else true end;
        def select(f): if f then . else empty end;
        def recurse(f): def r: ., (f | r); r;
        def recurse(f; cond): def r: ., (f | select(cond) | r); r;
        def recurse: recurse(.[]?);
        def map(f): [.[] | f];
        def map_values(f): .[] |= f;
        def values: select(. != null);
        def nulls: select(type == "null");
        def booleans: select(type == "boolean");
        def numbers: select(type == "number");
        def strings: select(type == "string");
        def arrays: select(type == "array");
        def objects: select(type == "object");
        def iterables: select(type == "array" or type == "object");
        def scalars: select(type != "array" and type != "object");
        def add: reduce .[] as $x (null; . + $x);
        def add(f): reduce (.[] | f) as $x (null; . + $x);
        def any: reduce .[] as $x (false; . or $x);
        def all: reduce .[] as $x (true; . and $x);
        def any(f): reduce (.[] | f) as $x (false; . or $x);
        def all(f): reduce (.[] | f) as $x (true; . and $x);
        def any(g; f): reduce (g | f) as $x (false; . or $x);
        def all(g; f): reduce (g | f) as $x (true; . and $x);
        def to_entries: [keys_unsorted[] as $k | {key: $k, value: .[$k]}];
        def from_entries:
          reduce .[] as $e (
            {};
            . + {
              (($e | if type == "object" then (.key // .k // .name // .Key // .K // .Name) else . end)
                 | if type == "string" then . else tojson end):
              ($e | if type == "object" then (if has("value") then .value elif has("v") then .v else .Value end) else null end)
            }
          );
        def with_entries(f): to_entries | map(f) | from_entries;
        def del(f): delpaths([path(f)]);
        def paths: path(..) | select(length > 0);
        def paths(f): . as $in | paths | select(. as $p | $in | getpath($p) | f);
        def leaf_paths: paths(scalars);
        def flatten: flatten(1e9);
        def range($n): range(0; $n);
        def first: .[0];
        def last: .[-1];
        def nth($n): .[$n];
        def nth($n; f): last(limit($n + 1; f));
        def until(cond; update): def _u: if cond then . else (update | _u) end; _u;
        def while(cond; update): def _w: if cond then ., (update | _w) else empty end; _w;
        def repeat(f): def _r: ., (f | _r); _r;
        def unique: unique_by(.);
        def in(xs): . as $x | xs | has($x);
        def inside(xs): . as $x | xs | contains($x);
        def index($i): indices($i) | .[0];
        def rindex($i): indices($i) | .[-1:][0];
        def combinations: if length == 0 then [] else .[0][] as $x | [$x] + (.[1:] | combinations) end;
        def walk(f): def w: if type == "object" then map_values(w) elif type == "array" then map(w) else . end | f; w;
        def env: $ENV;
        def join($sep):
          reduce .[] as $item (
            null;
            (if . == null then "" else . + $sep end)
              + ($item | if . == null then "" elif type == "string" then . else tojson end)
          ) // "";
        def splits($re): splits($re; null);
        .
        """;

    private static readonly Lock Gate = new();
    private static JqNode? _parsed;

    /// <summary>Wraps <paramref name="filter"/> in the standard definitions.</summary>
    public static JqNode Wrap(JqNode filter)
    {
        JqNode prelude;

        lock (Gate)
        {
            prelude = _parsed ??= JqParser.Parse(Source);
        }

        return Splice(prelude, filter);
    }

    /// <summary>Replaces the trailing <c>.</c> of the definition chain with the user's filter.</summary>
    private static JqNode Splice(JqNode prelude, JqNode filter) =>
        prelude is JqFuncDef definition
            ? definition with { Rest = Splice(definition.Rest, filter) }
            : filter;
}
