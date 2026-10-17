namespace MediaTidy.Tests;

public class ReleaseNameParserTests
{
    [Theory]
    [InlineData("Hope 2026 1080p Korean WEB-DL HEVC x265 5.1 BONE", "Hope", 2026)]
    [InlineData("The Uprising 2026 1080p WEB-DL HEVC x265 5.1 BONE", "The Uprising", 2026)]
    [InlineData("Demolition 2015 1080p WEB-DL x264 AC3-JYK", "Demolition", 2015)]
    [InlineData("Monsters.Inc.2001.PROPER.1080p.BluRay.H264.AAC-LAMA[TGx]", "Monsters Inc", 2001)]
    [InlineData("Weapons (2025) [1080p] [WEBRip] [5.1] [YTS.MX]", "Weapons", 2025)]
    [InlineData("Die Welle aka The Wave (2008) - 1080p x265 HEVC + 1080p x264 - GER (ENG SUBS) [BRSHNKV]", "Die Welle", 2008)]
    [InlineData("www.UIndex.org    -    Shelter 2026 1080p AMZN WEB-DL DDP5 1 H 264-KyoGo", "Shelter", 2026)]
    [InlineData("Spider-Man- Brand New Day 2026.1080p.HQ Pre.Multi.AAC 2.0.x264", "Spider-Man- Brand New Day", 2026)]
    [InlineData("Winter.Sleep.2014.1080p.BluRay.x264.[ExYu - SRB]", "Winter Sleep", 2014)]
    [InlineData("2001.A.Space.Odyssey.1968.1080p.BluRay", "2001 A Space Odyssey", 1968)]
    [InlineData("blade.runner.2049.2017.2160p.web", "Blade Runner 2049", 2017)]
    public void Finds_a_films_title_and_year(string name, string title, int year)
    {
        Assert.Equal(new MovieName(title, year), ReleaseNameParser.ParseMovie(name));
    }

    [Theory]
    [InlineData("Some.Random.Video.1080p.x264")]
    [InlineData("2026")]
    public void Gives_up_without_a_year(string name)
    {
        Assert.Null(ReleaseNameParser.ParseMovie(name));
    }

    [Theory]
    [InlineData("MobLand S02E05 Birdland 720p AMZN WEB-DL DDP5 1 H 264-RAWR[EZTVx.to]", "MobLand", 2, 5)]
    [InlineData("mobland.s02e01.1080p.web.h264-poke[EZTVx.to]", "Mobland", 2, 1)]
    [InlineData("House.of.the.Dragon.S03E01.1080p.HEVC.x265-MeGusta[EZTVx.to]", "House of the Dragon", 3, 1)]
    [InlineData("Marvel Zombies - S01E04 - Episode 4", "Marvel Zombies", 1, 4)]
    [InlineData("Doctor.Who.2023.S01E02.1080p", "Doctor Who (2023)", 1, 2)]
    public void Finds_a_shows_season_and_episode(string name, string show, int season, int episode)
    {
        Assert.Equal(new EpisodeName(show, season, episode), ReleaseNameParser.ParseEpisode(name));
    }

    [Fact]
    public void A_film_is_not_an_episode()
    {
        Assert.Null(ReleaseNameParser.ParseEpisode("Weapons.2025.1080p.WEBRip.x264.AAC5.1-[YTS.MX]"));
    }

    [Theory]
    [InlineData("Marvel Zombies", "Marvel Zombies 2025 Season 1 Complete 1080p WEB xc264 [i_c]", "Marvel Zombies (2025)")]
    [InlineData("When They See Us", "When.They.See.Us.S01.COMPLETE.1080p.WEB.X264-AMRAP[TGx]", "When They See Us")]
    [InlineData("Doctor Who (2023)", "Doctor Who 2005 Pack", "Doctor Who (2023)")]
    public void Takes_a_shows_year_from_its_folder(string show, string folder, string expected)
    {
        Assert.Equal(expected, ReleaseNameParser.WithYearFrom(show, folder));
    }

    [Theory]
    [InlineData("English", "en")]
    [InlineData("2_English", "en")]
    [InlineData("English (SDH).eng", "en.sdh")]
    [InlineData("Film.2020.en", "en")]
    [InlineData("Français (France).fre", "other")]
    [InlineData("中文（繁體）.chi", "other")]
    [InlineData("Toy.Story.1995.1080p.BRrip.x264.YIFY", null)]
    public void Recognises_subtitle_languages(string stem, string? expected)
    {
        Assert.Equal(expected, ReleaseNameParser.SubtitleLanguage(stem));
    }
}
