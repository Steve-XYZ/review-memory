using ReviewMemory.Core.Diff;

namespace ReviewMemory.Core.Tests;

public sealed class PatchHunksTests
{
    [Fact]
    public void Null_patch_returns_empty()
    {
        Assert.Empty(PatchHunks.Parse(null));
    }

    [Fact]
    public void Blank_patch_returns_empty()
    {
        Assert.Empty(PatchHunks.Parse("   \n"));
    }

    [Fact]
    public void Single_hunk_parses_header_and_body()
    {
        const string patch = """
            @@ -12,7 +12,9 @@ namespace Lotto
             pending = Load();
            -Process(pending);
            +if (!IsProcessed(pending))
            +    Process(pending);
             Save();
            """;

        var hunks = PatchHunks.Parse(patch);

        var hunk = Assert.Single(hunks);
        Assert.Equal(12, hunk.OldStart);
        Assert.Equal(7, hunk.OldLines);
        Assert.Equal(12, hunk.NewStart);
        Assert.Equal(9, hunk.NewLines);
        Assert.Contains("-Process(pending);", hunk.Text);
        Assert.Contains("+    Process(pending);", hunk.Text);
    }

    [Fact]
    public void Multiple_hunks_split_correctly()
    {
        const string patch = """
            @@ -1,3 +1,4 @@
             a
            -b
            +B
            +C
             c
            @@ -50,2 +51,3 @@
             x
            +y
             z
            """;

        var hunks = PatchHunks.Parse(patch);

        Assert.Equal(2, hunks.Count);
        Assert.Equal(1, hunks[0].OldStart);
        Assert.Equal(50, hunks[1].OldStart);
        Assert.StartsWith("@@ -50,2 +51,3 @@", hunks[1].Text);
        Assert.EndsWith(" z", hunks[1].Text);
    }

    [Fact]
    public void Header_without_counts_defaults_to_one_line()
    {
        const string patch = "@@ -5 +5 @@\n-changed\n";

        var hunks = PatchHunks.Parse(patch);

        var hunk = Assert.Single(hunks);
        Assert.Equal(5, hunk.OldStart);
        Assert.Equal(1, hunk.OldLines);
        Assert.Equal(1, hunk.NewLines);
    }
}
