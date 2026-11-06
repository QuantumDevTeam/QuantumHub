using QuantumHub.Contracts.Proto;

namespace QuantumHub.Core.Plugins;

/// <summary>Показывает манифест на нужном языке. Сам манифест не меняется: перевод накладывается на копию.</summary>
public static class ManifestLocalizer
{
    /// <summary>Накладывает перевод из блока "locales". Чего в переводе нет, остаётся как в манифесте.</summary>
    /// <param name="manifest">Манифест.</param>
    /// <param name="language">Язык.</param>
    /// <returns>Манифест на этом языке.</returns>
    public static PluginManifest Apply(PluginManifest manifest, string language)
    {
        if (!manifest.Locales.TryGetValue(language, out var locale)) return manifest;

        var result = manifest.Clone();
        if (locale.Name.Length > 0) result.Name = locale.Name;
        if (locale.Description.Length > 0) result.Description = locale.Description;

        foreach (var setting in result.Settings)
        {
            if (locale.Groups.TryGetValue(setting.Group, out var group)) setting.Group = group;
            if (!locale.Settings.TryGetValue(setting.Key, out var text)) continue;

            if (text.Title.Length > 0) setting.Title = text.Title;
            if (text.Description.Length > 0) setting.Description = text.Description;
            foreach (var (option, label) in text.Options) setting.OptionLabels[option] = label;
        }

        return result;
    }
}
