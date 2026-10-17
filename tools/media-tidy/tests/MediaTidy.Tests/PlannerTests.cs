namespace MediaTidy.Tests;

public sealed class PlannerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mediatidy-" + Guid.NewGuid().ToString("N"));
    private readonly Options _options;

    public PlannerTests()
    {
        _options = new Options(
            Source: Path.Combine(_root, "downloads"),
            Destination: Path.Combine(_root, "media"),
            Apply: true,
            Target: null,
            Settle: TimeSpan.FromMinutes(10),
            MinVideoBytes: 100,
            DataDirectory: Path.Combine(_root, "data"));
        Directory.CreateDirectory(_options.Source);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Files_a_film_with_its_english_subtitles_and_drops_the_rest()
    {
        var entry = Download("Weapons (2025) [1080p] [WEBRip] [5.1] [YTS.MX]",
            ("Weapons.2025.1080p.WEBRip.x264.AAC5.1-[YTS.MX].mp4", 500),
            ("Weapons.2025.1080p.WEBRip.x264.AAC5.1-[YTS.MX].srt", 10),
            (@"Subs\English (SDH).eng.srt", 10),
            (@"Subs\Français (France).fre.srt", 10),
            ("www.YTS.MX.jpg", 10));

        var plan = Planner().PlanEntry(entry, settle: false);

        Assert.Equal(PlanStatus.Ready, plan.Status);
        Assert.Equal(
            [@"movies\Weapons (2025)\Weapons (2025).mp4", @"movies\Weapons (2025)\Weapons (2025).en.sdh.srt"],
            plan.Links.Select(l => Path.GetRelativePath(_options.Destination, l.To)));
    }

    [Fact]
    public void Files_a_loose_film_file()
    {
        var entry = Download("The Uprising 2026 1080p WEB-DL HEVC x265 5.1 BONE.mkv", 500);

        var plan = Planner().PlanEntry(entry, settle: false);

        Assert.Equal(@"movies\The Uprising (2026)\The Uprising (2026).mkv", Relative(Assert.Single(plan.Links)));
    }

    [Fact]
    public void Files_each_episode_of_a_season_pack_and_ignores_samples()
    {
        var entry = Download("Marvel Zombies 2025 Season 1 Complete 1080p WEB xc264 [i_c]",
            ("Marvel Zombies - S01E01 - Episode 1.mkv", 500),
            ("Marvel Zombies - S01E02 - Episode 2.mkv", 500),
            (@"Sample\Marvel Zombies - S01E01 - sample.mkv", 500));

        var plan = Planner().PlanEntry(entry, settle: false);

        Assert.Equal(
            [@"shows\Marvel Zombies (2025)\Season 01\Marvel Zombies (2025) S01E01.mkv",
             @"shows\Marvel Zombies (2025)\Season 01\Marvel Zombies (2025) S01E02.mkv"],
            plan.Links.Select(Relative));
    }

    [Fact]
    public void Waits_for_unfinished_and_recently_changed_downloads()
    {
        var unfinished = Download("Hope 2026 1080p", ("Hope 2026 1080p.mkv.!qB", 500));
        var fresh = Download("Fresh 2026 1080p.mkv", 500);

        Assert.Equal(PlanStatus.Waiting, Planner().PlanEntry(unfinished, settle: false).Status);
        Assert.Equal(PlanStatus.Waiting, Planner().PlanEntry(fresh, settle: true).Status);
        Assert.Equal(PlanStatus.Ready, Planner().PlanEntry(fresh, settle: false).Status);
    }

    [Fact]
    public void Leaves_names_it_cannot_understand_for_you()
    {
        var entry = Download("holiday-video.mp4", 500);

        Assert.Equal(PlanStatus.Unrecognised, Planner().PlanEntry(entry, settle: false).Status);
    }

    [Fact]
    public void Hard_links_the_file_and_never_files_it_twice()
    {
        var entry = Download("Hope 2026 1080p Korean WEB-DL HEVC x265 5.1 BONE.mkv", 500);
        var ledger = Ledger.Load(_options.LedgerFile);

        var plan = new Planner(_options, ledger, TimeProvider.System).PlanEntry(entry, settle: false);
        Linker.Apply(plan);
        ledger.MarkFiled(entry, DateTimeOffset.Now);
        ledger.Save();

        var filed = Path.Combine(_options.Destination, "movies", "Hope (2026)", "Hope (2026).mkv");
        Assert.True(File.Exists(entry), "the original stays for seeding");
        Assert.Equal(new FileInfo(entry).Length, new FileInfo(filed).Length);

        // Deleting the tidy copy (e.g. after watching) must not bring it back.
        File.Delete(filed);
        var reloaded = new Planner(_options, Ledger.Load(_options.LedgerFile), TimeProvider.System);
        Assert.Equal(PlanStatus.AlreadyFiled, reloaded.PlanEntry(entry, settle: false).Status);
    }

    private Planner Planner() => new(_options, Ledger.Load(_options.LedgerFile), TimeProvider.System);

    private string Relative(FileLink link) => Path.GetRelativePath(_options.Destination, link.To);

    private string Download(string file, int bytes)
    {
        var path = Path.Combine(_options.Source, file);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private string Download(string folder, params (string File, int Bytes)[] files)
    {
        var entry = Path.Combine(_options.Source, folder);
        foreach (var (file, bytes) in files)
        {
            var path = Path.Combine(entry, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, new byte[bytes]);
        }

        return entry;
    }
}
