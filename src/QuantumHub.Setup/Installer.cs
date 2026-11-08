using QuantumHub.Contracts.Ui;
using System.IO.Compression;

namespace QuantumHub.Setup;

/// <summary>Файловая часть установки: без окон, реестра и процессов, поэтому её можно проверять тестами.</summary>
/// <param name="directory">Папка программы.</param>
internal sealed class Installer(string directory)
{
    /// <summary>Файл, по которому видно, что папка действительно наша.</summary>
    public const string ServiceExe = "QuantumHub.Service.exe";
    /// <summary>Папка плагинов внутри программы.</summary>
    private const string PluginsFolder = "plugins";

    /// <summary>Сколько раз пробовать удалить файл, который ещё держит завершающийся процесс.</summary>
    private const int DeleteAttempts = 10;
    /// <summary>Пауза между попытками.</summary>
    private static readonly TimeSpan DeleteDelay = TimeSpan.FromMilliseconds(300);

    /// <summary>Папка программы.</summary>
    public string Directory { get; } = Path.GetFullPath(directory);

    /// <summary>Установлена ли программа в этой папке.</summary>
    public bool IsInstalled => File.Exists(Path.Combine(Directory, ServiceExe));

    /// <summary>
    /// Ставит программу или обновляет её. Старые файлы уходят, кроме плагинов, которые пользователь поставил
    /// сам: их в архиве нет, и установщик о них ничего не знает.
    /// </summary>
    /// <param name="archive">Архив с программой.</param>
    /// <param name="progress">Доля выполненного от 0 до 1.</param>
    public void Install(Stream archive, Action<double>? progress = null)
    {
        using var zip = new ZipArchive(archive, ZipArchiveMode.Read);
        var bundled = zip.Entries.Select(entry => PluginOf(entry.FullName)).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        // В чужой папке ничего не удаляем: её могли выбрать по ошибке.
        if (IsInstalled) RemoveOldFiles(bundled);

        System.IO.Directory.CreateDirectory(Directory);
        var total = Math.Max(1, zip.Entries.Sum(entry => entry.Length));
        long done = 0;
        foreach (var entry in zip.Entries)
        {
            var target = Target(entry.FullName);
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                System.IO.Directory.CreateDirectory(target);
                continue;
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, true);
            done += entry.Length;
            progress?.Invoke((double)done / total);
        }
    }

    /// <summary>Удаляет программу. Настройки в профиле пользователя остаются.</summary>
    public void Uninstall()
    {
        if (IsInstalled) DeleteDirectory(Directory);
    }

    /// <summary>Убирает всё старое перед обновлением.</summary>
    private void RemoveOldFiles(HashSet<string> bundled)
    {
        var plugins = Path.Combine(Directory, PluginsFolder);
        foreach (var dir in System.IO.Directory.EnumerateDirectories(Directory))
        {
            if (!string.Equals(dir, plugins, StringComparison.OrdinalIgnoreCase)) DeleteDirectory(dir);
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(Directory)) DeleteFile(file);

        if (!System.IO.Directory.Exists(plugins)) return;
        foreach (var dir in System.IO.Directory.EnumerateDirectories(plugins))
        {
            if (bundled.Contains(Path.GetFileName(dir))) DeleteDirectory(dir);
        }
    }

    /// <summary>Id плагина, к которому относится файл архива.</summary>
    private static string? PluginOf(string entry)
    {
        var parts = entry.Split('/', '\\');
        return parts.Length > 2 && parts[0] == PluginsFolder ? parts[1] : null;
    }

    /// <summary>Путь файла на диске. Файл из архива не должен выйти за пределы папки программы.</summary>
    private string Target(string entry)
    {
        var target = Path.GetFullPath(Path.Combine(Directory, entry));
        if (!target.StartsWith(Directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(Loc.T($"В архиве недопустимый путь: {entry}", $"The archive has an invalid path: {entry}"));
        return target;
    }

    /// <summary>Удаляет папку, подождав процесс, который её ещё держит.</summary>
    private static void DeleteDirectory(string path) => Retry(() =>
    {
        if (System.IO.Directory.Exists(path)) System.IO.Directory.Delete(path, true);
    });

    /// <summary>Удаляет файл, подождав процесс, который его ещё держит.</summary>
    private static void DeleteFile(string path) => Retry(() => File.Delete(path));

    /// <summary>Повторяет действие, пока файлы заняты.</summary>
    private static void Retry(Action action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < DeleteAttempts)
            {
                Thread.Sleep(DeleteDelay);
            }
        }
    }
}
