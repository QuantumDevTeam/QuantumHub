namespace QuantumHub.Plugin.MicSwitcher;

/// <summary>Что делать с найденным сигналом.</summary>
public enum MicMode
{
    /// <summary>Ничего: микрофоны не заняты.</summary>
    Off,

    /// <summary>Показать подсказку и переключить по щелчку.</summary>
    Hint,

    /// <summary>Переключить сразу.</summary>
    Auto,
}

/// <summary>Микрофон, каким его отдаёт возможность audio.devices. Общий у плагинов только формат JSON.</summary>
/// <param name="Id">Постоянный id устройства.</param>
/// <param name="Name">Имя для показа.</param>
/// <param name="Bluetooth">Подключён ли по Bluetooth.</param>
/// <param name="IsDefault">Микрофон ли это по умолчанию.</param>
/// <param name="Peak">Уровень от 0 до 1.</param>
/// <param name="Listening">Настоящий ли это уровень: открыт ли поток записи.</param>
public sealed record MicLevel(string Id, string Name, bool Bluetooth, bool IsDefault, double Peak, bool Listening);

/// <summary>Микрофон с решением плагина. Элемент ответа mic.levels.</summary>
/// <param name="Id">Постоянный id устройства.</param>
/// <param name="Name">Имя для показа.</param>
/// <param name="Bluetooth">Подключён ли по Bluetooth.</param>
/// <param name="IsDefault">Микрофон ли это по умолчанию.</param>
/// <param name="Peak">Уровень от 0 до 1.</param>
/// <param name="Listening">Настоящий ли это уровень: открыт ли поток записи.</param>
/// <param name="Excluded">Почему на него нельзя переключать: одно из значений <see cref="MicFilter"/> или <see langword="null"/>.</param>
public sealed record MicState(string Id, string Name, bool Bluetooth, bool IsDefault, double Peak, bool Listening, string? Excluded);

/// <summary>
/// Что движку нужно от звука. В работе за этим стоит возможность audio.devices другого плагина, а в тестах подделка,
/// поэтому настоящие микрофоны они не трогают.
/// </summary>
public interface IMicAudio
{
    /// <summary>Все активные микрофоны с уровнями.</summary>
    /// <param name="listen">Какие слушать. Каждый из них останется занят, пока его спрашивают.</param>
    Task<IReadOnlyList<MicLevel>> ReadAsync(IReadOnlyList<string> listen);

    /// <summary>Делает микрофон микрофоном по умолчанию.</summary>
    /// <param name="deviceId">Id устройства.</param>
    Task SetDefaultAsync(string deviceId);
}

/// <summary>Подсказка пользователю. В работе за этим стоит возможность ui.notifications.</summary>
public interface IMicPrompt
{
    /// <summary>Предлагает переключиться.</summary>
    /// <param name="mic">На какой микрофон.</param>
    /// <param name="duration">Сколько показывать.</param>
    Task ShowAsync(MicState mic, TimeSpan duration);

    /// <summary>Убирает устаревшую подсказку.</summary>
    Task HideAsync();
}

/// <summary>Идёт ли VR-сессия.</summary>
public interface IVrState
{
    /// <summary>Идёт ли она прямо сейчас.</summary>
    bool Active { get; }
}

/// <summary>Настройки, которые движок читает на каждом шаге.</summary>
public sealed record MicSwitchOptions
{
    /// <summary>Что делать с сигналом.</summary>
    public MicMode Mode { get; init; } = MicMode.Off;

    /// <summary>С какого уровня, от 0 до 1, считать сигнал настоящим.</summary>
    public double Threshold { get; init; } = 0.1;

    /// <summary>Сколько сигнал должен держаться. Щелчок и стук по столу столько не живут.</summary>
    public TimeSpan Hold { get; init; } = TimeSpan.FromSeconds(1.2);

    /// <summary>Какая пауза ещё не обрывает сигнал: в речи они есть всегда.</summary>
    public TimeSpan Gap { get; init; } = TimeSpan.FromSeconds(0.5);

    /// <summary>Сколько не вмешиваться после переключения, подсказки или чужой смены микрофона.</summary>
    public TimeSpan Cooldown { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Сколько показывать подсказку.</summary>
    public TimeSpan HintTime { get; init; } = TimeSpan.FromSeconds(12);

    /// <summary>Переключать только если на текущем микрофоне тихо. Иначе рядом стоящие микрофоны будут спорить.</summary>
    public bool RequireCurrentSilent { get; init; } = true;

    /// <summary>Слушать ли Bluetooth-микрофоны. Открытый поток портит звук в такой гарнитуре.</summary>
    public bool WatchBluetooth { get; init; }

    /// <summary>Если не пуст, то на другие микрофоны не переключать. Подходит часть имени.</summary>
    public IReadOnlyList<string> Whitelist { get; init; } = [];

    /// <summary>Эти микрофоны не слушать и на них не переключать. Подходит часть имени.</summary>
    public IReadOnlyList<string> Blacklist { get; init; } = [];
}
