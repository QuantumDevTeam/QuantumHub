using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.VrDashboard;

/// <summary>Рисует панель в картинку. Окон здесь нет: SteamVR показывает готовые пиксели.</summary>
public static class PanelRenderer
{
    private const double Dpi = 96;
    private const double Radius = 14;

    private static readonly Typeface Regular = new("Segoe UI");
    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Brush Background = Solid(0x14, 0x16, 0x1C);
    private static readonly Brush Tile = Solid(0x24, 0x28, 0x32);
    private static readonly Brush TileActive = Solid(0x2B, 0x5C, 0xE6);
    private static readonly Brush TileDisabled = Solid(0x1C, 0x1F, 0x27);
    private static readonly Brush Text = Solid(0xF2, 0xF4, 0xF8);
    private static readonly Brush Muted = Solid(0xA0, 0xA8, 0xB8);

    private static SolidColorBrush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Рисует панель.</summary>
    /// <param name="view">Раскладка.</param>
    /// <param name="state">Данные.</param>
    private static RenderTargetBitmap Draw(PanelView view, PanelState state)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(Background, null, new Rect(0, 0, PanelLayout.Width, PanelLayout.Height));
            DrawText(context, "QuantumHub", 24, 20, 26, Text, Bold, 400);

            if (view.Tiles.Count == 0)
                DrawText(context, Loc.T("Нет плагинов для этой панели. Включи их в QuantumHub.", "No plugins for this panel. Enable them in QuantumHub."), 24, 90, 22, Muted, Regular, 960);

            foreach (var tile in view.Tiles) DrawTile(context, tile);

            if (view.Preview is { } preview)
            {
                var rect = new Rect(preview.X, preview.Y, preview.Width, preview.Height);
                context.DrawRoundedRectangle(Tile, null, rect, Radius, Radius);
                var heading = state.GameRunning ? Loc.T("В чатбоксе сейчас", "In the chatbox now") : Loc.T("VRChat не запущен", "VRChat is not running");
                DrawText(context, heading, rect.X + 16, rect.Y + 10, 16, Muted, Regular, rect.Width - 32);
                var text = state.Preview.Length > 0 ? state.Preview : Loc.T("Пока пусто", "Nothing yet");
                DrawText(context, text, rect.X + 16, rect.Y + 36, 18, Text, Regular, rect.Width - 32, rect.Height - 46);
            }
        }

        var bitmap = new RenderTargetBitmap(PanelLayout.Width, PanelLayout.Height, Dpi, Dpi, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap;
    }

    /// <summary>Рисует кнопку.</summary>
    private static void DrawTile(DrawingContext context, PanelTile tile)
    {
        var rect = new Rect(tile.Bounds.X, tile.Bounds.Y, tile.Bounds.Width, tile.Bounds.Height);
        var fill = !tile.Enabled ? TileDisabled : tile.Active ? TileActive : Tile;
        context.DrawRoundedRectangle(fill, null, rect, Radius, Radius);

        var width = rect.Width - 28;
        var title = tile.Enabled ? Text : Muted;
        if (tile.Value.Length == 0)
        {
            DrawText(context, tile.Title, rect.X + 14, rect.Y + (rect.Height - 26) / 2, 18, title, Bold, width, 26);
            return;
        }

        DrawText(context, tile.Title, rect.X + 14, rect.Y + 10, 16, tile.Active ? Text : Muted, Regular, width, 24);
        DrawText(context, tile.Value, rect.X + 14, rect.Y + 36, rect.Height > 100 ? 26 : 20, title, Bold, width, rect.Height - 44);
    }

    /// <summary>Рисует текст. Что не поместилось, обрезается многоточием.</summary>
    private static void DrawText(DrawingContext context, string text, double x, double y, double size, Brush brush, Typeface face, double maxWidth, double maxHeight = 0)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush, 1)
        {
            MaxTextWidth = Math.Max(maxWidth, 1),
            Trimming = TextTrimming.CharacterEllipsis,
        };
        if (maxHeight > 0) formatted.MaxTextHeight = maxHeight;
        context.DrawText(formatted, new Point(x, y));
    }

    /// <summary>Рисует панель для SteamVR.</summary>
    /// <param name="view">Раскладка.</param>
    /// <param name="state">Данные.</param>
    /// <returns>Пиксели RGBA.</returns>
    public static byte[] RenderRgba(PanelView view, PanelState state)
    {
        var bitmap = Draw(view, state);
        var pixels = new byte[PanelLayout.Width * PanelLayout.Height * 4];
        bitmap.CopyPixels(pixels, PanelLayout.Width * 4, 0);

        // WPF отдаёт BGRA, а SteamVR ждёт RGBA.
        for (var i = 0; i < pixels.Length; i += 4) (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        return pixels;
    }

    /// <summary>Сохраняет панель в файл. Так внешний вид можно посмотреть без шлема.</summary>
    /// <param name="view">Раскладка.</param>
    /// <param name="state">Данные.</param>
    /// <param name="path">Куда сохранить PNG.</param>
    public static void SavePng(PanelView view, PanelState state, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Draw(view, state)));
        using var file = File.Create(path);
        encoder.Save(file);
    }
}

/// <summary>Рисование на WPF.</summary>
public sealed class WpfPanelPainter : IPanelPainter
{
    /// <inheritdoc/>
    public byte[] Rgba(PanelView view, PanelState state) => PanelRenderer.RenderRgba(view, state);

    /// <inheritdoc/>
    public void Png(PanelView view, PanelState state, string path) => PanelRenderer.SavePng(view, state, path);
}
