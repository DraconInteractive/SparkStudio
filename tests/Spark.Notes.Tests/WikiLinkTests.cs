using Spark.Api;

namespace Spark.Notes.Tests;

public class WikiLinkTests
{
    [Fact]
    public void Parses_all_parts()
    {
        var l = WikiLink.ParseInner("Specs/Watch#Battery|battery notes");
        Assert.Equal(new WikiLink("Specs/Watch", "Battery", "battery notes"), l);
        Assert.Equal("[[Specs/Watch#Battery|battery notes]]", l!.ToString());
    }

    [Fact]
    public void FindAll_skips_code()
    {
        var md = "[[A]] `[[B]]`\n```\n[[C]]\n```\n[[D]]";
        Assert.Equal(new[] { "A", "D" }, WikiLink.FindAll(md).Select(l => l.Target));
    }

    [Fact]
    public void Rewrite_keeps_heading_and_alias()
    {
        var md = "[[Old]] [[old#H|x]] [[Other]] `[[Old]]`";
        var result = WikiLink.RewriteTargets(md, t => t.Equals("old", StringComparison.OrdinalIgnoreCase), "New", out var n);
        Assert.Equal("[[New]] [[New#H|x]] [[Other]] `[[Old]]`", result);
        Assert.Equal(2, n);
    }

    private static SparkFile F(string dir, string name) => new()
    {
        Id = dir + "/" + name, Directory = dir, Name = name,
    };

    [Fact]
    public void Resolves_titles_paths_and_prefers_local_folder()
    {
        var index = new NoteIndex("/projects/SPARK", new[]
        {
            F("/projects/SPARK", "Plan.md"),
            F("/projects/SPARK/SPARK Watch", "Plan.md"),
            F("/projects/SPARK", "REMOTE_PLAN.txt"),
            F("/projects/SPARK/SPARK Watch", "Battery.md"),
        });

        Assert.Equal("/projects/SPARK/Plan.md", index.Resolve("plan")!.Id);
        Assert.Equal("/projects/SPARK/SPARK Watch/Plan.md", index.Resolve("Plan", "/projects/SPARK/SPARK Watch")!.Id);
        Assert.Equal("/projects/SPARK/SPARK Watch/Plan.md", index.Resolve("SPARK Watch/Plan")!.Id);
        Assert.Equal("/projects/SPARK/REMOTE_PLAN.txt", index.Resolve("REMOTE_PLAN.txt")!.Id);
        Assert.Null(index.Resolve("REMOTE_PLAN"));
        Assert.Null(index.Resolve("Missing"));

        Assert.Equal("Battery", index.LinkTextFor(index.Resolve("Battery")!));
        Assert.Equal("SPARK Watch/Plan", index.LinkTextFor(index.Resolve("SPARK Watch/Plan")!));
    }
}
