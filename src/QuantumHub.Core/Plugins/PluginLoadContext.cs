using System.Reflection;
using System.Runtime.Loader;

namespace QuantumHub.Core.Plugins;

/// <summary>Отдельная область загрузки сборок для каждого плагина.</summary>
/// <remarks>
/// <para>
/// Даёт плагину собственные версии зависимостей: два плагина могут тянуть разные версии одной библиотеки.
/// Исключение — сборки контракта с хабом. Они обязаны быть копиями хаба, иначе .NET посчитает
/// <c>IHubPlugin</c> из разных копий разными типами.
/// </para>
/// <para>Контекст выгружаемый (collectible), поэтому плагин можно удалить без перезапуска хаба.</para>
/// </remarks>
/// <param name="mainAssemblyPath">Главная сборка плагина.</param>
/// <param name="name">Имя контекста, для диагностики.</param>
public sealed class PluginLoadContext(string mainAssemblyPath, string name) : AssemblyLoadContext(name, isCollectible: true)
{
    /// <summary>Сборки, общие с хабом.</summary>
    private static readonly HashSet<string> Shared = new(StringComparer.OrdinalIgnoreCase)
    {
        "QuantumHub.Contracts",
        "Google.Protobuf",
    };

    /// <summary>Находит зависимости плагина по его .deps.json.</summary>
    private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);
    private readonly string _directory = Path.GetDirectoryName(mainAssemblyPath)!;

    /// <summary>Находит сборку для плагина. <see langword="null"/> отдаёт решение контексту по умолчанию, то есть хабу.</summary>
    /// <param name="assemblyName">Что ищем.</param>
    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is null || Shared.Contains(assemblyName.Name)) return null;

        var path = _resolver.ResolveAssemblyToPath(assemblyName);
        if (path is null)
        {
            // Плагин не обязан поставлять .deps.json.
            var candidate = Path.Combine(_directory, assemblyName.Name + ".dll");
            if (File.Exists(candidate)) path = candidate;
        }

        return path is null ? null : LoadFromAssemblyPath(path);
    }

    /// <summary>Находит нативную библиотеку плагина.</summary>
    /// <param name="unmanagedDllName">Что ищем.</param>
    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
    }
}
