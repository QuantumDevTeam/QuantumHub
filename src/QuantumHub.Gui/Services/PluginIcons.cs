using Microsoft.UI.Xaml.Media.Imaging;
using QuantumHub.Contracts.Proto;
using Windows.Storage.Streams;

namespace QuantumHub.Gui.Services;

/// <summary>Загрузка иконок плагинов.</summary>
public static class PluginIcons
{
    /// <summary>Получает иконку от хаба. Файл может лежать на диске или внутри DLL, GUI об этом не знает.</summary>
    /// <param name="hub">Связь с хабом.</param>
    /// <param name="pluginId">Id плагина.</param>
    /// <returns>Изображение или <see langword="null"/>.</returns>
    public static async Task<BitmapImage?> LoadAsync(HubConnection hub, string pluginId)
    {
        var asset = await hub.TryCallAsync<AssetResponse>("hub.plugins.asset", new AssetRequest { PluginId = pluginId });
        if (asset is null || asset.Data.IsEmpty) return null;

        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(asset.Data.ToByteArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }
}
