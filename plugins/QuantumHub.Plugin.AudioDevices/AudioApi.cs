namespace QuantumHub.Plugin.AudioDevices;

/// <summary>Звуковое устройство Windows.</summary>
/// <param name="Id">Постоянный id устройства.</param>
/// <param name="Name">Имя для показа, например "Speakers (Pico Streaming Speaker)".</param>
public sealed record AudioDevice(string Id, string Name);

/// <summary>Ответ devices.current и тело события audio.default.changed.</summary>
/// <param name="Playback">Текущие наушники или динамики.</param>
/// <param name="Recording">Текущий микрофон.</param>
public sealed record AudioCurrent(AudioDevice? Playback, AudioDevice? Recording);

/// <summary>Ответ devices.list.</summary>
/// <param name="Playback">Устройства вывода.</param>
/// <param name="Recording">Устройства ввода.</param>
public sealed record AudioList(IReadOnlyList<AudioDevice> Playback, IReadOnlyList<AudioDevice> Recording);

/// <summary>Запрос devices.set.</summary>
/// <param name="Id">Какое устройство сделать устройством по умолчанию.</param>
public sealed record SetDefaultRequest(string Id);

/// <summary>Приложение, которое сейчас работает со звуком. Элемент ответа sessions.list.</summary>
/// <param name="Process">Имя процесса.</param>
/// <param name="Volume">Громкость этого приложения в микшере, от 0 до 1.</param>
/// <param name="Peak">Текущий уровень звука, от 0 до 1. Больше нуля значит, что звук правда идёт.</param>
/// <param name="Muted">Отключён ли звук приложения.</param>
public sealed record AudioSession(string Process, double Volume, double Peak, bool Muted);

/// <summary>Запрос sessions.volume.</summary>
/// <param name="Process">Имя процесса.</param>
/// <param name="Volume">Новая громкость, от 0 до 1.</param>
/// <param name="FadeMs">За сколько плавно к ней прийти. 0 значит сразу.</param>
public sealed record SetSessionVolumeRequest(string Process, double Volume, int FadeMs);

/// <summary>Запрос mics.levels.</summary>
/// <param name="Ids">
/// Какие микрофоны слушать. Уровень виден только при открытом потоке записи, поэтому каждый id из списка занимает
/// микрофон, пока его спрашивают. Пусто значит просто показать список, ничего не открывая.
/// </param>
public sealed record MicLevelsRequest(IReadOnlyList<string>? Ids);

/// <summary>Микрофон с текущим уровнем. Элемент ответа mics.levels.</summary>
/// <param name="Id">Постоянный id устройства.</param>
/// <param name="Name">Имя для показа.</param>
/// <param name="Bluetooth">Подключён ли по Bluetooth. Открытый поток переводит такую гарнитуру в режим HFP.</param>
/// <param name="IsDefault">Микрофон ли это по умолчанию.</param>
/// <param name="Peak">Текущий уровень от 0 до 1. Ноль, если микрофон не слушается.</param>
/// <param name="Listening">Открыт ли поток записи, то есть настоящий ли уровень в <paramref name="Peak"/>.</param>
public sealed record MicLevel(string Id, string Name, bool Bluetooth, bool IsDefault, double Peak, bool Listening);
