namespace Computerwelt.Benchmarks;

/// <summary>The category a <see cref="BenchCase"/> belongs to, as upstream groups them.</summary>
public enum BenchCategory
{
    Startup,
    Variables,
    Arithmetic,
    Control,
    Strings,
    Arrays,
    Pipes,
    Tools,
    Complex,
    Large,
    Subshell,
    Io,
}

/// <summary>One script, its category, and the output bash produces for it.</summary>
/// <param name="Name">Upstream's name for the case; the row label in every report.</param>
/// <param name="Category">The group the case is measured under.</param>
/// <param name="Description">What the case is exercising.</param>
/// <param name="Script">The script, verbatim.</param>
/// <param name="Expected">
/// The standard output bash produces, or <see langword="null"/> where upstream pins none
/// because the output is long enough that the case is timed rather than compared.
/// </param>
public sealed record BenchCase(
    string Name,
    BenchCategory Category,
    string Description,
    string Script,
    string? Expected);
