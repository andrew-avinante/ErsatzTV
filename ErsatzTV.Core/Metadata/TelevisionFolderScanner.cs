﻿using Bugsnag;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Images;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Search;
using MediatR;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Metadata;

public class TelevisionFolderScanner : LocalFolderScanner, ITelevisionFolderScanner
{
    private readonly IClient _client;
    private readonly ILibraryRepository _libraryRepository;
    private readonly ILocalFileSystem _localFileSystem;
    private readonly ILocalMetadataProvider _localMetadataProvider;
    private readonly ILocalSubtitlesProvider _localSubtitlesProvider;
    private readonly ILogger<TelevisionFolderScanner> _logger;
    private readonly IMediator _mediator;
    private readonly IMetadataRepository _metadataRepository;
    private readonly ISearchIndex _searchIndex;
    private readonly ISearchRepository _searchRepository;
    private readonly ITelevisionRepository _televisionRepository;

    public TelevisionFolderScanner(
        ILocalFileSystem localFileSystem,
        ITelevisionRepository televisionRepository,
        ILocalStatisticsProvider localStatisticsProvider,
        ILocalMetadataProvider localMetadataProvider,
        ILocalSubtitlesProvider localSubtitlesProvider,
        IMetadataRepository metadataRepository,
        IImageCache imageCache,
        ISearchIndex searchIndex,
        ISearchRepository searchRepository,
        ILibraryRepository libraryRepository,
        IMediaItemRepository mediaItemRepository,
        IMediator mediator,
        IFFmpegProcessService ffmpegProcessService,
        ITempFilePool tempFilePool,
        IClient client,
        ILogger<TelevisionFolderScanner> logger) : base(
        localFileSystem,
        localStatisticsProvider,
        metadataRepository,
        mediaItemRepository,
        imageCache,
        ffmpegProcessService,
        tempFilePool,
        client,
        logger)
    {
        _localFileSystem = localFileSystem;
        _televisionRepository = televisionRepository;
        _localMetadataProvider = localMetadataProvider;
        _localSubtitlesProvider = localSubtitlesProvider;
        _metadataRepository = metadataRepository;
        _searchIndex = searchIndex;
        _searchRepository = searchRepository;
        _libraryRepository = libraryRepository;
        _mediator = mediator;
        _client = client;
        _logger = logger;
    }

    public async Task<Either<BaseError, Unit>> ScanFolder(
        LibraryPath libraryPath,
        string ffmpegPath,
        string ffprobePath,
        decimal progressMin,
        decimal progressMax,
        CancellationToken cancellationToken)
    {
        try
        {
            decimal progressSpread = progressMax - progressMin;

            var allShowFolders = _localFileSystem.ListSubdirectories(libraryPath.Path)
                .Filter(ShouldIncludeFolder)
                .OrderBy(identity)
                .ToList();

            foreach (string showFolder in allShowFolders)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return new ScanCanceled();
                }

                decimal percentCompletion = (decimal)allShowFolders.IndexOf(showFolder) / allShowFolders.Count;
                await _mediator.Publish(
                    new LibraryScanProgress(libraryPath.LibraryId, progressMin + percentCompletion * progressSpread),
                    cancellationToken);

                Either<BaseError, MediaItemScanResult<Show>> maybeShow =
                    await FindOrCreateShow(libraryPath.Id, showFolder)
                        .BindT(show => UpdateMetadataForShow(show, showFolder))
                        .BindT(show => UpdateArtworkForShow(show, showFolder, ArtworkKind.Poster, cancellationToken))
                        .BindT(show => UpdateArtworkForShow(show, showFolder, ArtworkKind.FanArt, cancellationToken))
                        .BindT(
                            show => UpdateArtworkForShow(show, showFolder, ArtworkKind.Thumbnail, cancellationToken));

                foreach (BaseError error in maybeShow.LeftToSeq())
                {
                    _logger.LogWarning(
                        "Error processing show in folder {Folder}: {Error}",
                        showFolder,
                        error.Value);
                }

                foreach (MediaItemScanResult<Show> result in maybeShow.RightToSeq())
                {
                    Either<BaseError, Unit> scanResult = await ScanSeasons(
                        libraryPath,
                        ffmpegPath,
                        ffprobePath,
                        result.Item,
                        showFolder,
                        cancellationToken);

                    foreach (ScanCanceled error in scanResult.LeftToSeq().OfType<ScanCanceled>())
                    {
                        return error;
                    }

                    if (result.IsAdded)
                    {
                        await _searchIndex.AddItems(_searchRepository, new List<MediaItem> { result.Item });
                    }
                    else if (result.IsUpdated)
                    {
                        await _searchIndex.UpdateItems(_searchRepository, new List<MediaItem> { result.Item });
                    }
                }
            }

