namespace MediaTidy;

internal enum PlanStatus { Ready, AlreadyFiled, Waiting, Unrecognised, Conflict, NothingToDo }

internal sealed record FileLink(string From, string To);

internal sealed record Plan(string Entry, PlanStatus Status, string Reason, IReadOnlyList<FileLink> Links)
{
    public static Plan Skip(string entry, PlanStatus status, string reason) => new(entry, status, reason, []);
}

/// <summary>Works out where each finished download belongs, without touching anything.</summary>
internal sealed class Planner(Options options, Ledger ledger, TimeProvider time)
{
    private static readonly HashSet<string> VideoExtensions =
        new([".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".webm"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> SubtitleExtensions =
        new([".srt", ".ass", ".ssa", ".sub", ".vtt"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> IncompleteExtensions =
        new([".!qb", ".!ut", ".part", ".crdownload"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Every download in the source folder, except qBittorrent's "incomplete" folder.</summary>
    public IEnumerable<Plan> PlanAll() =>
        Directory.EnumerateFileSystemEntries(options.Source)
            .Where(e => !Path.GetFileName(e).Equals("incomplete", StringComparison.OrdinalIgnoreCase))
            .Where(e => !Path.GetFileName(e).StartsWith('.'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(e => PlanEntry(e, settle: true));

    /// <param name="settle">Wait until files have been unchanged for a while. Off when qBittorrent
    /// itself reports the download as finished.</param>
    public Plan PlanEntry(string entry, bool settle)
    {
        if (ledger.IsFiled(entry))
        {
            return Plan.Skip(entry, PlanStatus.AlreadyFiled, "filed earlier");
        }

        List<string> files = Directory.Exists(entry)
            ? [.. Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories)]
            : File.Exists(entry) ? [entry] : [];

        if (files.Any(f => IncompleteExtensions.Contains(Path.GetExtension(f))))
        {
            return Plan.Skip(entry, PlanStatus.Waiting, "still downloading");
        }

        var videos = files.Where(IsMainVideo).ToList();
        if (videos.Count == 0)
        {
            return Plan.Skip(entry, PlanStatus.NothingToDo, "no video files");
        }

        if (settle && videos.Any(v => time.GetUtcNow() - File.GetLastWriteTimeUtc(v) < options.Settle))
        {
            return Plan.Skip(entry, PlanStatus.Waiting, $"changed in the last {options.Settle.TotalMinutes:0} minutes");
        }

        var subtitles = files.Where(f => SubtitleExtensions.Contains(Path.GetExtension(f))).ToList();
        var entryName = Path.GetFileName(entry);

        var episodes = videos
            .Select(v => (Video: v, Episode: ReleaseNameParser.ParseEpisode(Path.GetFileNameWithoutExtension(v))))
            .Where(x => x.Episode is not null)
            .ToList();

        List<FileLink> links = [];
        if (episodes.Count > 0)
        {
            foreach (var (video, episode) in episodes)
            {
                var show = Directory.Exists(entry) ? ReleaseNameParser.WithYearFrom(episode!.Show, entryName) : episode!.Show;
                var named = episode with { Show = show };
                var folder = Path.Combine(options.Destination, "shows", show, $"Season {named.Season:00}");
                links.Add(new FileLink(video, Path.Combine(folder, named.FileStem + Path.GetExtension(video))));
                var stem = Path.GetFileNameWithoutExtension(video);
                links.AddRange(SubtitleLinks(
                    subtitles.Where(s => Path.GetFileName(s).StartsWith(stem, StringComparison.OrdinalIgnoreCase)),
                    folder, named.FileStem, stem));
            }
        }
        else
        {
            var main = videos.MaxBy(v => new FileInfo(v).Length)!;
            var movie = ReleaseNameParser.ParseMovie(entryName)
                ?? ReleaseNameParser.ParseMovie(Path.GetFileNameWithoutExtension(main));
            if (movie is null)
            {
                return Plan.Skip(entry, PlanStatus.Unrecognised, "couldn't find a title and year in the name");
            }

            var folder = Path.Combine(options.Destination, "movies", movie.FolderName);
            links.Add(new FileLink(main, Path.Combine(folder, movie.FolderName + Path.GetExtension(main))));
            links.AddRange(SubtitleLinks(subtitles, folder, movie.FolderName, Path.GetFileNameWithoutExtension(main)));
        }

        var existing = links.FirstOrDefault(l => File.Exists(l.To));
        return existing is null
            ? new Plan(entry, PlanStatus.Ready, "", links)
            : Plan.Skip(entry, PlanStatus.Conflict, $"already exists: {Relative(existing.To)}");
    }

    public string Relative(string path) => Path.GetRelativePath(options.Destination, path);

    private bool IsMainVideo(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path))
        && new FileInfo(path).Length >= options.MinVideoBytes
        && !path.Split(Path.DirectorySeparatorChar)
            .Any(part => part.Equals("sample", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(part).EndsWith("-sample", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(part).EndsWith(".sample", StringComparison.OrdinalIgnoreCase));

    /// <summary>Keeps one English and one English SDH subtitle. A lone subtitle with no language
    /// tag is kept only when it is named after the video, as many downloads do.</summary>
    private static IEnumerable<FileLink> SubtitleLinks(IEnumerable<string> subtitles, string folder, string stem, string videoStem)
    {
        var tagged = subtitles
            .Select(s => (Path: s, Language: ReleaseNameParser.SubtitleLanguage(Path.GetFileNameWithoutExtension(s))))
            .ToList();

        var english = tagged.FirstOrDefault(t => t.Language == "en").Path;
        var sdh = tagged.FirstOrDefault(t => t.Language == "en.sdh").Path;

        if (english is not null)
        {
            yield return new FileLink(english, Path.Combine(folder, $"{stem}.en{Path.GetExtension(english)}"));
        }

        if (sdh is not null)
        {
            yield return new FileLink(sdh, Path.Combine(folder, $"{stem}.en.sdh{Path.GetExtension(sdh)}"));
        }

        if (english is null && sdh is null && tagged.Count == 1 && tagged[0].Language is null
            && Path.GetFileNameWithoutExtension(tagged[0].Path).Equals(videoStem, StringComparison.OrdinalIgnoreCase))
        {
            yield return new FileLink(tagged[0].Path, Path.Combine(folder, stem + Path.GetExtension(tagged[0].Path)));
        }
    }
}
