namespace QuantumHub.Setup;

/// <summary>Правка значения PATH. Отдельно от реестра, чтобы проверять тестами: сломанный PATH ломает пользователю всё.</summary>
internal static class PathList
{
    /// <summary>Добавляет папку в конец, если её там нет.</summary>
    /// <param name="path">Значение PATH.</param>
    /// <param name="folder">Папка.</param>
    /// <returns>Новое значение.</returns>
    public static string Add(string path, string folder)
    {
        if (Split(path).Any(item => Same(item, folder))) return path;
        return path.Length == 0 ? folder : $"{path.TrimEnd(';')};{folder}";
    }

    /// <summary>Убирает папку, не трогая остальное.</summary>
    /// <param name="path">Значение PATH.</param>
    /// <param name="folder">Папка.</param>
    /// <returns>Новое значение.</returns>
    public static string Remove(string path, string folder) =>
        Split(path).Any(item => Same(item, folder)) ? string.Join(';', Split(path).Where(item => !Same(item, folder))) : path;

    private static IEnumerable<string> Split(string path) => path.Split(';', StringSplitOptions.RemoveEmptyEntries);

    private static bool Same(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
}
