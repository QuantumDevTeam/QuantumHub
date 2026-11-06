using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Contracts.Proto;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.Music;

/// <summary>Текущий трек. Ответ player.current и тело события media.player.changed.</summary>
/// <param name="Title">Название.</param>
/// <param name="Artist">Исполнитель.</param>
/// <param name="Album">Альбом.</param>
/// <param name="App">Приложение.</param>
/// <param name="State">"playing", "paused" или "stopped".</param>
/// <param name="PositionSec">Текущая позиция.</param>
/// <param name="DurationSec">Длительность.</param>
/// <param name="HasThumbnail">Есть ли обложка, её отдаёт player.thumbnail.</param>
public sealed record TrackInfo(
    string Title,
    string Artist,
    string Album,
    string App,
    string State,
    double PositionSec,
    double DurationSec,
    bool HasThumbnail);

/// <summary>
/// Показывает трек и приглушает музыку. Здесь только связь с хабом: что считать событием, решает
/// <see cref="MusicTracker"/>, а громкостью управляет <see cref="DuckEngine"/>.
/// </summary>
[HubPlugin]
public sealed class MusicPlugin : IHubPlugin, IUiProvider
{
    /// <summary>Топик смены трека или состояния. Тело: <see cref="TrackInfo"/> в JSON, либо пусто, если плеера нет.</summary>
    public const string ChangedTopic = "media.player.changed";

    private const string NotificationKey = "music";

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DuckInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>Пауза после сигнала: при смене трека система присылает пачку событий, и данные в них собираются не сразу.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(350);

    private readonly MusicTracker _tracker = new();
    private readonly SmtcMediaSource _source = new();
    private readonly SemaphoreSlim _changed = new(0);
    private IPluginContext _context = null!;
    private DuckEngine _duck = null!;
    private byte[]? _thumbnail;
    private string? _thumbnailFor;
    private CancellationTokenSource? _stop;
    private Task? _loop;
    private Task? _duckLoop;

    /// <inheritdoc/>
    public async Task StartAsync(IPluginContext context, CancellationToken token)
    {
        _context = context;
        _duck = new DuckEngine(new BusAudioSessions(context.Bus), new ConfigDuckMemory(context.Config), ReadDuckOptions);

        context.Bus.Handle("player.current", (_, _) => ValueTask.FromResult(HubPayload.Json(CurrentTrack())));
        context.Bus.Handle("player.thumbnail", (_, _) => ValueTask.FromResult(_thumbnail is null ? HubPayload.Empty : HubPayload.Bytes(_thumbnail)));

        _source.Changed += OnSourceChanged;
        await _source.StartAsync();

        // Хаб мог упасть, пока музыка была приглушена.
        await TryAsync(_duck.ReleaseAsync);

        _stop = new CancellationTokenSource();
        _loop = Task.Run(() => TrackLoopAsync(_stop.Token), CancellationToken.None);
        _duckLoop = Task.Run(() => DuckLoopAsync(_stop.Token), CancellationToken.None);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken token)
    {
        _source.Changed -= OnSourceChanged;
        if (_stop is not null) await _stop.CancelAsync();
        if (_loop is not null) await _loop;
        if (_duckLoop is not null) await _duckLoop;
        await TryAsync(_duck.ReleaseAsync);
        _source.Dispose();
    }

    private void OnSourceChanged()
    {
        if (_changed.CurrentCount == 0) _changed.Release();
    }

