namespace QuantumHub.Gui.Services;

/// <summary>
/// Очередь файлов плагинов, открытых из проводника. Второй запуск GUI кладёт сюда путь и будит первый,
/// поэтому файл можно открыть и тогда, когда программа уже работает в трее.
/// </summary>
public static class PendingInstalls
{
    /// <summary>Файл очереди.</summary>
    private static string QueuePath(string instance) => Path.Combine(Path.GetTempPath(), $"QuantumHub.Gui.{instance}.install");

    /// <summary>Добавляет файл в очередь.</summary>
    /// <param name="instance">Экземпляр хаба.</param>
    /// <param name="path">Путь к файлу плагина.</param>
    public static void Add(string instance, string path) => File.AppendAllLines(QueuePath(instance), [path]);

    /// <summary>Забирает всё из очереди.</summary>
    /// <param name="instance">Экземпляр хаба.</param>
    /// <returns>Пути к файлам.</returns>
    public static string[] Take(string instance)
    {
        var queue = QueuePath(instance);
        try
        {
            var paths = File.ReadAllLines(queue);
            File.Delete(queue);
            return paths;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