            foreach (string path in await _televisionRepository.FindEpisodePaths(libraryPath))
            {
                if (!_localFileSystem.FileExists(path))
                {
                    _logger.LogInformation("Flagging missing episode at {Path}", path);
                    List<int> episodeIds = await FlagFileNotFound(libraryPath, path);
                    await _searchIndex.RebuildItems(_searchRepository, episodeIds);
                }
                else if (Path.GetFileName(path).StartsWith("._"))
                {
                    _logger.LogInformation("Removing dot underscore file at {Path}", path);
                    await _televisionRepository.DeleteByPath(libraryPath, path);
                }
            }

            await _libraryRepository.CleanEtagsForLibraryPath(libraryPath);

            await _televisionRepository.DeleteEmptySeasons(libraryPath);
            List<int> ids = await _televisionRepository.DeleteEmptyShows(libraryPath);
            await _searchIndex.RemoveItems(ids);

            return Unit.Default;
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            return new ScanCanceled();
        }
        finally
        {
            _searchIndex.Commit();
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<Show>>> FindOrCreateShow(
        int libraryPathId,
        string showFolder)
    {
        ShowMetadata metadata = await _localMetadataProvider.GetMetadataForShow(showFolder);
        Option<Show> maybeShow = await _televisionRepository.GetShowByMetadata(libraryPathId, metadata);

        foreach (Show show in maybeShow)
        {
            return new MediaItemScanResult<Show>(show);
        }

        return await _televisionRepository.AddShow(libraryPathId, showFolder, metadata);
    }

    private async Task<Either<BaseError, Unit>> ScanSeasons(
        LibraryPath libraryPath,
        string ffmpegPath,
        string ffprobePath,
        Show show,
        string showFolder,
        CancellationToken cancellationToken)
    {
        foreach (string seasonFolder in _localFileSystem.ListSubdirectories(showFolder).Filter(ShouldIncludeFolder)
                     .OrderBy(identity))
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new ScanCanceled();
            }

            string etag = FolderEtag.CalculateWithSubfolders(seasonFolder, _localFileSystem);
            Option<LibraryFolder> knownFolder = libraryPath.LibraryFolders
                .Filter(f => f.Path == seasonFolder)
                .HeadOrNone();

            // skip folder if etag matches
            if (await knownFolder.Map(f => f.Etag ?? string.Empty).IfNoneAsync(string.Empty) == etag)
            {
                continue;
            }

            Option<int> maybeSeasonNumber = SeasonNumberForFolder(seasonFolder);
            foreach (int seasonNumber in maybeSeasonNumber)
            {
                Either<BaseError, Season> maybeSeason = await _televisionRepository
                    .GetOrAddSeason(show, libraryPath.Id, seasonNumber)
                    .BindT(EnsureMetadataExists)
                    .BindT(season => UpdatePoster(season, seasonFolder, cancellationToken));

                foreach (BaseError error in maybeSeason.LeftToSeq())
                {
                    _logger.LogWarning(
                        "Error processing season in folder {Folder}: {Error}",
                        seasonFolder,
                        error.Value);
                }

                foreach (Season season in maybeSeason.RightToSeq())
                {
                    Either<BaseError, Unit> scanResult = await ScanEpisodes(
                        libraryPath,
                        ffmpegPath,
                        ffprobePath,
                        season,
                        seasonFolder,
                        cancellationToken);

                    foreach (ScanCanceled error in scanResult.LeftToSeq().OfType<ScanCanceled>())
                    {
                        return error;
                    }

                    await _libraryRepository.SetEtag(libraryPath, knownFolder, seasonFolder, etag);

                    season.Show = show;
                    await _searchIndex.UpdateItems(_searchRepository, new List<MediaItem> { season });
                }
            }
        }

        return Unit.Default;
    }

    private async Task<Either<BaseError, Unit>> ScanEpisodes(
        LibraryPath libraryPath,
        string ffmpegPath,
        string ffprobePath,
        Season season,
        string seasonPath,
        CancellationToken cancellationToken)
    {
        var allSeasonFiles = _localFileSystem.ListSubdirectories(seasonPath)
            .Map(_localFileSystem.ListFiles)
            .Flatten()
            .Append(_localFileSystem.ListFiles(seasonPath))
            .Filter(f => VideoFileExtensions.Contains(Path.GetExtension(f)))
            .Filter(f => !Path.GetFileName(f).StartsWith("._"))
            .OrderBy(identity)
            .ToList();

        foreach (string file in allSeasonFiles)
        {
            // TODO: figure out how to rebuild playlists
            Either<BaseError, Episode> maybeEpisode = await _televisionRepository
                .GetOrAddEpisode(season, libraryPath, file)
                .BindT(
                    episode => UpdateStatistics(new MediaItemScanResult<Episode>(episode), ffmpegPath, ffprobePath)
                        .MapT(_ => episode))
                .BindT(UpdateMetadata)
                .BindT(e => UpdateThumbnail(e, cancellationToken))
                .BindT(UpdateSubtitles)
                .BindT(e => FlagNormal(new MediaItemScanResult<Episode>(e)))
                .MapT(r => r.Item);

            foreach (BaseError error in maybeEpisode.LeftToSeq())
            {
                _logger.LogWarning("Error processing episode at {Path}: {Error}", file, error.Value);
            }

            foreach (Episode episode in maybeEpisode.RightToSeq())
            {
                await _searchIndex.UpdateItems(_searchRepository, new List<MediaItem> { episode });
            }
        }

        // TODO: remove missing episodes?

        return Unit.Default;
    }

    private async Task<Either<BaseError, MediaItemScanResult<Show>>> UpdateMetadataForShow(
        MediaItemScanResult<Show> result,
        string showFolder)
    {
        try
        {
            Show show = result.Item;

            Option<string> maybeNfo = LocateNfoFileForShow(showFolder);
            if (maybeNfo.IsNone)
            {
                if (!Optional(show.ShowMetadata).Flatten().Any())
                {
                    _logger.LogDebug("Refreshing {Attribute} for {Path}", "Fallback Metadata", showFolder);
                    if (await _localMetadataProvider.RefreshFallbackMetadata(show, showFolder))
                    {
                        result.IsUpdated = true;
                    }
                }
            }

            foreach (string nfoFile in maybeNfo)
            {
                bool shouldUpdate = Optional(show.ShowMetadata).Flatten().HeadOrNone().Match(
                    m => m.MetadataKind == MetadataKind.Fallback ||
                         m.DateUpdated != _localFileSystem.GetLastWriteTime(nfoFile),
                    true);

                if (shouldUpdate)
                {
                    _logger.LogDebug("Refreshing {Attribute} from {Path}", "Sidecar Metadata", nfoFile);
                    if (await _localMetadataProvider.RefreshSidecarMetadata(show, nfoFile))
                    {
                        result.IsUpdated = true;
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _client.Notify(ex);
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, Season>> EnsureMetadataExists(Season season)
    {
        season.SeasonMetadata ??= new List<SeasonMetadata>();

        if (!season.SeasonMetadata.Any())
        {
            var metadata = new SeasonMetadata
            {
                SeasonId = season.Id,
                Season = season,
                DateAdded = DateTime.UtcNow,
                Guids = new List<MetadataGuid>(),
                Tags = new List<Tag>()
            };

            season.SeasonMetadata.Add(metadata);
            await _metadataRepository.Add(metadata);
        }

        return season;
    }

    private async Task<Either<BaseError, Episode>> UpdateMetadata(Episode episode)
    {
        try
        {
            Option<string> maybeNfo = LocateNfoFile(episode);
            if (maybeNfo.IsNone)
            {
                bool shouldUpdate = Optional(episode.EpisodeMetadata).Flatten().HeadOrNone().Match(
                    m => m.DateUpdated == SystemTime.MinValueUtc,
                    true);

                if (shouldUpdate)
                {
                    string path = episode.MediaVersions.Head().MediaFiles.Head().Path;
                    _logger.LogDebug("Refreshing {Attribute} for {Path}", "Fallback Metadata", path);
                    await _localMetadataProvider.RefreshFallbackMetadata(episode);
                }
            }

            foreach (string nfoFile in maybeNfo)
            {
                bool shouldUpdate = Optional(episode.EpisodeMetadata).Flatten().HeadOrNone().Match(
                    m => m.MetadataKind == MetadataKind.Fallback ||
                         m.DateUpdated != _localFileSystem.GetLastWriteTime(nfoFile),
                    true);

                if (shouldUpdate)
                {
                    _logger.LogDebug("Refreshing {Attribute} from {Path}", "Sidecar Metadata", nfoFile);
                    await _localMetadataProvider.RefreshSidecarMetadata(episode, nfoFile);
                }
            }

            return episode;
        }
        catch (Exception ex)
        {
            _client.Notify(ex);
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, MediaItemScanResult<Show>>> UpdateArtworkForShow(
        MediaItemScanResult<Show> result,
        string showFolder,
        ArtworkKind artworkKind,
        CancellationToken cancellationToken)
    {
        try
        {
            Show show = result.Item;
            Option<string> maybeArtwork = LocateArtworkForShow(showFolder, artworkKind);
            foreach (string artworkFile in maybeArtwork)
            {
                ShowMetadata metadata = show.ShowMetadata.Head();
                await RefreshArtwork(artworkFile, metadata, artworkKind, None, None, cancellationToken);
            }

            return result;
        }
        catch (Exception ex)
        {
            _client.Notify(ex);
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, Season>> UpdatePoster(
        Season season,
        string seasonFolder,
        CancellationToken cancellationToken)
    {
        try
        {
            Option<string> maybePoster = LocatePoster(season, seasonFolder);
            foreach (string posterFile in maybePoster)
            {
                SeasonMetadata metadata = season.SeasonMetadata.Head();
                await RefreshArtwork(posterFile, metadata, ArtworkKind.Poster, None, None, cancellationToken);
            }

            return season;
        }
        catch (Exception ex)
        {
            _client.Notify(ex);
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, Episode>> UpdateThumbnail(Episode episode, CancellationToken cancellationToken)
    {
        try
        {
            Option<string> maybeThumbnail = LocateThumbnail(episode);
            foreach (string thumbnailFile in maybeThumbnail)
            {
                foreach (EpisodeMetadata metadata in episode.EpisodeMetadata)
                {
                    await RefreshArtwork(
                        thumbnailFile,
                        metadata,
                        ArtworkKind.Thumbnail,
                        None,
                        None,
                        cancellationToken);
                }
            }

            return episode;
        }
        catch (Exception ex)
        {
            _client.Notify(ex);
            return BaseError.New(ex.ToString());
        }
    }

    private async Task<Either<BaseError, Episode>> UpdateSubtitles(Episode episode)
    {
        try
        {
            await _localSubtitlesProvider.UpdateSubtitles(episode, None, true);
            return episode;
        }
        catch (Exception ex)
        {
            _client.Notify(ex);
            return BaseError.New(ex.ToString());
        }
    }

    private Option<string> LocateNfoFileForShow(string showFolder) =>
        Optional(Path.Combine(showFolder, "tvshow.nfo")).Filter(s => _localFileSystem.FileExists(s));

    private Option<string> LocateNfoFile(Episode episode)
    {
        string path = episode.MediaVersions.Head().MediaFiles.Head().Path;
        return Optional(Path.ChangeExtension(path, "nfo")).Filter(s => _localFileSystem.FileExists(s));
    }

    private Option<string> LocateArtworkForShow(string showFolder, ArtworkKind artworkKind)
    {
        string[] segments = artworkKind switch
        {
            ArtworkKind.Poster => new[] { "poster", "folder" },
            ArtworkKind.FanArt => new[] { "fanart" },
            ArtworkKind.Thumbnail => new[] { "thumb" },
            _ => throw new ArgumentOutOfRangeException(nameof(artworkKind))
        };

        return ImageFileExtensions
            .Map(ext => segments.Map(segment => $"{segment}.{ext}"))
            .Flatten()
            .Map(f => Path.Combine(showFolder, f))
            .Filter(s => _localFileSystem.FileExists(s))
            .HeadOrNone();
    }

    private Option<string> LocatePoster(Season season, string seasonFolder)
    {
        string folder = Path.GetDirectoryName(seasonFolder) ?? string.Empty;
        return ImageFileExtensions
            .Map(ext => Path.Combine(folder, $"season{season.SeasonNumber:00}-poster.{ext}"))
            .Filter(s => _localFileSystem.FileExists(s))
            .HeadOrNone();
    }

    private Option<string> LocateThumbnail(Episode episode)
    {
        string path = episode.MediaVersions.Head().MediaFiles.Head().Path;
        string folder = Path.GetDirectoryName(path) ?? string.Empty;
        return ImageFileExtensions
            .Map(ext => Path.GetFileNameWithoutExtension(path) + $"-thumb.{ext}")
            .Map(f => Path.Combine(folder, f))
            .Filter(f => _localFileSystem.FileExists(f))
            .HeadOrNone();
    }

    private static Option<int> SeasonNumberForFolder(string folder)
    {
        if (int.TryParse(folder.Split(" ").Last(), out int seasonNumber))
        {
            return seasonNumber;
        }

        if (folder.EndsWith("specials", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return None;
    }
}
