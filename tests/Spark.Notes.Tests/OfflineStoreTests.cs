using System.IO;
using Spark.Api;

namespace Spark.Notes.Tests;

public sealed class OfflineStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spark-offline-tests-" + Guid.NewGuid().ToString("N")[..8]);
    private OfflineStore NewStore() => new(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static SparkFile F(string id, string dir, string name = "Note.md") => new() { Id = id, Directory = dir, Name = name };
    private static Project P(string id, string dir) => new() { Id = id, Name = id, Directory = dir };

    [Fact]
    public void Content_round_trips_with_its_etag_and_survives_a_new_instance()
    {
        NewStore().SaveContent(F("a", "/p"), "# Title\n\nünïcödé [[Link]]\n", "\"a-1\"");

        var c = NewStore().LoadContent("a")!;
        Assert.Equal("# Title\n\nünïcödé [[Link]]\n", c.Text);
        Assert.Equal("\"a-1\"", c.ETag);
        Assert.Equal("/p", c.File.Directory);
        Assert.Null(NewStore().LoadContent("missing"));
    }

    [Fact]
    public void Saving_a_listing_drops_copies_of_files_deleted_on_the_server()
    {
        var s = NewStore();
        s.SaveContent(F("keep", "/p"), "1", "e1");
        s.SaveContent(F("gone", "/p/sub"), "2", "e2");
        s.SaveContent(F("other-root", "/q"), "3", "e3");

        s.SaveTree("/p", new() { "/p", "/p/sub" }, new() { F("keep", "/p") });

        Assert.True(s.HasContent("keep"));
        Assert.False(s.HasContent("gone"));
        Assert.True(s.HasContent("other-root")); // outside the listed root
        var tree = s.LoadTree("/p")!;
        Assert.Equal(new[] { "/p", "/p/sub" }, tree.Directories);
        Assert.Equal("keep", Assert.Single(tree.Files).Id);
    }

    [Fact]
    public void Pins_decide_which_files_are_wanted_and_unpinning_prunes_them()
    {
        var s = NewStore();
        s.SetPinned(P("watch", "/projects/SPARK/Watch"), true);
        Assert.True(s.IsPinned("watch"));
        Assert.True(s.IsWanted(F("x", "/projects/SPARK/Watch/Design")));
        Assert.False(s.IsWanted(F("y", "/projects/SPARK/Watcher"))); // prefix of the name, not a subfolder
        Assert.False(s.IsWanted(F("z", "/projects/SPARK")));

        s.SaveContent(F("x", "/projects/SPARK/Watch/Design"), "x", "ex");
        s.SaveContent(F("open", "/projects/SPARK/Watch"), "o", "eo");
        s.SetPinned(P("watch", "/projects/SPARK/Watch"), false);
        var removed = s.PruneUnpinned("/projects/SPARK/Watch", keepFileIds: new[] { "open" });

        Assert.Equal(1, removed);
        Assert.False(s.HasContent("x"));
        Assert.True(s.HasContent("open")); // the note on screen stays available
    }

    [Fact]
    public void Each_server_gets_its_own_folder()
    {
        var a = OfflineStore.ForServer(_dir, "https://spark.dracon.au");
        var b = OfflineStore.ForServer(_dir, "http://192.168.4.20:47693");
        Assert.NotEqual(a.Folder, b.Folder);
        a.SaveProjects(new[] { P("1", "/p") });
        Assert.NotNull(a.LoadProjects());
        Assert.Null(b.LoadProjects());
    }

    [Fact]
    public void A_damaged_cache_entry_reads_as_missing()
    {
        var s = NewStore();
        s.SaveContent(F("a", "/p"), "text", "e");
        File.WriteAllText(Path.Combine(s.Folder, "content", "a.json"), "{ not json");
        Assert.Null(s.LoadContent("a"));
        Assert.False(s.HasContent("b"));
    }
}
