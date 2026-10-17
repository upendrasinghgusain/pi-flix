using System.Globalization;
using System.Text.RegularExpressions;

namespace MediaTidy;

internal sealed record MovieName(string Title, int Year)
{
    public string FolderName => $"{Title} ({Year})";
}

internal sealed record EpisodeName(string Show, int Season, int Episode)
{
    public string FileStem => $"{Show} S{Season:00}E{Episode:00}";
}

/// <summary>Turns release-style names into the names Jellyfin expects.</summary>
internal static partial class ReleaseNameParser
{
    public static MovieName? ParseMovie(string name)
    {
        var clean = Clean(name);
        foreach (Match year in YearPattern().Matches(clean))
        {
            // A year at the very start, or one still in the future, is part of the title
            // (e.g. "2001 A Space Odyssey 1968", "Blade Runner 2049 2017").
            if (year.Index == 0 || int.Parse(year.Value, CultureInfo.InvariantCulture) > DateTime.Today.Year + 1)
            {
                continue;
            }

            var title = TrimTitle(clean[..year.Index]);
            var aka = title.IndexOf(" aka ", StringComparison.OrdinalIgnoreCase);
            if (aka > 0)
            {
                title = TrimTitle(title[..aka]);
            }

            if (title.Length > 0)
            {
                return new MovieName(TitleCase(title), int.Parse(year.Value, CultureInfo.InvariantCulture));
            }
        }

        return null;
    }

    public static EpisodeName? ParseEpisode(string name)
    {
        var clean = Clean(name);
        var match = EpisodePattern().Match(clean);
        if (!match.Success)
        {
            return null;
        }

        var show = TrimTitle(clean[..match.Index]);
        if (show.Length == 0)
        {
            return null;
        }

        var year = TrailingYearPattern().Match(show);
        if (year.Success)
        {
            show = $"{TrimTitle(show[..year.Index])} ({year.Groups[1].Value})";
        }

        return new EpisodeName(
            TitleCase(show),
            int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>Adds a year from the download's folder name when the episode file lacks one,
    /// e.g. "Marvel Zombies 2025 Season 1 Complete" gives "Marvel Zombies (2025)".</summary>
    public static string WithYearFrom(string show, string folderName)
    {
        if (show.EndsWith(')'))
        {
            return show;
        }

        var clean = Clean(folderName);
        if (!clean.StartsWith(show + " ", StringComparison.OrdinalIgnoreCase))
        {
            return show;
        }

        var year = LeadingYearPattern().Match(clean[(show.Length + 1)..]);
        return year.Success ? $"{show} ({year.Groups[1].Value})" : show;
    }

    /// <summary>"en", "en.sdh" for English subtitles, "other" for another language, null if untagged.</summary>
    public static string? SubtitleLanguage(string fileStem)
    {
        var words = WordSplitter().Split(fileStem.ToLowerInvariant());
        if (words.Any(w => w is "english" or "eng" or "en"))
        {
            return words.Any(w => w is "sdh" or "cc" or "hi") ? "en.sdh" : "en";
        }

        return words.Any(OtherLanguages.Contains) ? "other" : null;
    }

    private static readonly HashSet<string> OtherLanguages =
    [
        "ara", "bul", "chi", "chs", "cht", "cze", "dan", "dut", "est", "fin", "fre", "fra", "ger", "deu", "gre",
        "heb", "hun", "ind", "ita", "jpn", "kor", "lit", "nor", "pol", "por", "rum", "rus", "spa", "swe", "tha",
        "tur", "ukr", "vie",
    ];

    private static string Clean(string name)
    {
        var text = SitePrefixPattern().Replace(name, "");
        text = BracketedPattern().Replace(text, " ");
        text = text.Replace('.', ' ').Replace('_', ' ').Replace('(', ' ').Replace(')', ' ');
        return SpacesPattern().Replace(text, " ").Trim();
    }

    private static string TrimTitle(string title) => title.Trim(' ', '-');

    private static string TitleCase(string title) =>
        title == title.ToLowerInvariant() ? CultureInfo.GetCultureInfo("en-GB").TextInfo.ToTitleCase(title) : title;

    [GeneratedRegex(@"(?<!\d)(?:19|20)\d{2}(?![\dpx])", RegexOptions.IgnoreCase)]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"\s((?:19|20)\d{2})$")]
    private static partial Regex TrailingYearPattern();

    [GeneratedRegex(@"^((?:19|20)\d{2})(?!\d)")]
    private static partial Regex LeadingYearPattern();

    [GeneratedRegex(@"(?<![a-z\d])s(\d{1,2})\s?e(\d{1,3})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodePattern();

    // Download-site prefixes such as "www.UIndex.org    -    ".
    [GeneratedRegex(@"^\s*(?:www\.)?[a-z0-9-]+\.(?:org|com|net|to|mx|me|io|cc)\s*-\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SitePrefixPattern();

    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex BracketedPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesPattern();

    [GeneratedRegex(@"[^\p{L}\d]+")]
    private static partial Regex WordSplitter();
}
