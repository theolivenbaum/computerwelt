using Monty.Runtime;
using Xunit;

namespace Monty.Tests;

/// <summary>
/// The embedded Unicode database and the normalization built on it.
/// </summary>
/// <remarks>
/// The fixture corpus normalizes a handful of characters; these cover the parts of the
/// algorithm it does not reach — Hangul, multi-mark reordering, composition exclusions —
/// because the port implements normalization itself rather than borrowing the host's.
/// </remarks>
public sealed class UnicodeDataTests
{
    [Fact]
    public void The_database_is_the_release_it_reports()
    {
        Assert.Equal("16.0.0", UnicodeData.Version);
        Assert.Equal("Lu", UnicodeData.Category('A'));
        Assert.Equal("Cn", UnicodeData.Category(0xFFFF));
        Assert.Equal(230, UnicodeData.Combining(0x0301));
    }

    [Fact]
    public void Names_cover_the_listed_and_the_algorithmic()
    {
        Assert.Equal("LATIN CAPITAL LETTER A", UnicodeData.Name('A'));
        Assert.Equal("CJK UNIFIED IDEOGRAPH-4E00", UnicodeData.Name(0x4E00));
        Assert.Equal("HANGUL SYLLABLE GA", UnicodeData.Name(0xAC00));
        Assert.Equal("HANGUL SYLLABLE HIH", UnicodeData.Name(0xD7A3));
        Assert.Null(UnicodeData.Name(0xFFFF));

        Assert.Equal('A', UnicodeData.Lookup("LATIN CAPITAL LETTER A"));
        Assert.Equal(0x4E00, UnicodeData.Lookup("CJK UNIFIED IDEOGRAPH-4E00"));
        Assert.Equal(0xAC00, UnicodeData.Lookup("HANGUL SYLLABLE GA"));
        Assert.Null(UnicodeData.Lookup("NOT A CHARACTER NAME"));
    }

    [Theory]
    // A precomposed character round-trips through both forms.
    [InlineData("é", false, false, "é")]
    [InlineData("é", false, true, "é")]

    // Marks of different classes are reordered into class order, and only then composed.
    [InlineData("q̣̇", false, false, "q̣̇")]
    [InlineData("ḍ̇", false, true, "ḍ̇")]

    // A Hangul syllable decomposes and composes by arithmetic rather than by table.
    [InlineData("가", false, false, "가")]
    [InlineData("각", false, true, "각")]

    // Compatibility folds the ligature; the canonical forms leave it alone.
    [InlineData("ﬁ", true, false, "fi")]
    [InlineData("ﬁ", false, true, "ﬁ")]

    // A compatibility-only decomposition is invisible to the canonical forms and does not
    // recompose under NFKC, because its parts form no primary composite.
    [InlineData("\u0F77", false, true, "\u0F77")]
    [InlineData("\u0F77", true, true, "\u0FB2\u0F71\u0F80")]
    public void Normalization_follows_the_four_forms(string input, bool compatibility, bool compose, string expected) =>
        Assert.Equal(expected, UnicodeData.Normalize(input, compatibility, compose));

    [Fact]
    public void Normalization_leaves_plain_text_alone()
    {
        Assert.Equal(string.Empty, UnicodeData.Normalize(string.Empty, false, true));
        Assert.Equal("hello", UnicodeData.Normalize("hello", true, true));

        // A surrogate pair is one code point, not two, and survives as one.
        Assert.Equal("\U0001F600", UnicodeData.Normalize("\U0001F600", true, true));
    }
}
