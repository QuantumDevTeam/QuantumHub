using System.Reflection;
using System.Runtime.Loader;
using QuantumHub.Ui.Contracts;

namespace QuantumHub.Gui.Services;

/// <summary>Загружает нативную UI-часть плагина.</summary>
/// <remarks>
/// Всё, что у GUI уже есть (WinUI, контракты), берётся из копии GUI, иначе типы не совпадут. Контекст не
/// выгружаемый: объекты WinRT удерживают свои типы, поэтому новая версия подхватится после перезапуска GUI.
/// </remarks>
public static class NativeUiLoader
{
    /// <summary>Уже загруженные сборки.</summary>
    private static readonly Dictionary<string, Assembly> Loaded = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Загружает сборку и создаёт её страницы.</summary>
    /// <param name="path">Путь к DLL.</param>
    /// <param name="typeName">Имя класса. Если пусто, берутся все реализации <see cref="INativePluginPage"/>.</param>
    /// <returns>Страницы.</returns>
    public static IReadOnlyList<INativePluginPage> Load(string path, string typeName)
    {
        if (!Loaded.TryGetValue(path, out var assembly))
        {
            assembly = new UiLoadContext(path).LoadFromAssemblyPath(path);
            Loaded[path] = assembly;
        }

        if (typeName.Length > 0)
            return [(INativePluginPage)Activator.CreateInstance(assembly.GetType(typeName, true)!)!];

        return assembly.GetExportedTypes()
            .Where(type => type is { IsAbstract: false } && typeof(INativePluginPage).IsAssignableFrom(type))
            .Select(type => (INativePluginPage)Activator.CreateInstance(type)!)
            .ToList();
    }

    /// <summary>Область загрузки UI-части плагина.</summary>
    /// <param name="mainAssembly">Главная сборка.</param>
    private sealed class UiLoadContext(string mainAssembly) : AssemblyLoadContext(Path.GetFileName(mainAssembly))
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssembly);

        /// <summary>Сначала своё, потом зависимости плагина.</summary>
        /// <param name="name">Что ищем.</param>
        protected override Assembly? Load(AssemblyName name)
        {
            var mine = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            if (File.Exists(mine)) return null;

            var path = _resolver.ResolveAssemblyToPath(name);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
