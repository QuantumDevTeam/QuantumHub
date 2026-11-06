namespace QuantumHub.Core;

/// <summary>
/// Где что лежит. Файлы программы остаются в папке установки, которая может быть где угодно, а всё
/// пользовательское лежит в профиле пользователя.
/// </summary>
public sealed class HubPaths
{
    /// <summary>Вычисляет пути и создаёт папки данных.</summary>
    /// <param name="rootDir">Папка программы.</param>
    /// <param name="dataDir">Папка данных. По умолчанию %APPDATA%\QuantumHub.</param>
    public HubPaths(string rootDir, string? dataDir = null)
    {
        RootDir = Path.GetFullPath(rootDir);
        DataDir = Path.GetFullPath(dataDir ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "QuantumHub"));
        BundledPluginsDir = Path.Combine(RootDir, "plugins");
        UserPluginsDir = Path.Combine(DataDir, "plugins");
        ConfigDir = Path.Combine(DataDir, "config");
        PluginDataDir = Path.Combine(DataDir, "data");
        CacheDir = Path.Combine(DataDir, "cache");
        LogsDir = Path.Combine(DataDir, "logs");

        foreach (var dir in new[] { DataDir, UserPluginsDir, ConfigDir, PluginDataDir, CacheDir, LogsDir })
            Directory.CreateDirectory(dir);
    }

    /// <summary>Папка программы.</summary>
    public string RootDir { get; }
    /// <summary>Папка данных пользователя.</summary>
    public string DataDir { get; }
    /// <summary>Плагины рядом с программой: &lt;root&gt;/plugins.</summary>
    public string BundledPluginsDir { get; }
    /// <summary>Плагины в профиле пользователя.</summary>
    public string UserPluginsDir { get; }
    /// <summary>Конфиги плагинов и самого хаба.</summary>
    public string ConfigDir { get; }
    /// <summary>Личные данные плагинов.</summary>
    public string PluginDataDir { get; }
    /// <summary>Кеши плагинов.</summary>
    public string CacheDir { get; }
    /// <summary>Журналы.</summary>
    public string LogsDir { get; }

    /// <summary>Все папки, где ищутся плагины. Из двух одинаковых версий выигрывает пользовательская.</summary>
    public IEnumerable<string> PluginDirs => [BundledPluginsDir, UserPluginsDir];

    /// <summary>
    /// Выбирает, куда ставить новые плагины: рядом с программой, если туда можно писать, как в портативной
    /// установке. В Program Files писать нельзя, и тогда используется папка пользователя.
    /// </summary>
    /// <returns>Путь к папке плагинов.</returns>
    public string InstallTargetDir()
    {
        try
        {
            Directory.CreateDirectory(BundledPluginsDir);
            var probe = Path.Combine(BundledPluginsDir, $".probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return BundledPluginsDir;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return UserPluginsDir;
        }
    }
}