    private async Task TrackLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                // Система сообщает не обо всём, поэтому кроме событий есть и обычная проверка.
                if (await _changed.WaitAsync(PollInterval, token)) await Task.Delay(SettleDelay, token);
                await TryAsync(RefreshAsync);
            }
        }
        catch (OperationCanceledException)
        {
            // Плагин останавливается.
        }
    }

    private async Task DuckLoopAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(DuckInterval, token);
                if (_context.Config.Get("ducking", true) || _duck.IsDucked)
                    await TryAsync(() => _duck.TickAsync(_tracker.Current?.State == PlaybackState.Playing));

                _context.Ui.Set("duck", _duck.IsDucked ? $"приглушена из-за {_duck.Trigger ?? "другого приложения"}" : "обычная громкость");
            }
        }
        catch (OperationCanceledException)
        {
            // Плагин останавливается.
        }
    }

    /// <summary>Без зависимости audio.devices приглушения просто нет: всё остальное продолжает работать.</summary>
    private async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is HubCallException or System.Runtime.InteropServices.COMException)
        {
            _context.Logger.Debug(ex.Message);
        }
    }

    private async Task RefreshAsync()
    {
        var previous = _tracker.Current;
        var sessions = await _source.ReadAsync();
        var apps = List("musicApps");
        var change = _tracker.Update(sessions, apps, DateTimeOffset.UtcNow);
        var current = _tracker.Current;

        var key = current is null ? null : $"{current.AppId}|{current.Title}|{current.Artist}";
        if (key != _thumbnailFor)
        {
            _thumbnail = current is null ? null : await _source.ReadThumbnailAsync(current.AppId);
            _thumbnailFor = _thumbnail is null ? null : key;
        }

        PushState();
        if (change != MusicChange.None || current?.State != previous?.State)
            _ = _context.Bus.PublishAsync(ChangedTopic, CurrentTrack() is { } track ? HubPayload.Json(track) : HubPayload.Empty);

        if (change is not (MusicChange.None or MusicChange.Stopped) && current is not null) await NotifyAsync(current, change);
    }

    private async Task NotifyAsync(MediaSession track, MusicChange change)
    {
        var config = _context.Config;
        var wanted = change is MusicChange.Paused or MusicChange.Resumed ? config.Get("notifyPause", true) : config.Get("notifications", true);
        if (!wanted) return;

        var album = config.Get("showAlbum", true) ? track.Album : string.Empty;
        var details = string.Join(" · ", new[] { track.Artist, album }.Where(part => part.Length > 0));
        await _context.Bus.CallAsync("cap:ui.notifications", "notify.show", HubPayload.Json(new
        {
            key = NotificationKey,
            title = track.Title,
            subtitle = details,
            imageBase64 = _thumbnail is null ? null : Convert.ToBase64String(_thumbnail),
            badge = change == MusicChange.Paused ? "pause" : null,
        }));
    }

    private TrackInfo? CurrentTrack()
    {
        if (_tracker.Current is not { } track) return null;

        return new TrackInfo(track.Title, track.Artist, track.Album, track.AppId, track.State.ToString().ToLowerInvariant(),
            track.PositionAt(DateTimeOffset.UtcNow), track.DurationSec, _thumbnail is not null);
    }

    private DuckOptions ReadDuckOptions()
    {
        var config = _context.Config;
        return new DuckOptions(
            config.Get("ducking", true),
            config.Get("duckMode", "whitelist") == "blacklist",
            List("duckApps"),
            List("duckIgnore"),
            List("musicApps"),
            config.Get("duckVolume", 15d) / 100,
            (int)config.Get("duckFadeMs", 150d),
            config.Get("duckThreshold", 1d) / 100,
            ReleaseTicks: (int)Math.Ceiling(config.Get("duckReleaseSeconds", 3d) / DuckInterval.TotalSeconds));
    }

    private List<string> List(string key) => _context.Config.Get(key, new List<string>())!;

    private void PushState()
    {
        var track = _tracker.Current;
        _context.Ui.Set("track", track is null ? "плеер не найден" : $"{track.Artist} — {track.Title}");
        _context.Ui.Set("state", track?.State switch
        {
            PlaybackState.Playing => "играет",
            PlaybackState.Paused => "на паузе",
            _ => "остановлено",
        });
        _context.Ui.Set("app", track?.AppId ?? "—");
    }

    /// <inheritdoc/>
    public IEnumerable<UiPage> GetPages()
    {
        var known = _source.ReadAsync().GetAwaiter().GetResult();
        _context.Ui.Set("sessions", string.Join('\n', known.Select(session => $"{session.AppId}: {session.Title}")));

        yield return Ui.Page("main", "Сейчас", "E8D6",
            Ui.KeyValue("Трек", "track"),
            Ui.KeyValue("Состояние", "state"),
            Ui.KeyValue("Приложение", "app"),
            Ui.KeyValue("Громкость", "duck"),
            Ui.Text("Что система видит сейчас", "subtitle"),
            Ui.Text("Отсюда можно взять имя для настройки «Музыкальные приложения».", "caption"),
            Ui.BoundText("sessions", "mono"));
    }

    /// <inheritdoc/>
    public void OnUiEvent(UiEvent uiEvent)
    {
    }

    /// <summary>Звук через возможность audio.devices.</summary>
    private sealed class BusAudioSessions(IHubBus bus) : IAudioSessions
    {
        private const string Target = "cap:audio.devices";

        public async Task<IReadOnlyList<AudioSession>> ListAsync() =>
            (await bus.CallAsync(Target, "sessions.list")).AsJson<List<AudioSession>>() ?? [];

        public async Task SetVolumeAsync(string process, double volume, int fadeMs) =>
            await bus.CallAsync(Target, "sessions.volume", HubPayload.Json(new { process, volume, fadeMs }));
    }

    /// <summary>Хранит прежнюю громкость в конфиге плагина.</summary>
    private sealed class ConfigDuckMemory(IPluginConfig config) : IDuckMemory
    {
        public string? Process
        {
            get => config.Get<string>("duckedProcess") is { Length: > 0 } value ? value : null;
            set => config.Set("duckedProcess", value ?? string.Empty);
        }

        public double? Volume
        {
            get => config.Get<double?>("duckedVolume");
            set => config.Set("duckedVolume", value);
        }
    }
}
