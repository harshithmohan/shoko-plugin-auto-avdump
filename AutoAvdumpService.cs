using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Metadata.Anidb.Enums;
using Shoko.Abstractions.Metadata.Anidb.Events;
using Shoko.Abstractions.Metadata.Anidb.Services;
using Shoko.Abstractions.Metadata.Events;
using Shoko.Abstractions.Metadata.Services;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Events;
using Shoko.Abstractions.Video.Services;

namespace Shoko.Plugin.AutoAVDump;

/// <summary>
///   Stages videos whose automatic release search failed and dumps them
///   through the server's built-in AVDump, in batches. Dumping — registering
///   a file's hashes and technical metadata with AniDB — is the first of the
///   two steps of file registration; adding the dumped file to a series/
///   episode is still manual.
/// </summary>
public class AutoAvdumpService : IHostedService
{
    /// <summary>
    ///   How many failed AVDump attempts a video gets before the plugin
    ///   stops trying for it.
    /// </summary>
    private const int MaxFailures = 3;

    /// <summary>
    ///   How long a staged video waits before the batch is submitted.
    /// </summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    ///   The most videos scheduled in one flush. A flush becomes one AVDump
    ///   session that dumps files sequentially, so this bounds session
    ///   length and blast radius. Staged videos beyond the cap are flushed
    ///   on later intervals.
    /// </summary>
    private const int MaxBatchSize = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly ILogger<AutoAvdumpService> _logger;
    private readonly IVideoService _videoService;
    private readonly IVideoReleaseService _releaseService;
    private readonly IMetadataService _metadataService;
    private readonly IAnidbAvdumpService _avdumpService;
    private readonly IApplicationPaths _applicationPaths;

    private readonly object _stateLock = new();
    private readonly ConcurrentDictionary<int, IVideo> _pending = new();
    private readonly ConcurrentDictionary<int, int[]> _sessions = new();
    private readonly HashSet<int> _submitted = new();
    private State _state = new();
    private string _stateFilePath = string.Empty;
    private CancellationTokenSource? _cancellation;
    private Task? _flushLoop;

    /// <summary>
    ///   Initializes a new instance of the <see cref="AutoAvdumpService"/> class.
    /// </summary>
    public AutoAvdumpService(
        ILogger<AutoAvdumpService> logger,
        IVideoService videoService,
        IVideoReleaseService releaseService,
        IMetadataService metadataService,
        IAnidbAvdumpService avdumpService,
        IApplicationPaths applicationPaths)
    {
        _logger = logger;
        _videoService = videoService;
        _releaseService = releaseService;
        _metadataService = metadataService;
        _avdumpService = avdumpService;
        _applicationPaths = applicationPaths;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stateFilePath = Path.Combine(_applicationPaths.ConfigurationsPath, "auto-avdump-state.json");
        LoadState();

        _cancellation = new CancellationTokenSource();
        _releaseService.SearchCompleted += OnSearchCompleted;
        _metadataService.EpisodeAdded += OnEpisodeAdded;
        _avdumpService.AvdumpEvent += OnAvdumpEvent;
        _flushLoop = Task.Run(() => RunFlushLoopAsync(_cancellation.Token));

        _logger.LogInformation(
            "Auto AVDump is active. Videos that fail an automatic release search are dumped to AniDB through AVDump.");
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _releaseService.SearchCompleted -= OnSearchCompleted;
        _metadataService.EpisodeAdded -= OnEpisodeAdded;
        _avdumpService.AvdumpEvent -= OnAvdumpEvent;
        _cancellation?.Cancel();

        return _flushLoop ?? Task.CompletedTask;
    }

