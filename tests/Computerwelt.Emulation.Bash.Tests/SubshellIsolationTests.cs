using Xunit;

namespace Computerwelt.Emulation.Bash.Tests;

/// <summary>
/// What a subshell may and may not change in the shell that started it.
/// </summary>
/// <remarks>
/// A fork borrows its parent's variables rather than copying them, and copies one the
/// first time it touches it — so isolation is no longer a property of how the copy was
/// made but of every path that reads or writes. That makes it worth stating in cases: each
/// one below was checked against bash 5.2, and each is a way a write could leak outwards
/// if the borrowing were wrong.
/// </remarks>
public sealed class SubshellIsolationTests
{
    private static async Task<string> RunAsync(string script) =>
        (await Bash.CreateBuilder().WithWorkingDirectory("/").Build().ExecAsync(script)).Stdout.ToString();

    [Theory]
    // A plain assignment, in a subshell and in a substitution.
    [InlineData("x=outer; (x=inner); echo $x", "outer\n")]
    [InlineData("x=outer; y=$(x=inner; echo $x); echo $y-$x", "inner-outer\n")]
    [InlineData("x=1; echo $(x=2; echo $x)$x", "21\n")]
    // Unsetting, which has to hide the inherited variable without removing it outside.
    [InlineData("x=outer; (unset x); echo [$x]", "[outer]\n")]
    [InlineData("x=1; y=$(unset x; echo [${x-gone}]); echo $y-$x", "[gone]-1\n")]
    // Arrays and associative arrays, element by element.
    [InlineData("x=(a b c); (x[1]=z); echo ${x[@]}", "a b c\n")]
    [InlineData("x=(a b c); (unset 'x[1]'); echo ${x[@]}", "a b c\n")]
    [InlineData("x=(a); (x+=(b)); echo ${x[@]}", "a\n")]
    [InlineData("declare -A m; m[k]=v; (m[k]=z; m[n]=w); echo ${m[k]}-${m[n]}", "v-\n")]
    // Attributes, which travel with the variable.
    [InlineData("export E=1; (export E=2); echo $E", "1\n")]
    [InlineData("x=1; (readonly x); x=2; echo $x", "2\n")]
    [InlineData("declare -i n=5; (n+=5); echo $n", "5\n")]
    [InlineData("x=abc; (x+=def); echo $x", "abc\n")]
    // The shell's own variables are no different.
    [InlineData("PATH=/bin; (PATH=/other); echo $PATH", "/bin\n")]
    [InlineData("IFS=,; (IFS=:); a=\"1,2\"; set -- $a; echo $1-$2", "1-2\n")]
    // Nesting: each level sees the one outside it and none of the ones inside.
    [InlineData("x=1; (x=2; (x=3; echo $x); echo $x); echo $x", "3\n2\n1\n")]
    [InlineData("x=1; ( (x=2) ); echo $x", "1\n")]
    [InlineData("x=1; y=$( (x=2); echo $x ); echo $y-$x", "1-1\n")]
    // A function called in a subshell, and a local that outlives neither.
    [InlineData("x=1; f() { x=2; }; (f); echo $x", "1\n")]
    [InlineData("x=global; f() { local x=local; echo $(echo $x); }; f; echo $x", "local\nglobal\n")]
    [InlineData("f() { local n=$1; echo $n; }; f 5; echo [$n]", "5\n[]\n")]
    // A substitution in a loop, a condition and a case subject: the parent keeps its own.
    [InlineData("c=0; r=$(c=9; echo $c); echo $r-$c", "9-0\n")]
    [InlineData("x=1; for i in $(echo a b); do x=$i; done; echo $x", "b\n")]
    [InlineData("v=1; case $(v=2; echo x) in x) echo $v;; esac", "1\n")]
    // ...and the parent's own writes still work afterwards, which a borrowed variable
    // mutated in place would have broken.
    [InlineData("x=1; y=$(echo hi); x=2; echo $x-$y", "2-hi\n")]
    [InlineData("x=(a b); y=$(echo hi); x[0]=z; echo ${x[@]}", "z b\n")]
    [InlineData("f() { echo $((c++)); }; c=0; f; f; echo $c", "0\n1\n2\n")]
    public async Task ContainsWrites(string script, string expected) =>
        Assert.Equal(expected, await RunAsync(script));
}
