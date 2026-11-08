using QuantumHub.Contracts.Ui;

namespace QuantumHub.Plugin.MicSwitcher;

/// <summary>
/// Логика плагина: когда считать, что человек заговорил в другой микрофон, и что с этим делать. С внешним миром работает
/// только через интерфейсы, поэтому полностью проверяется тестами.
/// </summary>
/// <remarks>
/// <para>
/// Переключение возможно, только если на другом микрофоне устойчивый сигнал, а на текущем всё это время тихо. Поэтому два
/// микрофона рядом не будут спорить: человек говорит сразу в оба, и переключаться незачем.
/// </para>
/// <para>
/// Занимать микрофоны можно только когда это нужно: пока режим выключен или идёт VR, движок не просит слушать ничего. Страница
/// плагина просит об этом сама и только пока открыта. Список id собирается по уже известным микрофонам, поэтому первое же
/// обращение ничего не открывает и Bluetooth не будет разбужен даже на мгновение.
/// </para>
/// </remarks>
/// <param name="audio">Микрофоны.</param>
/// <param name="prompt">Подсказка.</param>
/// <param name="vr">VR-сессия.</param>
/// <param name="options">Текущие настройки.</param>
/// <param name="clock">Часы.</param>
/// <param name="log">Куда писать, что произошло.</param>
public sealed class MicSwitchEngine(
    IMicAudio audio,
    IMicPrompt prompt,
    IVrState vr,
    Func<MicSwitchOptions> options,
    TimeProvider clock,
    Action<string> log)
{
    /// <summary>Сколько страница считается открытой после последнего сигнала от неё. Окно могло упасть, не попрощавшись.</summary>
    private static readonly TimeSpan ViewLease = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, SignalTracker> _trackers = [];
    private DateTimeOffset _hintUntil;
    private DateTimeOffset _calmUntil;
    private long _viewedUntilTicks;
    private string? _lastDefault;
    private bool _wasVr;

    /// <summary>Все известные микрофоны и их уровни.</summary>
    public IReadOnlyList<MicState> Mics { get; private set; } = [];

    /// <summary>Какой микрофон сейчас предложен. Подсказка ещё показывается.</summary>
    public MicState? Hint { get; private set; }

    /// <summary>Что сейчас происходит, для показа пользователю.</summary>
    public string Status { get; private set; } = Loc.T("Выключен", "Off");

    /// <summary>Что произошло последним.</summary>
    public string LastAction { get; private set; } = "—";

    /// <summary>Сменился статус или произошло что-то новое.</summary>
    public event Action? StatusChanged;

    /// <summary>Страница открыта: уровни нужны, даже если режим выключен. Вызывается, пока она видна.</summary>
    public void KeepListening() => Volatile.Write(ref _viewedUntilTicks, (clock.GetUtcNow() + ViewLease).UtcTicks);

    /// <summary>Страницу закрыли.</summary>
    public void StopListening() => Volatile.Write(ref _viewedUntilTicks, 0);

    /// <summary>Одна проверка. Вызывается по таймеру.</summary>
    public async Task PollAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var settings = options();
            var now = clock.GetUtcNow();
            var watching = IsWatching(settings);

            if (_wasVr && !vr.Active) _calmUntil = now + settings.Cooldown;
            _wasVr = vr.Active;

            if (!watching)
            {
                _trackers.Clear();
                _lastDefault = null;
                await CloseHintAsync();
            }

            if (watching || IsViewed(now))
            {
                await ReadAsync(settings, true);
                if (watching) await DecideAsync(settings, now);
            }
            else
            {
                Mics = Mics.Select(mic => mic with { Peak = 0, Listening = false }).ToList();
            }

            SetStatus(Describe(settings));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Обновляет список, не открывая ничего лишнего. Нужно, когда проверки стоят, а спросить надо.</summary>
    public async Task RefreshAsync()
    {
        await _gate.WaitAsync();
        try
        {
            var settings = options();
            await ReadAsync(settings, IsWatching(settings) || IsViewed(clock.GetUtcNow()));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Переключает по просьбе человека. Его слово сильнее любых списков и VR.</summary>
    /// <param name="deviceId">Id микрофона.</param>
    public async Task SwitchAsync(string deviceId)
    {
        await _gate.WaitAsync();
        try
        {
            var name = Mics.FirstOrDefault(mic => mic.Id == deviceId)?.Name ?? deviceId;
            await audio.SetDefaultAsync(deviceId);
            await OnSwitchedAsync(deviceId, name);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Человек нажал на подсказку. Устаревшая подсказка уже ничего не значит.</summary>
    public async Task AcceptHintAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (Hint is { } hint && clock.GetUtcNow() < _hintUntil && !vr.Active) await TrySwitchAsync(hint);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Человек отказался от подсказки.</summary>
    public async Task DismissHintAsync()
    {
        await _gate.WaitAsync();
        try
        {
            Hint = null;
            SetStatus(Describe(options()));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Убирает подсказку перед остановкой плагина.</summary>
    public async Task ShutdownAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await CloseHintAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsWatching(MicSwitchOptions settings) => settings.Mode != MicMode.Off && !vr.Active;

    private bool IsViewed(DateTimeOffset now) => now.UtcTicks < Volatile.Read(ref _viewedUntilTicks);

    private async Task ReadAsync(MicSwitchOptions settings, bool listen)
    {
        var ids = listen ? Mics.Where(mic => MicFilter.ShouldListen(mic, settings)).Select(mic => mic.Id).ToList() : [];
        var levels = await audio.ReadAsync(ids);
        Mics = levels.Select(mic => new MicState(mic.Id, mic.Name, mic.Bluetooth, mic.IsDefault, mic.Peak, mic.Listening,
            MicFilter.Exclusion(mic.Name, mic.Bluetooth, settings))).ToList();
    }

    private async Task DecideAsync(MicSwitchOptions settings, DateTimeOffset now)
    {
        Listen(settings, now);

        var current = Mics.FirstOrDefault(mic => mic.IsDefault);
        if (_lastDefault is not null && _lastDefault != current?.Id) _calmUntil = now + settings.Cooldown;
        _lastDefault = current?.Id;

        if (Hint is { } hint && (now >= _hintUntil || hint.Id == current?.Id || !IsAllowed(hint.Id))) await CloseHintAsync();
        if (Hint is not null || now < _calmUntil) return;

        if (settings.RequireCurrentSilent && current is { Listening: true } && !_trackers[current.Id].IsQuiet(now, settings.Hold)) return;

        var candidate = Mics
            .Where(mic => mic is { IsDefault: false, Listening: true, Excluded: null } && _trackers[mic.Id].IsSustained(now, settings.Hold))
            .MaxBy(mic => mic.Peak);
        if (candidate is null) return;

        // Сразу, а не после успеха: иначе сбой повторялся бы на каждой проверке.
        _calmUntil = now + settings.Cooldown;
        if (settings.Mode == MicMode.Auto) await TrySwitchAsync(candidate);
        else await OfferAsync(candidate, settings, now);
    }

    private void Listen(MicSwitchOptions settings, DateTimeOffset now)
    {
        foreach (var id in _trackers.Keys.Where(id => !Mics.Any(mic => mic.Id == id && mic.Listening)).ToList()) _trackers.Remove(id);

        foreach (var mic in Mics.Where(mic => mic.Listening))
        {
            if (!_trackers.TryGetValue(mic.Id, out var tracker)) _trackers[mic.Id] = tracker = new SignalTracker();
            tracker.Sample(now, mic.Peak >= settings.Threshold, settings.Gap);
        }
    }

    private bool IsAllowed(string id) => Mics.Any(mic => mic.Id == id && mic.Excluded is null);

    private async Task OfferAsync(MicState mic, MicSwitchOptions settings, DateTimeOffset now)
    {
        Hint = mic;
        _hintUntil = now + settings.HintTime;
        Remember(Loc.T($"Предложено переключить на «{mic.Name}»", $"Offered to switch to \"{mic.Name}\""));
        try
        {
            await prompt.ShowAsync(mic, settings.HintTime);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log(Loc.T($"Не удалось показать подсказку: {ex.Message}", $"Could not show the hint: {ex.Message}")); // Статус на странице всё равно о ней скажет.
        }
    }

    private async Task TrySwitchAsync(MicState mic)
    {
        try
        {
            await audio.SetDefaultAsync(mic.Id);
            await OnSwitchedAsync(mic.Id, mic.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log(Loc.T($"Не удалось переключить на {mic.Name}: {ex.Message}", $"Could not switch to {mic.Name}: {ex.Message}"));
            Remember(Loc.T($"Не удалось переключить на «{mic.Name}»", $"Could not switch to \"{mic.Name}\""));
        }
    }

    private async Task OnSwitchedAsync(string id, string name)
    {
        _calmUntil = clock.GetUtcNow() + options().Cooldown;
        _lastDefault = id;
        Mics = Mics.Select(mic => mic with { IsDefault = mic.Id == id }).ToList();
        await CloseHintAsync();

        log(Loc.T($"Микрофон: {name}", $"Microphone: {name}"));
        Remember(Loc.T($"Переключено на «{name}»", $"Switched to \"{name}\""));
    }

    private async Task CloseHintAsync()
    {
        if (Hint is null) return;

        Hint = null;
        try
        {
            await prompt.HideAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log(Loc.T($"Не удалось убрать подсказку: {ex.Message}", $"Could not remove the hint: {ex.Message}")); // Сама исчезнет по времени.
        }
    }

    private string Describe(MicSwitchOptions settings)
    {
        if (settings.Mode == MicMode.Off) return Loc.T("Выключен, микрофоны не заняты", "Off, microphones are not in use");
        if (vr.Active) return Loc.T("Идёт VR, плагин не вмешивается", "VR is running, the plugin stays out of it");
        if (Hint is { } hint) return Loc.T($"Сигнал на «{hint.Name}»", $"Signal on \"{hint.Name}\"");

        var count = Mics.Count(mic => mic.Listening);
        return Loc.T($"Слушаю микрофонов: {count}", $"Listening to microphones: {count}");
    }

    private void SetStatus(string status)
    {
        if (status == Status) return;

        Status = status;
        StatusChanged?.Invoke();
    }

    private void Remember(string action)
    {
        LastAction = action;
        StatusChanged?.Invoke();
    }
}
