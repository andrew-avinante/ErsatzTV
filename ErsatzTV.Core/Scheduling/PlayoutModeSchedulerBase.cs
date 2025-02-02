using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;
using LanguageExt.UnsafeValueAccess;
using Microsoft.Extensions.Logging;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace ErsatzTV.Core.Scheduling;

public abstract class PlayoutModeSchedulerBase<T> : IPlayoutModeScheduler<T> where T : ProgramScheduleItem
{
    protected readonly ILogger _logger;

    protected PlayoutModeSchedulerBase(ILogger logger) => _logger = logger;

    public abstract Tuple<PlayoutBuilderState, List<PlayoutItem>> Schedule(
        PlayoutBuilderState playoutBuilderState,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> collectionEnumerators,
        T scheduleItem,
        ProgramScheduleItem nextScheduleItem,
        DateTimeOffset hardStop,
        CancellationToken cancellationToken);

    public static DateTimeOffset GetFillerStartTimeAfter(
        PlayoutBuilderState state,
        ProgramScheduleItem scheduleItem,
        DateTimeOffset hardStop)
    {
        DateTimeOffset startTime = GetStartTimeAfter(state, scheduleItem);

        // filler should always stop at the hard stop
        if (hardStop < startTime)
        {
            startTime = hardStop;
        }

        return startTime;
    }

    public static DateTimeOffset GetStartTimeAfter(PlayoutBuilderState state, ProgramScheduleItem scheduleItem)
    {
        DateTimeOffset startTime = state.CurrentTime.ToLocalTime();

        bool isIncomplete = scheduleItem is ProgramScheduleItemMultiple && state.MultipleRemaining.IsSome ||
                            scheduleItem is ProgramScheduleItemDuration && state.DurationFinish.IsSome ||
                            scheduleItem is ProgramScheduleItemFlood && state.InFlood ||
                            scheduleItem is ProgramScheduleItemDuration && state.InDurationFiller;

        if (scheduleItem.StartType == StartType.Fixed && !isIncomplete)
        {
            TimeSpan itemStartTime = scheduleItem.StartTime.GetValueOrDefault();
            DateTime date = startTime.Date;
            DateTimeOffset result = new DateTimeOffset(
                    date.Year,
                    date.Month,
                    date.Day,
                    0,
                    0,
                    0,
                    TimeZoneInfo.Local.GetUtcOffset(
                        new DateTime(date.Year, date.Month, date.Day, 0, 0, 0, DateTimeKind.Local)))
                .Add(itemStartTime);

            // DateTimeOffset result = startTime.Date + itemStartTime;
            // need to wrap to the next day if appropriate
            startTime = startTime.TimeOfDay > itemStartTime ? result.AddDays(1) : result;
        }

        return startTime;
    }

    protected Tuple<PlayoutBuilderState, List<PlayoutItem>> AddTailFiller(
        PlayoutBuilderState playoutBuilderState,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> collectionEnumerators,
        ProgramScheduleItem scheduleItem,
        List<PlayoutItem> playoutItems,
        DateTimeOffset nextItemStart,
        CancellationToken cancellationToken)
    {
        var newItems = new List<PlayoutItem>(playoutItems);
        PlayoutBuilderState nextState = playoutBuilderState;

        if (scheduleItem.TailFiller != null)
        {
            IMediaCollectionEnumerator enumerator =
                collectionEnumerators[CollectionKey.ForFillerPreset(scheduleItem.TailFiller)];

            while (enumerator.Current.IsSome && nextState.CurrentTime < nextItemStart)
            {
                MediaItem mediaItem = enumerator.Current.ValueUnsafe();

                TimeSpan itemDuration = DurationForMediaItem(mediaItem);

                if (nextState.CurrentTime + itemDuration > nextItemStart)
                {
                    _logger.LogDebug(
                        "Filler with duration {Duration:hh\\:mm\\:ss} will go past next item start {NextItemStart}",
                        itemDuration,
                        nextItemStart);

                    break;
                }

                var playoutItem = new PlayoutItem
                {
                    MediaItemId = mediaItem.Id,
                    Start = nextState.CurrentTime.UtcDateTime,
                    Finish = nextState.CurrentTime.UtcDateTime + itemDuration,
                    InPoint = TimeSpan.Zero,
                    OutPoint = itemDuration,
                    FillerKind = FillerKind.Tail,
                    GuideGroup = nextState.NextGuideGroup,
                    DisableWatermarks = !scheduleItem.TailFiller.AllowWatermarks
                };

                newItems.Add(playoutItem);

                nextState = nextState with
                {
                    CurrentTime = nextState.CurrentTime + itemDuration
                };

                enumerator.MoveNext();
            }
        }

        return Tuple(nextState, newItems);
    }

