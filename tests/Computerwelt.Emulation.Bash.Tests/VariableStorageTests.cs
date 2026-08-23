using Xunit;

namespace Computerwelt.Emulation.Bash.Tests;

/// <summary>
/// The shapes a variable can hold, and what a subshell sees of them.
/// </summary>
/// <remarks>
/// A variable stores a lone scalar without allocating array storage, and promotes to the
/// full map when a second subscript arrives; a fork copies from that storage directly
/// rather than replaying the assignments. Both are invisible from a script — which is the
/// point, and is what these cases pin: the promotion boundary and the copy are where a
/// value would silently go missing, or be shared where it must not be.
/// </remarks>
public sealed class VariableStorageTests
{
    private static async Task<string> RunAsync(string script) =>
        (await Bash.CreateBuilder().WithWorkingDirectory("/").Build().ExecAsync(script)).Stdout.ToString();

    [Theory]
    // A scalar promoted by a second subscript keeps element 0.
    [InlineData("x=a; x[1]=b; echo ${x[0]}-${x[1]}", "a-b\n")]
    [InlineData("x=a; x[1]=b; echo ${#x[@]}", "2\n")]
    [InlineData("x=a; x[1]=b; echo $x", "a\n")]
    // An array that was assigned nothing has no elements.
    [InlineData("x=(); echo ${#x[@]}", "0\n")]
    [InlineData("x=(); x+=(a); echo ${x[0]}", "a\n")]
    // A negative subscript counts from the end, before and after promotion.
    [InlineData("x=a; x[-1]=z; echo ${x[0]}", "z\n")]
    [InlineData("x=(a b c); x[-1]=z; echo ${x[@]}", "a b z\n")]
    // Appending grows from the highest subscript, not from the count.
    [InlineData("x=(a); x[5]=f; x+=(g); echo ${x[6]}", "g\n")]
    // Unsetting the only element leaves an empty array, not a scalar.
    [InlineData("x=(a); unset 'x[0]'; echo ${#x[@]}", "0\n")]
    // Associative arrays are keyed independently of the indexed view.
    [InlineData("declare -A m; m[one]=1; m[two]=2; echo ${m[one]}${m[two]}", "12\n")]
    [InlineData("declare -A m; m[one]=1; echo ${#m[@]}", "1\n")]
    public async Task StoresValues(string script, string expected) =>
        Assert.Equal(expected, await RunAsync(script));

    [Theory]
    // A subshell's writes are its own, whichever shape the variable has.
    [InlineData("x=outer; (x=inner); echo $x", "outer\n")]
    [InlineData("x=(a b); (x[0]=z); echo ${x[0]}", "a\n")]
    [InlineData("declare -A m; m[k]=outer; (m[k]=inner); echo ${m[k]}", "outer\n")]
    [InlineData("x=(a b); y=$(x[1]=z; echo ${x[1]}); echo $y-${x[1]}", "z-b\n")]
    // ...and it starts from what the parent had, element for element.
    [InlineData("x=(a b c); echo $(echo ${x[@]})", "a b c\n")]
    [InlineData("declare -A m; m[k]=v; echo $(echo ${m[k]})", "v\n")]
    [InlineData("x=(a); x[9]=j; echo $(echo ${x[9]})", "j\n")]
    [InlineData("x=(); echo $(x+=(a); echo ${x[0]})-${#x[@]}", "a-0\n")]
    public async Task IsolatesSubshells(string script, string expected) =>
        Assert.Equal(expected, await RunAsync(script));
}
