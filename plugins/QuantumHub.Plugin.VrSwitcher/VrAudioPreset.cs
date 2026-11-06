namespace QuantumHub.Plugin.VrSwitcher;

/// <summary>Как найти устройства гарнитуры.</summary>
/// <param name="Name">Название пресета.</param>
/// <param name="Playback">Части имени устройства вывода, по порядку предпочтения.</param>
/// <param name="Recording">Части имени устройства ввода, по порядку предпочтения.</param>
public sealed record VrAudioPreset(string Name, IReadOnlyList<string> Playback, IReadOnlyList<string> Recording)
{
    /// <summary>
    /// Pico Connect / Streaming Assistant. Устройства называются "Pico Streaming Speaker" и
    /// "PicoStreamingMicrophone", поэтому пробелы при поиске не учитываются.
    /// </summary>
    public static VrAudioPreset Pico { get; } = new("Pico", ["Pico Streaming"], ["Pico Streaming"]);

    /// <summary>Meta Quest Link и Air Link. Устройства приложения Meta Quest Link.</summary>
    public static VrAudioPreset Meta { get; } = new("Meta",
        ["Oculus Virtual Audio", "Meta Virtual Audio"],
        ["Oculus Virtual Audio", "Meta Virtual Audio"]);

    /// <summary>Выбирает пресет по значению настройки.</summary>
    /// <param name="name">"Pico", "Meta" или "Custom".</param>
    /// <param name="customPlayback">Часть имени устройства вывода для Custom.</param>
    /// <param name="customRecording">Часть имени устройства ввода для Custom.</param>
    public static VrAudioPreset Resolve(string? name, string? customPlayback, string? customRecording) => name switch
    {
        "Meta" => Meta,
        "Custom" => new VrAudioPreset("Custom", Names(customPlayback), Names(customRecording)),
        _ => Pico,
    };

    private static string[] Names(string? value) => string.IsNullOrWhiteSpace(value) ? [] : [value];

    /// <summary>
    /// Находит устройство по частям имени. Регистр и пробелы не важны, а порядок частей задаёт предпочтение.
    /// </summary>
    /// <param name="devices">Среди кого искать.</param>
    /// <param name="names">Части имени.</param>
    /// <returns>Устройство или <see langword="null"/>.</returns>
    public static AudioDevice? Find(IEnumerable<AudioDevice> devices, IEnumerable<string> names)
    {
        var list = devices.ToList();
        foreach (var name in names)
        {
            var wanted = Normalize(name);
            if (wanted.Length == 0) continue;

            var match = list.FirstOrDefault(device => Normalize(device.Name).Contains(wanted, StringComparison.Ordinal));
            if (match is not null) return match;
        }

        return null;
    }

    private static string Normalize(string text) => text.Replace(" ", string.Empty).ToLowerInvariant();
}