    protected Tuple<PlayoutBuilderState, List<PlayoutItem>> AddFallbackFiller(
        PlayoutBuilderState playoutBuilderState,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> collectionEnumerators,
        ProgramScheduleItem scheduleItem,
        List<PlayoutItem> playoutItems,
        DateTimeOffset nextItemStart,
        CancellationToken cancellationToken)
    {
        var newItems = new List<PlayoutItem>(playoutItems);
        PlayoutBuilderState nextState = playoutBuilderState;

        if (scheduleItem.FallbackFiller != null && playoutBuilderState.CurrentTime < nextItemStart)
        {
            IMediaCollectionEnumerator enumerator =
                collectionEnumerators[CollectionKey.ForFillerPreset(scheduleItem.FallbackFiller)];

            foreach (MediaItem mediaItem in enumerator.Current)
            {
                var playoutItem = new PlayoutItem
                {
                    MediaItemId = mediaItem.Id,
                    Start = nextState.CurrentTime.UtcDateTime,
                    Finish = nextItemStart.UtcDateTime,
                    InPoint = TimeSpan.Zero,
                    OutPoint = TimeSpan.Zero,
                    GuideGroup = nextState.NextGuideGroup,
                    FillerKind = FillerKind.Fallback,
                    DisableWatermarks = !scheduleItem.FallbackFiller.AllowWatermarks
                };

                newItems.Add(playoutItem);

                nextState = nextState with
                {
                    CurrentTime = nextItemStart.UtcDateTime
                };

                enumerator.MoveNext();
            }
        }

        return Tuple(nextState, newItems);
    }

    protected static TimeSpan DurationForMediaItem(MediaItem mediaItem)
    {
        MediaVersion version = mediaItem.GetHeadVersion();
        return version.Duration;
    }

    protected static List<MediaChapter> ChaptersForMediaItem(MediaItem mediaItem)
    {
        MediaVersion version = mediaItem.GetHeadVersion();
        return Optional(version.Chapters).Flatten().OrderBy(c => c.StartTime).ToList();
    }

    protected void LogScheduledItem(
        ProgramScheduleItem scheduleItem,
        MediaItem mediaItem,
        DateTimeOffset startTime) =>
        _logger.LogDebug(
            "Scheduling media item: {ScheduleItemNumber} / {CollectionType} / {MediaItemId} - {MediaItemTitle} / {StartTime}",
            scheduleItem.Index,
            scheduleItem.CollectionType,
            mediaItem.Id,
            PlayoutBuilder.DisplayTitle(mediaItem),
            startTime);