    private async Task RunFlushLoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(FlushInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try
                {
                    await FlushPendingAsync();
                }
                catch (Exception ex)
                {
                    // The batch stays in _pending and is retried next tick.
                    _logger.LogError(ex, "The AVDump flush failed. The staged videos will be retried on the next interval.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    private void OnSearchCompleted(object? sender, VideoReleaseSearchCompletedEventArgs e)
    {
        if (!e.IsAutomatic || e.IsSuccessful)
            return;

        var video = _videoService.GetVideoByID(e.Video.ID);
        if (video is null || video.IsIgnored || video.Episodes.Count > 0 || ShouldSkip(video.ID))
            return;

        _pending[video.ID] = video;
        _logger.LogDebug("Staged video {VideoID} for AVDump after a failed automatic release search.", video.ID);
    }

    private void OnEpisodeAdded(object? sender, EpisodeInfoUpdatedEventArgs e)
    {
        // The video was recognized some other way, so it no longer needs
        // the AVDump route. The flush re-checks this, so a missed event
        // here is harmless.
        foreach (var video in e.EpisodeInfo.Videos)
        {
            if (_pending.TryRemove(video.ID, out _))
                _logger.LogDebug("Dropped video {VideoID} from the AVDump batch. It is now linked to an episode.", video.ID);
        }
    }

    private async Task FlushPendingAsync()
    {
        if (_pending.IsEmpty)
            return;

        var videos = new List<IVideo>();
        foreach (var videoID in _pending.Keys)
        {
            var video = _videoService.GetVideoByID(videoID);
            if (video is null || video.IsIgnored || video.Episodes.Count > 0 || ShouldSkip(video.ID))
            {
                _pending.TryRemove(videoID, out _);
                continue;
            }

            videos.Add(video);
        }

        if (videos.Count == 0)
            return;

        var batch = videos.Count <= MaxBatchSize ? videos : videos.Take(MaxBatchSize).ToList();

        if (!EnsureAvdumpInstalled())
            return;

        lock (_stateLock)
        {
            foreach (var video in batch)
                _submitted.Add(video.ID);
        }

        try
        {
            await _avdumpService.ScheduleAvdumpVideos([.. batch]);

            foreach (var video in batch)
                _pending.TryRemove(video.ID, out _);

            if (batch.Count == videos.Count)
            {
                _logger.LogInformation(
                    "Scheduled AVDump for {Count} video(s): {VideoIDs}.",
                    batch.Count,
                    string.Join(", ", batch.Select(x => x.ID)));
            }
            else
            {
                _logger.LogInformation(
                    "Scheduled AVDump for {Count} of {Total} staged video(s): {VideoIDs}. The remaining {Remaining} stay staged for later flushes.",
                    batch.Count,
                    videos.Count,
                    string.Join(", ", batch.Select(x => x.ID)),
                    videos.Count - batch.Count);
            }
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                foreach (var video in batch)
                    _submitted.Remove(video.ID);
            }

            _logger.LogError(
                ex,
                "Scheduling AVDump failed. The {Count} staged video(s) will be retried on the next interval.",
                batch.Count);
        }
    }

    private bool EnsureAvdumpInstalled()
    {
        if (_avdumpService.IsAvdumpInstalled)
            return true;

        try
        {
            _logger.LogInformation("AVDump is not installed. Attempting to install it before the next flush.");
            if (!_avdumpService.UpdateAvdump())
            {
                _logger.LogWarning("The AVDump install reported failure. Skipping this batch until a later flush.");
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Installing AVDump threw. Skipping this batch until a later flush.");
            return false;
        }

        if (!_avdumpService.IsAvdumpInstalled)
        {
            _logger.LogWarning("AVDump is still not installed after the update attempt. Skipping this batch until a later flush.");
            return false;
        }

        _logger.LogInformation("AVDump {Version} is installed.", _avdumpService.InstalledAvdumpVersion);
        return true;
    }

    private void OnAvdumpEvent(object? sender, AnidbAvdumpEventArgs e)
    {
        switch (e.Type)
        {
            case AnidbAvdumpEventType.Started when e.SessionID is { } startedSession && e.VideoIDs is not null:
                _sessions[startedSession] = [.. e.VideoIDs];
                break;

            case AnidbAvdumpEventType.Success when e.VideoIDs is not null:
                if (e.SessionID is { } successSession)
                    _sessions.TryRemove(successSession, out _);
                HandleSessionOutcome(e.VideoIDs, success: true, e);
                break;

            case AnidbAvdumpEventType.Failure when e.VideoIDs is not null:
                if (e.SessionID is { } failureSession)
                    _sessions.TryRemove(failureSession, out _);
                HandleSessionOutcome(e.VideoIDs, success: false, e);
                break;

            case AnidbAvdumpEventType.GenericException when e.SessionID is { } exceptionSession:
                if (_sessions.TryRemove(exceptionSession, out var sessionVideos))
                    HandleSessionOutcome(sessionVideos, success: false, e);
                else
                    _logger.LogWarning(
                        "An AVDump session this plugin does not track ended with an exception: {Message}",
                        e.Exception?.Message ?? e.Message);
                break;

            case AnidbAvdumpEventType.Timeout:
                _logger.LogWarning("AVDump reported a timeout: {Message}", e.Message ?? e.ErrorMessage);
                break;

            case AnidbAvdumpEventType.MissingApiKey:
                var abandoned = AbandonSubmitted();
                _logger.LogWarning(
                    "AVDump cannot run: the AniDB AVDump API key is missing from the server settings. {Count} staged video(s) were set aside and a later automatic search will stage them again.",
                    abandoned);
                break;

            case AnidbAvdumpEventType.InvalidCredentials:
                AbandonSubmitted();
                _logger.LogWarning(
                    "AVDump could not authenticate with AniDB. Check the AniDB user name and AVDump API key in the server settings (Settings -> AniDB).");
                break;

            case AnidbAvdumpEventType.InstallException:
                AbandonSubmitted();
                _logger.LogError(
                    e.Exception,
                    "Installing AVDump failed. {Message}",
                    e.Message);
                break;
        }
    }

    private void HandleSessionOutcome(IReadOnlyCollection<int> videoIDs, bool success, AnidbAvdumpEventArgs e)
    {
        var affected = new List<int>();

        lock (_stateLock)
        {
            foreach (var videoID in videoIDs)
            {
                // Only videos this plugin staged count. The server can also
                // start AVDump sessions from the UI or the queue, and those
                // are nobody's business here.
                if (!_submitted.Remove(videoID))
                    continue;

                affected.Add(videoID);

                if (success)
                {
                    _state.Dumped[videoID] = true;
                    _state.Failures.Remove(videoID);
                }
                else
                {
                    var failures = _state.Failures.GetValueOrDefault(videoID) + 1;
                    if (failures >= MaxFailures)
                    {
                        _state.Failures.Remove(videoID);
                        _state.GivenUp.Add(videoID);
                        _logger.LogInformation("Giving up on video {VideoID} after {Failures} failed AVDump attempts.", videoID, failures);
                    }
                    else
                    {
                        _state.Failures[videoID] = failures;
                    }
                }
            }
        }

        if (affected.Count == 0)
            return;

        SaveState();

        if (success)
        {
            _logger.LogInformation(
                "AVDump succeeded for {Count} video(s): {VideoIDs}. They are now dumped; adding them to AniDB (a series/episode) is the manual second step.",
                affected.Count,
                string.Join(", ", affected));
        }
        else
        {
            _logger.LogWarning(
                "AVDump failed for {Count} video(s): {VideoIDs}. {Error}",
                affected.Count,
                string.Join(", ", affected),
                e.ErrorMessage ?? e.Message ?? e.Exception?.Message);
        }
    }

    private int AbandonSubmitted()
    {
        lock (_stateLock)
        {
            var count = _submitted.Count;
            _submitted.Clear();
            return count;
        }
    }

    private bool ShouldSkip(int videoID)
    {
        lock (_stateLock)
        {
            return _state.Dumped.ContainsKey(videoID)
                   || _state.GivenUp.Contains(videoID)
                   || _state.Failures.GetValueOrDefault(videoID) >= MaxFailures;
        }
    }

    private void LoadState()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
                return;

            var loaded = JsonSerializer.Deserialize<State>(File.ReadAllText(_stateFilePath));
            if (loaded is not null)
                _state = loaded;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the AVDump state file at {Path}. Starting with an empty state.", _stateFilePath);
        }
    }

    private void SaveState()
    {
        State snapshot;
        lock (_stateLock)
        {
            snapshot = _state;
            PruneDeadVideos(snapshot);
        }

        try
        {
            var tempPath = _stateFilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(snapshot, SerializerOptions));
            File.Move(tempPath, _stateFilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not write the AVDump state file to {Path}.", _stateFilePath);
        }
    }

    private void PruneDeadVideos(State state)
    {
        foreach (var videoID in state.Dumped.Keys.ToList())
        {
            if (_videoService.GetVideoByID(videoID) is null)
                state.Dumped.Remove(videoID);
        }

        foreach (var videoID in state.Failures.Keys.ToList())
        {
            if (_videoService.GetVideoByID(videoID) is null)
                state.Failures.Remove(videoID);
        }

        state.GivenUp.RemoveAll(videoID => _videoService.GetVideoByID(videoID) is null);
    }

    /// <summary>
    ///   What is persisted across restarts.
    /// </summary>
    private sealed class State
    {
        /// <summary>
        ///   Videos AVDump accepted. They are never re-dumped.
        /// </summary>
        public Dictionary<int, bool> Dumped { get; set; } = [];

        /// <summary>
        ///   How many times AVDump has failed for each video.
        /// </summary>
        public Dictionary<int, int> Failures { get; set; } = [];

        /// <summary>
        ///   Videos the plugin gave up on after too many failures.
        /// </summary>
        public List<int> GivenUp { get; set; } = [];
    }
}
