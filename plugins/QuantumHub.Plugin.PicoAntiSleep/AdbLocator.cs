namespace QuantumHub.Plugin.PicoAntiSleep;

/// <summary>Ищет adb.exe.</summary>
public static class AdbLocator
{
    private const string FileName = "adb.exe";

    /// <summary>Настройка, потом папка плагина, потом Android SDK, потом PATH.</summary>
    /// <param name="configured">Путь из настройки. Можно указать и папку.</param>
    /// <param name="pluginDir">Папка плагина.</param>
    /// <returns>Путь или <see langword="null"/>.</returns>
    public static string? Find(string? configured, string pluginDir)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var path = configured.Trim().Trim('"');
            candidates.Add(path);
            candidates.Add(Path.Combine(path, FileName));
        }

        candidates.Add(Path.Combine(pluginDir, "adb", FileName));
        candidates.Add(Path.Combine(pluginDir, FileName));

        foreach (var variable in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } sdk)
                candidates.Add(Path.Combine(sdk, "platform-tools", FileName));
        }

        var searchPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        candidates.AddRange(searchPath.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(dir => Path.Combine(dir.Trim(), FileName)));

        return candidates.FirstOrDefault(File.Exists);
    }
}
