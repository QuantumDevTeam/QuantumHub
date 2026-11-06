using System.IO.Compression;
using QuantumHub.Contracts.Proto;

namespace QuantumHub.Core.Plugins;

/// <summary>Установленный плагин на диске или причина, по которой его не удалось прочитать.</summary>
/// <param name="Directory">Папка плагина.</param>
/// <param name="Manifest">Манифест.</param>
/// <param name="Error">Что с ним не так, если он сломан.</param>
public sealed record PluginPackage(string Directory, PluginManifest Manifest, string? Error = null);

/// <summary>Поиск и установка плагинов на диске.</summary>
public static class PluginCatalog
{
    /// <summary>Отметка в папке, которую не удалось удалить при удалении плагина. Папка уйдёт при следующем сканировании.</summary>
    public const string PendingDeleteMarker = ".pending-delete";

    /// <summary>Расширения архивов плагинов.</summary>
    private static readonly string[] ArchiveExtensions = [".qhp", ".zip"];

    /// <summary>Читает все плагины в указанных папках. При равных версиях выигрывает папка, идущая позже.</summary>
    /// <param name="roots">Папки плагинов.</param>
    /// <param name="log">Журнал.</param>
    /// <returns>Плагины по алфавиту. Сломанные тоже входят в список, чтобы их было видно в GUI.</returns>
    public static List<PluginPackage> Discover(IEnumerable<string> roots, HubLog log)
    {
        var found = new Dictionary<string, PluginPackage>();
        foreach (var root in roots.Where(Directory.Exists))
        {
            Normalize(root, log);
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                if (Path.GetFileName(dir).StartsWith('.')) continue;
                if (File.Exists(Path.Combine(dir, PendingDeleteMarker)))
                {
                    TryDelete(dir, log);
                    continue;
                }

                var package = ReadFolder(dir);
                if (package is null) continue;

                if (!found.TryGetValue(package.Manifest.Id, out var existing) || IsNewer(package, existing))
                    found[package.Manifest.Id] = package;
            }
        }

        return found.Values.OrderBy(package => package.Manifest.Id, StringComparer.Ordinal).ToList();
    }

    /// <summary>Удаляет папку, если это возможно.</summary>
    private static void TryDelete(string dir, HubLog log)
    {
        try
        {
            Directory.Delete(dir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning($"Could not remove '{dir}': {ex.Message}");
        }
    }

    /// <summary>Новее ли кандидат.</summary>
    private static bool IsNewer(PluginPackage candidate, PluginPackage existing) =>
        SemVersion.TryParse(candidate.Manifest.Version, out var a) &&
        SemVersion.TryParse(existing.Manifest.Version, out var b) && a.CompareTo(b) >= 0;

    /// <summary>
    /// Превращает брошенные в папку архивы и одиночные DLL в обычные папки плагинов, чтобы остальной код
    /// имел дело только с одной формой.
    /// </summary>
    private static void Normalize(string root, HubLog log)
    {
        foreach (var file in Directory.EnumerateFiles(root))
        {
            var extension = Path.GetExtension(file).ToLowerInvariant();
            if (!ArchiveExtensions.Contains(extension) && extension != ".dll") continue;
            if (extension == ".dll" && ManifestReader.ReadEmbeddedManifest(file) is null) continue;

            try
            {
                Install(file, root);
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                log.Warning($"Could not unpack '{file}': {ex.Message}");
            }
        }
    }

    /// <summary>Читает один плагин: сначала plugin.jsonc, потом манифест, встроенный в DLL.</summary>
    /// <param name="dir">Папка.</param>
    /// <returns>Пакет, пакет с ошибкой, либо <see langword="null"/>, если это вообще не плагин.</returns>
    public static PluginPackage? ReadFolder(string dir)
    {
        try
        {
            var path = Path.Combine(dir, ManifestReader.FileName);
            if (File.Exists(path))
                return new PluginPackage(dir, ManifestReader.Parse(File.ReadAllText(path)));

            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll"))
            {
                if (ManifestReader.ReadEmbeddedManifest(dll) is not { } text) continue;

                var manifest = ManifestReader.Parse(text);
                manifest.Service ??= new PluginEntry();
                if (manifest.Service.Assembly.Length == 0) manifest.Service.Assembly = Path.GetFileName(dll);
                return new PluginPackage(dir, manifest);
            }

            return null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or Google.Protobuf.InvalidJsonException
                                       or Google.Protobuf.InvalidProtocolBufferException or System.Text.Json.JsonException)
        {
            var id = Path.GetFileName(dir);
            return new PluginPackage(dir, new PluginManifest { Id = id, Name = id, Version = "0.0.0" }, ex.Message);
        }
    }

    /// <summary>Смотрит id того, что собираются установить, не устанавливая.</summary>
    /// <param name="source">Архив, DLL или папка.</param>
    public static PluginPackage? Inspect(string source)
    {
        if (Directory.Exists(source)) return ReadFolder(source);

        if (ArchiveExtensions.Contains(Path.GetExtension(source).ToLowerInvariant()))
        {
            using var archive = ZipFile.OpenRead(source);
            var entry = archive.GetEntry(ManifestReader.FileName);
            if (entry is null) return null;
            using var reader = new StreamReader(entry.Open());
            return new PluginPackage(source, ManifestReader.Parse(reader.ReadToEnd()));
        }

        return ManifestReader.ReadEmbeddedManifest(source) is { } text
            ? new PluginPackage(source, ManifestReader.Parse(text))
            : null;
    }

    /// <summary>
    /// Копирует плагин в папку назначения. Сначала во временную папку, чтобы плохой пакет не задел
    /// установленную версию.
    /// </summary>
    /// <param name="source">Архив, DLL с манифестом внутри или папка.</param>
    /// <param name="targetRoot">Папка плагинов.</param>
    /// <returns>Установленный пакет.</returns>
    /// <exception cref="InvalidDataException">В источнике нет корректного манифеста.</exception>
    public static PluginPackage Install(string source, string targetRoot)
    {
        Directory.CreateDirectory(targetRoot);
        var staging = Path.Combine(targetRoot, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            if (Directory.Exists(source))
            {
                CopyDirectory(source, staging);
            }
            else if (ArchiveExtensions.Contains(Path.GetExtension(source).ToLowerInvariant()))
            {
                ZipFile.ExtractToDirectory(source, staging);
            }
            else
            {
                File.Copy(source, Path.Combine(staging, Path.GetFileName(source)));
                var deps = Path.ChangeExtension(source, ".deps.json");
                if (File.Exists(deps)) File.Copy(deps, Path.Combine(staging, Path.GetFileName(deps)));
            }

            var package = ReadFolder(staging) ?? throw new InvalidDataException($"No plugin manifest found in '{source}'.");
            if (package.Error is not null) throw new InvalidDataException(package.Error);

            var destination = Path.Combine(targetRoot, package.Manifest.Id);
            if (Directory.Exists(destination)) Directory.Delete(destination, true);
            Directory.Move(staging, destination);
            return package with { Directory = destination };
        }
        catch
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            throw;
        }
    }

    /// <summary>Копирует папку со всем содержимым.</summary>
    private static void CopyDirectory(string source, string destination)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)));
    }
}