    internal List<PlayoutItem> AddFiller(
        PlayoutBuilderState playoutBuilderState,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> enumerators,
        ProgramScheduleItem scheduleItem,
        PlayoutItem playoutItem,
        List<MediaChapter> chapters,
        bool log,
        CancellationToken cancellationToken)
    {
        var result = new List<PlayoutItem>();

        var allFiller = Optional(scheduleItem.PreRollFiller)
            .Append(Optional(scheduleItem.MidRollEnterFiller))
            .Append(Optional(scheduleItem.MidRollFiller))
            .Append(Optional(scheduleItem.MidRollExitFiller))
            .Append(Optional(scheduleItem.PostRollFiller))
            .ToList();

        // multiple pad-to-nearest-minute values are invalid; use no filler
        if (allFiller.Count(f => f.FillerMode == FillerMode.Pad && f.PadToNearestMinute.HasValue) > 1)
        {
            _logger.LogError("Multiple pad-to-nearest-minute values are invalid; no filler will be used");
            return new List<PlayoutItem> { playoutItem };
        }

        List<MediaChapter> effectiveChapters = chapters;
        if (allFiller.All(fp => fp.FillerKind != FillerKind.MidRoll && fp.FillerKind != FillerKind.MidRollEnter && fp.FillerKind != FillerKind.MidRollExit) || effectiveChapters.Count <= 1)
        {
            effectiveChapters = new List<MediaChapter>();
        }

        foreach (FillerPreset filler in allFiller.Filter(
                     f => f.FillerKind == FillerKind.PreRoll && f.FillerMode != FillerMode.Pad))
        {
            switch (filler.FillerMode)
            {
                case FillerMode.Duration when filler.Duration.HasValue:
                    IMediaCollectionEnumerator e1 = enumerators[CollectionKey.ForFillerPreset(filler)];
                    result.AddRange(
                        AddDurationFiller(
                            playoutBuilderState,
                            e1,
                            filler.Duration.Value,
                            FillerKind.PreRoll,
                            filler.AllowWatermarks,
                            log,
                            cancellationToken));
                    break;
                case FillerMode.Count when filler.Count.HasValue:
                    IMediaCollectionEnumerator e2 = enumerators[CollectionKey.ForFillerPreset(filler)];
                    result.AddRange(
                        AddCountFiller(
                            playoutBuilderState,
                            e2,
                            filler.Count.Value,
                            FillerKind.PreRoll,
                            filler.AllowWatermarks,
                            cancellationToken));
                    break;
            }
        }

        if (effectiveChapters.Count <= 1)
        {
            result.Add(playoutItem);
        }
        else
        {
            foreach (FillerPreset filler in allFiller.Filter(
                         f => f.FillerKind == FillerKind.MidRoll && f.FillerMode != FillerMode.Pad))
            {
                switch (filler.FillerMode)
                {
                    case FillerMode.Duration when filler.Duration.HasValue:
                        IMediaCollectionEnumerator e1 = enumerators[CollectionKey.ForFillerPreset(filler)];
                        for (var i = 0; i < effectiveChapters.Count; i++)
                        {
                            result.Add(playoutItem.ForChapter(effectiveChapters[i]));
                            if (i < effectiveChapters.Count - 1)
                            {
                                foreach (FillerPreset fillerEnter in allFiller.Filter(
                                    f => f.FillerKind == FillerKind.MidRollEnter && f.FillerMode != FillerMode.Pad))
                                {
                                    IMediaCollectionEnumerator e3 = enumerators[CollectionKey.ForFillerPreset(fillerEnter)];
                                    result.AddRange(
                                       AddCountFiller(
                                           playoutBuilderState,
                                           e3,
                                           fillerEnter.Count.Value,
                                           FillerKind.MidRollEnter,
                                           fillerEnter.AllowWatermarks,
                                            cancellationToken));
                                }

                                result.AddRange(
                                    AddDurationFiller(
                                        playoutBuilderState,
                                        e1,
                                        filler.Duration.Value,
                                        FillerKind.MidRoll,
                                        filler.AllowWatermarks,
                                        log,
                                        cancellationToken));

                                foreach (FillerPreset fillerExit in allFiller.Filter(
                                    f => f.FillerKind == FillerKind.MidRollExit && f.FillerMode != FillerMode.Pad))
                                {
                                    IMediaCollectionEnumerator e3 = enumerators[CollectionKey.ForFillerPreset(fillerExit)];
                                    result.AddRange(
                                       AddCountFiller(
                                           playoutBuilderState,
                                           e3,
                                           fillerExit.Count.Value,
                                           FillerKind.MidRollExit,
                                           fillerExit.AllowWatermarks,
                                            cancellationToken));
                                }
                            }
                        }

                        break;
                    case FillerMode.Count when filler.Count.HasValue:
                        IMediaCollectionEnumerator e2 = enumerators[CollectionKey.ForFillerPreset(filler)];
                        for (var i = 0; i < effectiveChapters.Count; i++)
                        {
                            result.Add(playoutItem.ForChapter(effectiveChapters[i]));
                            if (i < effectiveChapters.Count - 1)
                            {
                                foreach (FillerPreset fillerEnter in allFiller.Filter(
                                    f => f.FillerKind == FillerKind.MidRollEnter && f.FillerMode != FillerMode.Pad))
                                {
                                    IMediaCollectionEnumerator e3 = enumerators[CollectionKey.ForFillerPreset(fillerEnter)];
                                    result.AddRange(
                                       AddCountFiller(
                                           playoutBuilderState,
                                           e3,
                                           fillerEnter.Count.Value,
                                           FillerKind.MidRollEnter,
                                           fillerEnter.AllowWatermarks,
                                           cancellationToken));
                                }

                                result.AddRange(
                                AddCountFiller(
                                    playoutBuilderState,
                                    e2,
                                    filler.Count.Value,
                                    FillerKind.MidRoll,
                                    filler.AllowWatermarks,
                                    cancellationToken));

                                foreach (FillerPreset fillerExit in allFiller.Filter(
                                    f => f.FillerKind == FillerKind.MidRollExit && f.FillerMode != FillerMode.Pad))
                                {
                                    IMediaCollectionEnumerator e3 = enumerators[CollectionKey.ForFillerPreset(fillerExit)];
                                    result.AddRange(
                                       AddCountFiller(
                                           playoutBuilderState,
                                           e3,
                                           fillerExit.Count.Value,
                                           FillerKind.MidRollExit,
                                           fillerExit.AllowWatermarks,
                                           cancellationToken));
                                }
                            }
                        }

                        break;
                }
            }
        }

        foreach (FillerPreset filler in allFiller.Filter(
                     f => f.FillerKind == FillerKind.PostRoll && f.FillerMode != FillerMode.Pad))
        {
            switch (filler.FillerMode)
            {
                case FillerMode.Duration when filler.Duration.HasValue:
                    IMediaCollectionEnumerator e1 = enumerators[CollectionKey.ForFillerPreset(filler)];
                    result.AddRange(
                        AddDurationFiller(
                            playoutBuilderState,
                            e1,
                            filler.Duration.Value,
                            FillerKind.PostRoll,
                            filler.AllowWatermarks,
                            log,
                            cancellationToken));
                    break;
                case FillerMode.Count when filler.Count.HasValue:
                    IMediaCollectionEnumerator e2 = enumerators[CollectionKey.ForFillerPreset(filler)];
                    result.AddRange(
                        AddCountFiller(
                            playoutBuilderState,
                            e2,
                            filler.Count.Value,
                            FillerKind.PostRoll,
                            filler.AllowWatermarks,
                            cancellationToken));
                    break;
            }
        }

        // after all non-padded filler has been added, figure out padding
        foreach (FillerPreset padFiller in Optional(
                     allFiller.FirstOrDefault(f => f.FillerMode == FillerMode.Pad && f.PadToNearestMinute.HasValue)))
        {
            var totalDuration = TimeSpan.FromMilliseconds(result.Sum(pi => (pi.Finish - pi.Start).TotalMilliseconds));

            // add primary content to totalDuration only if it hasn't already been added
            if (result.All(pi => pi.MediaItemId != playoutItem.MediaItemId))
            {
                totalDuration += TimeSpan.FromMilliseconds(
                    effectiveChapters.Sum(c => (c.EndTime - c.StartTime).TotalMilliseconds));
            }

            int currentMinute = (playoutItem.StartOffset + totalDuration).Minute;
            // ReSharper disable once PossibleInvalidOperationException
            int targetMinute = (currentMinute + padFiller.PadToNearestMinute.Value - 1) /
                padFiller.PadToNearestMinute.Value * padFiller.PadToNearestMinute.Value;

            DateTimeOffset almostTargetTime = playoutItem.StartOffset + totalDuration -
                                              TimeSpan.FromMinutes(currentMinute) +
                                              TimeSpan.FromMinutes(targetMinute);


            var targetTime = new DateTimeOffset(
                almostTargetTime.Year,
                almostTargetTime.Month,
                almostTargetTime.Day,
                almostTargetTime.Hour,
                almostTargetTime.Minute,
                0,
                almostTargetTime.Offset);

            TimeSpan remainingToFill = targetTime - totalDuration - playoutItem.StartOffset;

            // _logger.LogInformation(
            //     "Total duration {TotalDuration}; need to fill {TimeSpan} to pad properly to {TargetTime}",
            //     totalDuration,
            //     remainingToFill,
            //     targetTime);

            switch (padFiller.FillerKind)
            {
                case FillerKind.PreRoll:
                    IMediaCollectionEnumerator pre1 = enumerators[CollectionKey.ForFillerPreset(padFiller)];
                    result.InsertRange(
                        0,
                        AddDurationFiller(
                            playoutBuilderState,
                            pre1,
                            remainingToFill,
                            FillerKind.PreRoll,
                            padFiller.AllowWatermarks,
                            log,
                            cancellationToken));
                    totalDuration =
                        TimeSpan.FromMilliseconds(result.Sum(pi => (pi.Finish - pi.Start).TotalMilliseconds));
                    remainingToFill = targetTime - totalDuration - playoutItem.StartOffset;
                    if (remainingToFill > TimeSpan.Zero)
                    {
                        result.InsertRange(
                            0,
                            FallbackFillerForPad(
                                playoutBuilderState,
                                enumerators,
                                scheduleItem,
                                remainingToFill,
                                cancellationToken));
                    }

                    break;
                case FillerKind.MidRoll:
                    IMediaCollectionEnumerator mid1 = enumerators[CollectionKey.ForFillerPreset(padFiller)];
                    var fillerQueue = new Queue<PlayoutItem>(
                        AddDurationFiller(
                            playoutBuilderState,
                            mid1,
                            remainingToFill,
                            FillerKind.MidRoll,
                            padFiller.AllowWatermarks,
                            log,
                            cancellationToken));
                    TimeSpan average = effectiveChapters.Count <= 1
                        ? remainingToFill
                        : remainingToFill / (effectiveChapters.Count - 1);
                    TimeSpan filled = TimeSpan.Zero;

                    // remove post-roll to add after mid-roll/content
                    var postRoll = result.Where(i => i.FillerKind == FillerKind.PostRoll).ToList();
                    result.RemoveAll(i => i.FillerKind == FillerKind.PostRoll);

                    for (var i = 0; i < effectiveChapters.Count; i++)
                    {
                        result.Add(playoutItem.ForChapter(effectiveChapters[i]));
                        if (i < effectiveChapters.Count - 1)
                        {
                            TimeSpan current = TimeSpan.Zero;
                            List<PlayoutItem> midRollExit = new List<PlayoutItem>();

                            if (current < average && filled < remainingToFill)
                            {
                                foreach (FillerPreset fillerEnter in allFiller.Filter(
                                    f => f.FillerKind == FillerKind.MidRollEnter && f.FillerMode != FillerMode.Pad))
                                {
                                    IMediaCollectionEnumerator e3 = enumerators[CollectionKey.ForFillerPreset(fillerEnter)];

                                    if (current + DurationForMediaItem((MediaItem)e3.Current) < average && filled + DurationForMediaItem((MediaItem)e3.Current) < remainingToFill)
                                    {
                                        result.AddRange(
                                           AddCountFiller(
                                               playoutBuilderState,
                                               e3,
                                               fillerEnter.Count.Value,
                                               FillerKind.MidRollEnter,
                                               fillerEnter.AllowWatermarks,
                                               cancellationToken));

                                        current += DurationForMediaItem((MediaItem)e3.Current);
                                        filled += DurationForMediaItem((MediaItem)e3.Current);
                                    }
                                }

                                foreach (FillerPreset fillerExit in allFiller.Filter(
                                    f => f.FillerKind == FillerKind.MidRollExit && f.FillerMode != FillerMode.Pad))
                                {
                                    IMediaCollectionEnumerator e3 = enumerators[CollectionKey.ForFillerPreset(fillerExit)];
                                    if (current + DurationForMediaItem((MediaItem)e3.Current) < average && filled + DurationForMediaItem((MediaItem)e3.Current) < remainingToFill)
                                    {
                                        midRollExit = AddCountFiller(
                                           playoutBuilderState,
                                           e3,
                                           fillerExit.Count.Value,
                                           FillerKind.MidRollExit,
                                           fillerExit.AllowWatermarks,
                                           cancellationToken);

                                        current += DurationForMediaItem((MediaItem)e3.Current);
                                        filled += DurationForMediaItem((MediaItem)e3.Current);
                                    }
                                }
                            }

                            while (current < average && filled < remainingToFill)
                            {
                                if (fillerQueue.TryDequeue(out PlayoutItem fillerItem) && current + (fillerItem.Finish - fillerItem.Start) < average && filled + (fillerItem.Finish - fillerItem.Start) < remainingToFill)
                                {
                                    result.Add(fillerItem);
                                    current += fillerItem.Finish - fillerItem.Start;
                                    filled += fillerItem.Finish - fillerItem.Start;
                                }
                                else if(allFiller.Where(x => x.FillerKind == FillerKind.MidRollEnter || x.FillerKind == FillerKind.MidRollExit).Count() == 0)
                                {
                                    TimeSpan leftInThisBreak = average - current;
                                    TimeSpan leftOverall = remainingToFill - filled;

                                    TimeSpan maxThisBreak = leftOverall < leftInThisBreak
                                        ? leftOverall
                                        : leftInThisBreak;

                                    Option<PlayoutItem> maybeFallback = FallbackFillerForPad(
                                        playoutBuilderState,
                                        enumerators,
                                        scheduleItem,
                                        i < effectiveChapters.Count - 1 ? maxThisBreak : leftOverall,
                                        cancellationToken);

                                    foreach (PlayoutItem fallback in maybeFallback)
                                    {
                                        current += fallback.Finish - fallback.Start;
                                        filled += fallback.Finish - fallback.Start;
                                        result.Add(fallback);
                                    }
                                } else
                                {
                                    break;
                                }
                            }

                            // If there wasn't enough space to add in mid roll stuff then remove the roll enter
                            if (result.Last().FillerKind == FillerKind.MidRollEnter)
                            {
                                result.Remove(result.Last());
                            }
                            else
                            {
                                result.AddRange(midRollExit);
                            }
                        }
                    }

                    result.AddRange(postRoll);

                    break;
                case FillerKind.PostRoll:
                    IMediaCollectionEnumerator post1 = enumerators[CollectionKey.ForFillerPreset(padFiller)];
                    result.AddRange(
                        AddDurationFiller(
                            playoutBuilderState,
                            post1,
                            remainingToFill,
                            FillerKind.PostRoll,
                            padFiller.AllowWatermarks,
                            log,
                            cancellationToken));
                    totalDuration =
                        TimeSpan.FromMilliseconds(result.Sum(pi => (pi.Finish - pi.Start).TotalMilliseconds));
                    remainingToFill = targetTime - totalDuration - playoutItem.StartOffset;
                    if (remainingToFill > TimeSpan.Zero)
                    {
                        result.AddRange(
                            FallbackFillerForPad(
                                playoutBuilderState,
                                enumerators,
                                scheduleItem,
                                remainingToFill,
                                cancellationToken));
                    }

                    break;
            }
        }

        // fix times on each playout item
        DateTimeOffset currentTime = playoutItem.StartOffset;
        for (var i = 0; i < result.Count; i++)
        {
            PlayoutItem item = result[i];
            TimeSpan duration = item.Finish - item.Start;
            item.Start = currentTime.UtcDateTime;
            item.Finish = (currentTime + duration).UtcDateTime;
            currentTime = item.FinishOffset;
        }

        return result;
    }

