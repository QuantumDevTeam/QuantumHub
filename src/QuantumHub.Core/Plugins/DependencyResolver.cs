using QuantumHub.Contracts.Proto;

namespace QuantumHub.Core.Plugins;

/// <summary>Кто может закрыть зависимость: установленный плагин или подключённый клиент.</summary>
/// <param name="Id">Id.</param>
/// <param name="Version">Версия.</param>
/// <param name="Provides">Предоставляемые возможности.</param>
/// <param name="UnavailableReason">Почему сейчас недоступен, например выключен. <see langword="null"/>, если доступен.</param>
public sealed record ProviderCandidate(
    string Id,
    string Version,
    IReadOnlyList<Capability> Provides,
    string? UnavailableReason = null);

/// <summary>Чистая логика зависимостей: никакого ввода-вывода и никаких запущенных плагинов, поэтому её просто тестировать.</summary>
public static class DependencyResolver
{
    /// <summary>Значение поля "when" для ленивой зависимости.</summary>
    public const string LazyPhase = "lazy";

    /// <summary>Разрешается ли зависимость до запуска зависимого плагина.</summary>
    /// <param name="dependency">Зависимость.</param>
    public static bool IsInitTime(Dependency dependency) => dependency.When != LazyPhase;

    /// <summary>Подбирает поставщика, предпочитая самую высокую версию.</summary>
    /// <remarks>
    /// Разделяет случаи «никого нет», «есть, но версия не та» и «есть, но недоступен»: человеку
    /// нужно понимать, что именно чинить.
    /// </remarks>
    /// <param name="dependency">Что ищем.</param>
    /// <param name="candidates">Из кого выбираем.</param>
    /// <returns>Результат, для ошибок заполнено поле Detail.</returns>
    public static DependencyResolution Match(Dependency dependency, IEnumerable<ProviderCandidate> candidates)
    {
        var result = new DependencyResolution { Dependency = dependency };
        if (!VersionRange.TryParse(dependency.Version, out var range))
        {
            result.State = DependencyState.Failed;
            result.Detail = $"Invalid version range '{dependency.Version}'.";
            return result;
        }

        var label = dependency.Id.Length > 0 ? dependency.Id : $"capability '{dependency.Capability}'";
        var named = new List<(ProviderCandidate Provider, SemVersion Version)>();
        foreach (var candidate in candidates)
        {
            if (dependency.Id.Length > 0)
            {
                if (candidate.Id == dependency.Id && SemVersion.TryParse(candidate.Version, out var version))
                    named.Add((candidate, version));
            }
            else
            {
                var capability = candidate.Provides.FirstOrDefault(c => c.Name == dependency.Capability);
                if (capability is null) continue;
                var text = capability.Version.Length > 0 ? capability.Version : candidate.Version;
                if (SemVersion.TryParse(text, out var version)) named.Add((candidate, version));
            }
        }

        if (named.Count == 0)
        {
            result.State = DependencyState.Missing;
            result.Detail = $"Nothing provides {label}.";
            return result;
        }

        var matching = named.Where(entry => range.Includes(entry.Version)).OrderByDescending(entry => entry.Version).ToList();
        if (matching.Count == 0)
        {
            result.State = DependencyState.VersionMismatch;
            result.Detail = $"{label} is at {named.Max(entry => entry.Version)} but '{dependency.Version}' is required.";
            return result;
        }

        var usable = matching.FirstOrDefault(entry => entry.Provider.UnavailableReason is null);
        if (usable.Provider is null)
        {
            result.State = DependencyState.Failed;
            result.ProviderId = matching[0].Provider.Id;
            result.Detail = $"{label}: {matching[0].Provider.UnavailableReason}";
            return result;
        }

        result.State = DependencyState.Resolved;
        result.ProviderId = usable.Provider.Id;
        result.ProviderVersion = usable.Version.ToString();
        return result;
    }

    /// <summary>Поиск цикла зависимостей в глубину.</summary>
    /// <param name="start">С какого плагина начать.</param>
    /// <param name="edges">От кого зависит плагин.</param>
    /// <returns>Цикл в виде пути, например a, b, a. Либо <see langword="null"/>, если цикла нет.</returns>
    public static IReadOnlyList<string>? FindCycle(string start, Func<string, IEnumerable<string>> edges)
    {
        var done = new HashSet<string>();
        var path = new List<string>();

        IReadOnlyList<string>? Visit(string node)
        {
            var index = path.IndexOf(node);
            if (index >= 0) return [.. path.Skip(index), node];
            if (!done.Add(node)) return null;

            path.Add(node);
            foreach (var next in edges(node))
            {
                if (Visit(next) is { } cycle) return cycle;
            }

            path.RemoveAt(path.Count - 1);
            return null;
        }

        return Visit(start);
    }
}
