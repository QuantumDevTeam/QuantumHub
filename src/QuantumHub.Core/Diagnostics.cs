using QuantumHub.Contracts.Ui;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Google.Protobuf;

namespace QuantumHub.Core;

/// <summary>Собирает архив для отчёта об ошибке.</summary>
/// <remarks>
/// Из настроек плагинов попадают только объявленные в манифесте и не секретные: токены плагины держат и в ключах, которых
/// в манифесте нет. Журналы идут как есть, поэтому в них могут быть заголовки окон и имена треков.
/// </remarks>
public static class Diagnostics
{
    /// <summary>Сколько дней журнала класть в архив.</summary>
    private const int LogDays = 3;
    /// <summary>Сколько старых архивов оставлять в папке по умолчанию.</summary>
    private const int KeepReports = 5;
    /// <summary>Журналы других программ комплекта.</summary>
    private static readonly string[] ExtraLogs = ["crash.log", "gui.log", "vr-dashboard.log"];
    /// <summary>Настройки хаба, которые полезны в отчёте.</summary>
    private static readonly string[] HubSettings = [Hub.LanguageKey, Hub.LogLevelKey, Hub.LogRetentionKey, "disabledPlugins"];

    /// <summary>Записывает архив.</summary>
    /// <param name="hub">Хаб.</param>
    /// <param name="path">Куда. Пусто значит папка diagnostics внутри данных.</param>
    /// <returns>Путь к архиву.</returns>
    public static string Export(Hub hub, string path)
    {
        var folder = Path.Combine(hub.Paths.DataDir, "diagnostics");
        var target = path.Length > 0 ? Path.GetFullPath(path) : Path.Combine(folder, $"quantumhub-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        using (var zip = ZipFile.Open(target, ZipArchiveMode.Create))
        {
            WriteText(zip, "system.txt", $"QuantumHub {Hub.Version}\nWindows {Environment.OSVersion.Version}\n.NET {Environment.Version}\nLanguage {hub.GetStatus().Language}\nTime {DateTimeOffset.Now:O}\n");
            WriteText(zip, "status.json", JsonFormatter.Default.Format(hub.GetStatus()));
            WriteText(zip, "sessions.json", JsonFormatter.Default.Format(hub.Server.ListSessions()));
            WriteText(zip, "history.json", JsonFormatter.Default.Format(hub.Server.History()));
            WriteText(zip, "plugins.txt", Plugins(hub));
            WriteText(zip, "settings.json", Settings(hub).ToJsonString(Jsonc.WriteOptions));
            AddLogs(hub, zip);
        }

        if (path.Length == 0) Prune(folder);
        hub.Log.Info(Loc.T($"Отчёт записан: {target}", $"Wrote a diagnostics report to {target}"));
        return target;
    }

    /// <summary>Список плагинов с состояниями.</summary>
    private static string Plugins(Hub hub)
    {
        var text = new StringBuilder();
        foreach (var plugin in hub.Plugins.List().Plugins)
        {
            text.Append($"{plugin.Manifest.Id} {plugin.Manifest.Version}: {plugin.State}");
            if (!plugin.Enabled) text.Append(" (disabled)");
            if (plugin.Error.Length > 0) text.Append($" ({plugin.Error})");
            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>Настройки без секретов.</summary>
    private static JsonObject Settings(Hub hub)
    {
        var result = new JsonObject();
        var own = new JsonObject();
        foreach (var key in HubSettings) own[key] = hub.Config.Get(Hub.ConfigOwner, key);
        result[Hub.ConfigOwner] = own;

        foreach (var plugin in hub.Plugins.List().Plugins)
        {
            var values = new JsonObject();
            foreach (var setting in plugin.Manifest.Settings.Where(setting => setting.Type != "secret"))
                values[setting.Key] = hub.Config.Get(plugin.Manifest.Id, setting.Key);
            if (values.Count > 0) result[plugin.Manifest.Id] = values;
        }

        return result;
    }

    /// <summary>Кладёт журналы. Файл сегодняшнего дня открыт на запись, поэтому читается с разрешением на совместный доступ.</summary>
    private static void AddLogs(Hub hub, ZipArchive zip)
    {
        var logs = new DirectoryInfo(hub.Paths.LogsDir);
        var recent = logs.EnumerateFiles("hub-*.log").OrderByDescending(file => file.Name, StringComparer.Ordinal).Take(LogDays);
        var extra = ExtraLogs.Select(name => new FileInfo(Path.Combine(logs.FullName, name))).Where(file => file.Exists);

        foreach (var file in recent.Concat(extra))
        {
            try
            {
                using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var target = zip.CreateEntry($"logs/{file.Name}").Open();
                source.CopyTo(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                WriteText(zip, $"logs/{file.Name}.unreadable.txt", ex.Message);
            }
        }
    }

    /// <summary>Записывает текстовый файл в архив.</summary>
    private static void WriteText(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    /// <summary>Оставляет только последние архивы.</summary>
    private static void Prune(string folder)
    {
        foreach (var old in new DirectoryInfo(folder).EnumerateFiles("quantumhub-report-*.zip")
                     .OrderByDescending(file => file.Name, StringComparer.Ordinal).Skip(KeepReports))
        {
            try
            {
                old.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Удалится в следующий раз.
            }
        }
    }
}