    private static List<PlayoutItem> AddCountFiller(
        PlayoutBuilderState playoutBuilderState,
        IMediaCollectionEnumerator enumerator,
        int count,
        FillerKind fillerKind,
        bool allowWatermarks,
        CancellationToken cancellationToken)
    {
        var result = new List<PlayoutItem>();

        for (var i = 0; i < count; i++)
        {
            foreach (MediaItem mediaItem in enumerator.Current)
            {
                TimeSpan itemDuration = DurationForMediaItem(mediaItem);

                var playoutItem = new PlayoutItem
                {
                    MediaItemId = mediaItem.Id,
                    Start = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                    Finish = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) + itemDuration,
                    InPoint = TimeSpan.Zero,
                    OutPoint = itemDuration,
                    GuideGroup = playoutBuilderState.NextGuideGroup,
                    FillerKind = fillerKind,
                    DisableWatermarks = !allowWatermarks
                };

                result.Add(playoutItem);
                enumerator.MoveNext();
            }
        }

        return result;
    }

    private List<PlayoutItem> AddDurationFiller(
        PlayoutBuilderState playoutBuilderState,
        IMediaCollectionEnumerator enumerator,
        TimeSpan duration,
        FillerKind fillerKind,
        bool allowWatermarks,
        bool log,
        CancellationToken cancellationToken)
    {
        var result = new List<PlayoutItem>();

        TimeSpan remainingToFill = duration;
        while (enumerator.Current.IsSome && remainingToFill > TimeSpan.Zero &&
               remainingToFill >= enumerator.MinimumDuration)
        {
            foreach (MediaItem mediaItem in enumerator.Current)
            {
                TimeSpan itemDuration = DurationForMediaItem(mediaItem);

                if (remainingToFill - itemDuration >= TimeSpan.Zero)
                {
                    var playoutItem = new PlayoutItem
                    {
                        MediaItemId = mediaItem.Id,
                        Start = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                        Finish = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) + itemDuration,
                        InPoint = TimeSpan.Zero,
                        OutPoint = itemDuration,
                        GuideGroup = playoutBuilderState.NextGuideGroup,
                        FillerKind = fillerKind,
                        DisableWatermarks = !allowWatermarks
                    };

                    remainingToFill -= itemDuration;
                    result.Add(playoutItem);
                    enumerator.MoveNext();
                }
                else
                {
                    if (log)
                    {
                        _logger.LogDebug(
                            "Filler item is too long {FillerDuration:g} to fill {GapDuration:g}; skipping to next filler item",
                            itemDuration,
                            remainingToFill);
                    }

                    enumerator.MoveNext();
                }
            }
        }

        return result;
    }

    private static Option<PlayoutItem> FallbackFillerForPad(
        PlayoutBuilderState playoutBuilderState,
        Dictionary<CollectionKey, IMediaCollectionEnumerator> enumerators,
        ProgramScheduleItem scheduleItem,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (scheduleItem.FallbackFiller != null)
        {
            IMediaCollectionEnumerator enumerator =
                enumerators[CollectionKey.ForFillerPreset(scheduleItem.FallbackFiller)];

            foreach (MediaItem mediaItem in enumerator.Current)
            {
                var result = new PlayoutItem
                {
                    MediaItemId = mediaItem.Id,
                    Start = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc),
                    Finish = new DateTime(2020, 2, 1, 0, 0, 0, DateTimeKind.Utc) + duration,
                    InPoint = TimeSpan.Zero,
                    OutPoint = TimeSpan.Zero,
                    GuideGroup = playoutBuilderState.NextGuideGroup,
                    FillerKind = FillerKind.Fallback,
                    DisableWatermarks = !scheduleItem.FallbackFiller.AllowWatermarks
                };

                enumerator.MoveNext();

                return result;
            }
        }

        return None;
    }
}
