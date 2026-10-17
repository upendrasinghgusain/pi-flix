namespace MediaTidy;

internal sealed record Options(
    string Source,
    string Destination,
    bool Apply,
    string? Target,
    TimeSpan Settle,
    long MinVideoBytes,
    string DataDirectory)
{
    public const string Usage = """
        MediaTidy: files finished downloads into the Jellyfin folder.

          MediaTidy.exe [--apply] [--source <dir>] [--dest <dir>] [<download path>]

          --apply           make the changes (default is a dry run that only prints the plan)
          --source <dir>    downloads folder (default C:\qBittorrent)
          --dest <dir>      Jellyfin media folder (default C:\Jellyfin Media)
          <download path>   file just one download, without waiting for it to settle
                            (for qBittorrent's "Run on torrent finished": "%F")
        """;

    public string LogFile => Path.Combine(DataDirectory, "mediatidy.log");

    public string LedgerFile => Path.Combine(DataDirectory, "processed.json");

    public static Options? Parse(string[] args)
    {
        var options = new Options(
            Source: @"C:\qBittorrent",
            Destination: @"C:\Jellyfin Media",
            Apply: false,
            Target: null,
            Settle: TimeSpan.FromMinutes(10),
            MinVideoBytes: 20 * 1024 * 1024,
            DataDirectory: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MediaTidy"));

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--apply":
                    options = options with { Apply = true };
                    break;
                case "--source" when i + 1 < args.Length:
                    options = options with { Source = Path.GetFullPath(args[++i]) };
                    break;
                case "--dest" when i + 1 < args.Length:
                    options = options with { Destination = Path.GetFullPath(args[++i]) };
                    break;
                case var path when !path.StartsWith("--") && options.Target is null:
                    options = options with { Target = Path.GetFullPath(path.TrimEnd('\\', '/')) };
                    break;
                default:
                    return null;
            }
        }

        return options;
    }
}
