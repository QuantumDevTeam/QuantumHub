using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using Google.Protobuf;
using QuantumHub.Contracts.Proto;

namespace QuantumHub.Core.Plugins;

/// <summary>Чтение и проверка манифестов плагинов.</summary>
public static partial class ManifestReader
{
    /// <summary>Имя файла манифеста. Под этим же именем манифест встраивается в DLL.</summary>
    public const string FileName = "plugin.jsonc";

    /// <summary>Разбор protobuf из JSON. Незнакомые поля пропускаются ради совместимости с будущими манифестами.</summary>
    private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));

    /// <summary>Id попадает в имена файлов, так что проверка заодно исключает трюки с путями вроде "../".</summary>
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex ValidId();

    /// <summary>Разбирает и проверяет манифест.</summary>
    /// <param name="jsonc">Текст манифеста.</param>
    /// <returns>Манифест.</returns>
    /// <exception cref="InvalidDataException">Манифест пуст или нарушает правила.</exception>
    public static PluginManifest Parse(string jsonc)
    {
        var json = Jsonc.Parse(jsonc)?.ToJsonString() ?? throw new InvalidDataException("The manifest is empty.");
        var manifest = Parser.Parse<PluginManifest>(json);

        if (!ValidId().IsMatch(manifest.Id))
            throw new InvalidDataException($"'{manifest.Id}' is not a valid plugin id.");
        if (!SemVersion.TryParse(manifest.Version, out _))
            throw new InvalidDataException($"'{manifest.Version}' is not a valid version.");

        foreach (var dependency in manifest.Dependencies)
        {
            if (dependency.Id.Length > 0 == dependency.Capability.Length > 0)
                throw new InvalidDataException("A dependency needs exactly one of 'id' and 'capability'.");
        }

        return manifest;
    }

    /// <summary>Читает встроенный ресурс, не загружая сборку: осмотр плагина никогда не выполняет его код.</summary>
    /// <param name="assemblyPath">Путь к DLL.</param>
    /// <param name="resourceName">Имя ресурса.</param>
    /// <returns>Содержимое или <see langword="null"/>, если ресурса нет.</returns>
    public static byte[]? ReadEmbedded(string assemblyPath, string resourceName)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null) return null;

        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.ManifestResources)
        {
            var resource = metadata.GetManifestResource(handle);
            var name = metadata.GetString(resource.Name);
            if (!resource.Implementation.IsNil) continue;
            if (!name.Equals(resourceName, StringComparison.OrdinalIgnoreCase) &&
                !name.EndsWith("." + resourceName, StringComparison.OrdinalIgnoreCase)) continue;

            var directory = pe.PEHeaders.CorHeader.ResourcesDirectory;
            var section = pe.GetSectionData(directory.RelativeVirtualAddress);
            var reader = section.GetReader((int)resource.Offset, directory.Size - (int)resource.Offset);
            var length = reader.ReadInt32();
            return reader.ReadBytes(length);
        }

        return null;
    }

    /// <summary>Читает манифест из DLL, если он там есть.</summary>
    /// <param name="assemblyPath">Путь к DLL.</param>
    /// <returns>Текст манифеста или <see langword="null"/>.</returns>
    public static string? ReadEmbeddedManifest(string assemblyPath)
    {
        try
        {
            var bytes = ReadEmbedded(assemblyPath, FileName);
            return bytes is null ? null : Encoding.UTF8.GetString(bytes);
        }
        catch (BadImageFormatException)
        {
            return null; // Нативная DLL рядом с плагином.
        }
    }
}
