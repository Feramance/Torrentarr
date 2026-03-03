using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Torrentarr.Core.Configuration;
using Torrentarr.Core.Services;
using Torrentarr.Infrastructure.Database;
using Torrentarr.Infrastructure.Services;
using Torrentarr.Infrastructure.ApiClients.Arr;
using Xunit;

namespace Torrentarr.Infrastructure.Tests.Services;

public class ArrMediaServiceTests
{
    private static ArrMediaService CreateService(TorrentarrConfig? config = null)
    {
        var options = new DbContextOptionsBuilder<TorrentarrDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var dbContext = new TorrentarrDbContext(options);
        var cfg = config ?? new TorrentarrConfig();
        
        var mockSearchExecutor = new Mock<ISearchExecutor>();
        mockSearchExecutor.Setup(x => x.ExecuteSearchesAsync(It.IsAny<string>(), It.IsAny<IEnumerable<SearchCandidate>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SearchResult());
        mockSearchExecutor.Setup(x => x.GetActiveCommandCountAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        mockSearchExecutor.Setup(x => x.CanSearch(It.IsAny<int>(), It.IsAny<int>()))
            .Returns(true);
        
        var mockSyncService = new Mock<ArrSyncService>(
            NullLogger<ArrSyncService>.Instance,
            cfg,
            dbContext);
        
        return new ArrMediaService(
            NullLogger<ArrMediaService>.Instance,
            dbContext,
            cfg,
            mockSearchExecutor.Object,
            mockSyncService.Object);
    }

    private static TorrentarrConfig CreateConfigWithRadarr(
        bool customFormatUnmetSearch = true,
        bool doUpgradeSearch = true,
        int searchLimit = 5)
    {
        var config = new TorrentarrConfig();
        config.Settings.SearchLoopDelay = 30;
        config.ArrInstances["Radarr-test"] = new ArrInstanceConfig
        {
            URI = "http://localhost:7878",
            APIKey = "test-key",
            Category = "movies-radarr",
            Type = "radarr",
            Managed = true,
            Search = new SearchConfig
            {
                SearchMissing = true,
                CustomFormatUnmetSearch = customFormatUnmetSearch,
                DoUpgradeSearch = doUpgradeSearch,
                QualityUnmetSearch = true,
                SearchLimit = searchLimit
            }
        };
        return config;
    }

    private static TorrentarrConfig CreateConfigWithSonarr(
        bool customFormatUnmetSearch = true,
        bool doUpgradeSearch = true,
        int searchLimit = 5)
    {
        var config = new TorrentarrConfig();
        config.Settings.SearchLoopDelay = 30;
        config.ArrInstances["Sonarr-test"] = new ArrInstanceConfig
        {
            URI = "http://localhost:8989",
            APIKey = "test-key",
            Category = "tv-sonarr",
            Type = "sonarr",
            Managed = true,
            Search = new SearchConfig
            {
                SearchMissing = true,
                CustomFormatUnmetSearch = customFormatUnmetSearch,
                DoUpgradeSearch = doUpgradeSearch,
                QualityUnmetSearch = true,
                SearchLimit = searchLimit
            }
        };
        return config;
    }

    private static TorrentarrConfig CreateConfigWithLidarr(
        bool customFormatUnmetSearch = true,
        int searchLimit = 5)
    {
        var config = new TorrentarrConfig();
        config.Settings.SearchLoopDelay = 30;
        config.ArrInstances["Lidarr-test"] = new ArrInstanceConfig
        {
            URI = "http://localhost:8686",
            APIKey = "test-key",
            Category = "music-lidarr",
            Type = "lidarr",
            Managed = true,
            Search = new SearchConfig
            {
                SearchMissing = true,
                CustomFormatUnmetSearch = customFormatUnmetSearch,
                SearchLimit = searchLimit
            }
        };
        return config;
    }

    [Fact]
    public void Constructor_CreatesInstance()
    {
        var service = CreateService();
        service.Should().NotBeNull();
    }

    [Fact]
    public async Task SearchMissingMediaAsync_NoInstance_ReturnsEmpty()
    {
        var service = CreateService();

        var result = await service.SearchMissingMediaAsync("nonexistent-category");

        result.SearchesTriggered.Should().Be(0);
        result.SearchedIds.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchQualityUpgradesAsync_NoInstance_ReturnsEmpty()
    {
        var service = CreateService();

        var result = await service.SearchQualityUpgradesAsync("nonexistent-category");

        result.SearchesTriggered.Should().Be(0);
    }

    [Fact]
    public async Task SearchQualityUpgradesAsync_UpgradeSearchDisabled_ReturnsEmpty()
    {
        var config = CreateConfigWithRadarr(customFormatUnmetSearch: false, doUpgradeSearch: false);
        var service = CreateService(config);

        var result = await service.SearchQualityUpgradesAsync("movies-radarr");

        result.SearchesTriggered.Should().Be(0);
    }

    [Fact]
    public async Task GetWantedMediaAsync_NoInstance_ReturnsEmpty()
    {
        var service = CreateService();

        var result = await service.GetWantedMediaAsync("nonexistent-category");

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetCustomFormatUnmetMediaAsync_NoInstance_ReturnsEmpty()
    {
        var service = CreateService();

        var result = await service.GetCustomFormatUnmetMediaAsync("nonexistent-category");

        result.UnmetMedia.Should().BeEmpty();
    }

    [Fact]
    public async Task IsQualityUpgradeAsync_ReturnsFalse()
    {
        var service = CreateService();

        var result = await service.IsQualityUpgradeAsync(1, "Bluray-1080p");

        result.Should().BeFalse();
    }

    [Fact]
    public async Task SearchMissingMediaAsync_SearchLoopDelayDisabled_ReturnsEmpty()
    {
        var config = CreateConfigWithRadarr();
        config.Settings.SearchLoopDelay = -1;
        var service = CreateService(config);

        var result = await service.SearchMissingMediaAsync("movies-radarr");

        result.SearchesTriggered.Should().Be(0);
    }

    [Fact]
    public async Task SearchQualityUpgradesAsync_SearchLoopDelayDisabled_ReturnsEmpty()
    {
        var config = CreateConfigWithRadarr();
        config.Settings.SearchLoopDelay = 0;
        var service = CreateService(config);

        var result = await service.SearchQualityUpgradesAsync("movies-radarr");

        result.SearchesTriggered.Should().Be(0);
    }
}

public class CustomFormatScoringTests
{
    [Fact]
    public void QualityProfile_HasMinCustomFormatScore()
    {
        var profile = new QualityProfile
        {
            Id = 1,
            Name = "HD-1080p",
            MinCustomFormatScore = 100,
            Cutoff = 4
        };

        profile.MinCustomFormatScore.Should().Be(100);
    }

    [Fact]
    public void MovieFile_HasCustomFormatScore()
    {
        var movieFile = new MovieFile
        {
            Id = 1,
            RelativePath = "movie.mkv",
            CustomFormatScore = 150,
            CustomFormats = new List<CustomFormat>
            {
                new() { Id = 1, Name = "DTS-HD MA" },
                new() { Id = 2, Name = "HDR" }
            }
        };

        movieFile.CustomFormatScore.Should().Be(150);
        movieFile.CustomFormats.Should().HaveCount(2);
    }

    [Fact]
    public void EpisodeFile_HasCustomFormatScore()
    {
        var episodeFile = new EpisodeFile
        {
            Id = 1,
            SeriesId = 100,
            SeasonNumber = 1,
            RelativePath = "episode.mkv",
            CustomFormatScore = 75,
            CustomFormats = new List<CustomFormat>
            {
                new() { Id = 1, Name = "DD5.1" }
            }
        };

        episodeFile.CustomFormatScore.Should().Be(75);
    }

    [Fact]
    public void TrackFile_HasCustomFormatScore()
    {
        var trackFile = new TrackFile
        {
            Id = 1,
            AlbumId = 50,
            ArtistId = 10,
            RelativePath = "track.flac",
            CustomFormatScore = 200,
            CustomFormats = new List<CustomFormat>
            {
                new() { Id = 1, Name = "FLAC" }
            }
        };

        trackFile.CustomFormatScore.Should().Be(200);
    }
}

public class RadarrMovieModelTests
{
    [Fact]
    public void RadarrMovie_HasMovieFileId()
    {
        var movie = new RadarrMovie
        {
            Id = 1,
            Title = "Test Movie",
            Year = 2024,
            Monitored = true,
            HasFile = true,
            QualityProfileId = 1,
            MovieFileId = 100
        };

        movie.MovieFileId.Should().Be(100);
    }

    [Fact]
    public void RadarrMovie_WithMovieFile_HasCustomFormatScore()
    {
        var movie = new RadarrMovie
        {
            Id = 1,
            Title = "Test Movie",
            HasFile = true,
            MovieFile = new MovieFile
            {
                Id = 100,
                CustomFormatScore = 150
            }
        };

        movie.MovieFile?.CustomFormatScore.Should().Be(150);
    }
}

public class SonarrEpisodeModelTests
{
    [Fact]
    public void SonarrEpisode_HasEpisodeFile()
    {
        var episode = new SonarrEpisode
        {
            Id = 1,
            SeriesId = 100,
            SeasonNumber = 1,
            EpisodeNumber = 1,
            Title = "Pilot",
            HasFile = true,
            Monitored = true,
            EpisodeFile = new EpisodeFile
            {
                Id = 50,
                CustomFormatScore = 100
            }
        };

        episode.EpisodeFile?.CustomFormatScore.Should().Be(100);
    }
}

public class SearchConfigTests
{
    [Fact]
    public void SearchConfig_Defaults()
    {
        var config = new SearchConfig();

        config.SearchMissing.Should().BeTrue();
        config.CustomFormatUnmetSearch.Should().BeFalse();
        config.DoUpgradeSearch.Should().BeFalse();
        config.QualityUnmetSearch.Should().BeFalse();
        config.ForceMinimumCustomFormat.Should().BeFalse();
        config.SearchLimit.Should().Be(5);
    }

    [Fact]
    public void SearchConfig_WithCustomFormatUnmetSearch()
    {
        var config = new SearchConfig
        {
            CustomFormatUnmetSearch = true,
            DoUpgradeSearch = true,
            QualityUnmetSearch = true,
            SearchLimit = 10
        };

        config.CustomFormatUnmetSearch.Should().BeTrue();
        config.DoUpgradeSearch.Should().BeTrue();
        config.QualityUnmetSearch.Should().BeTrue();
        config.SearchLimit.Should().Be(10);
    }
}

public class QualityUpgradeResultTests
{
    [Fact]
    public void QualityUpgradeResult_Defaults()
    {
        var result = new QualityUpgradeResult();

        result.UnmetMedia.Should().BeEmpty();
        result.TotalUnmet.Should().Be(0);
    }

    [Fact]
    public void QualityUpgradeResult_WithItems()
    {
        var result = new QualityUpgradeResult
        {
            UnmetMedia = new List<CustomFormatUnmetItem>
            {
                new() { Id = 1, Title = "Movie 1", CurrentCustomFormatScore = 50, MinCustomFormatScore = 100 },
                new() { Id = 2, Title = "Movie 2", CurrentCustomFormatScore = 75, MinCustomFormatScore = 100 }
            }
        };

        result.TotalUnmet.Should().Be(2);
    }
}

public class CustomFormatUnmetItemTests
{
    [Fact]
    public void CustomFormatUnmetItem_Properties()
    {
        var item = new CustomFormatUnmetItem
        {
            Id = 1,
            Title = "Test Movie",
            Type = "Movie",
            CurrentCustomFormatScore = 50,
            MinCustomFormatScore = 100,
            QualityProfileId = 5,
            QualityProfileName = "HD-1080p",
            FilePath = "/movies/Test Movie/test.mkv"
        };

        item.Id.Should().Be(1);
        item.Title.Should().Be("Test Movie");
        item.Type.Should().Be("Movie");
        item.CurrentCustomFormatScore.Should().Be(50);
        item.MinCustomFormatScore.Should().Be(100);
        item.QualityProfileId.Should().Be(5);
        item.QualityProfileName.Should().Be("HD-1080p");
        item.FilePath.Should().Be("/movies/Test Movie/test.mkv");
    }

    [Fact]
    public void CustomFormatUnmetItem_EpisodeProperties()
    {
        var item = new CustomFormatUnmetItem
        {
            Id = 1,
            Title = "Test Series S01E01",
            Type = "Episode",
            SeriesId = 100,
            SeasonNumber = 1,
            EpisodeNumber = 1
        };

        item.SeriesId.Should().Be(100);
        item.SeasonNumber.Should().Be(1);
        item.EpisodeNumber.Should().Be(1);
    }

    [Fact]
    public void CustomFormatUnmetItem_AlbumProperties()
    {
        var item = new CustomFormatUnmetItem
        {
            Id = 1,
            Title = "Artist - Album",
            Type = "Album",
            ArtistId = 50
        };

        item.ArtistId.Should().Be(50);
    }
}
