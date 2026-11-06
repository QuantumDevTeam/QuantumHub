using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using QuantumHub.Contracts.Bus;
using QuantumHub.Contracts.Ui;
using Valve.VR;

namespace QuantumHub.VrDashboard;

/// <summary>Вкладка в меню SteamVR: показывает картинку панели и передаёт нажатия лучом контроллера.</summary>
/// <param name="client">Связь с плагинами.</param>
/// <param name="log">Журнал.</param>
public sealed class OverlayHost(PanelClient client, Action<string> log)
{
    private const string OverlayKey = "quantumdevteam.quantumhub.dashboard";
    private const float WidthInMeters = 2.2f;
    private const int MaxInputLength = 144;
    /// <summary>Шаг цикла: в нём читаются нажатия.</summary>
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(20);
    /// <summary>Как часто обновлять данные, пока панель открыта.</summary>
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromSeconds(1.5);

    private ulong _overlay;
    private PanelOptions _options = new();
    private PanelState _state = new();
    private PanelView _view = new([], null);
    private string? _hovered;
    private bool _dirty = true;
    /// <summary>Для чего сейчас открыта клавиатура SteamVR.</summary>
    private string? _keyboardFor;
    private int _refreshing;

    /// <summary>Подключается к SteamVR и работает, пока тот не закроется.</summary>
    /// <returns>0 при обычном завершении.</returns>
    public int Run()
    {
        var error = EVRInitError.None;
        Valve.VR.OpenVR.Init(ref error, EVRApplicationType.VRApplication_Overlay, string.Empty);
        if (error != EVRInitError.None)
        {
            log($"SteamVR is unavailable: {error}");
            return 1;
        }

        try
        {
            return CreateOverlay() ? Loop() : 1;
        }
        finally
        {
            Valve.VR.OpenVR.Shutdown();
        }
    }

    /// <summary>Создаёт вкладку.</summary>
    private bool CreateOverlay()
    {
        ulong thumbnail = 0;
        var result = Valve.VR.OpenVR.Overlay.CreateDashboardOverlay(OverlayKey, "QuantumHub", ref _overlay, ref thumbnail);
        if (result != EVROverlayError.None)
        {
            log($"Could not create the overlay: {result}");
            return false;
        }

        var icon = Path.Combine(AppContext.BaseDirectory, "logo.png");
        if (File.Exists(icon)) Valve.VR.OpenVR.Overlay.SetOverlayFromFile(thumbnail, icon);

        Valve.VR.OpenVR.Overlay.SetOverlayWidthInMeters(_overlay, WidthInMeters);
        Valve.VR.OpenVR.Overlay.SetOverlayInputMethod(_overlay, VROverlayInputMethod.Mouse);
        var scale = new HmdVector2_t { v0 = PanelLayout.Width, v1 = PanelLayout.Height };
        Valve.VR.OpenVR.Overlay.SetOverlayMouseScale(_overlay, ref scale);
        return true;
    }

    /// <summary>Главный цикл.</summary>
    private int Loop()
    {
        var size = (uint)Marshal.SizeOf<VREvent_t>();
        var next = DateTime.MinValue;
        while (true)
        {
            var vrEvent = new VREvent_t();
            while (Valve.VR.OpenVR.System.PollNextEvent(ref vrEvent, size))
            {
                if (vrEvent.eventType != (uint)EVREventType.VREvent_Quit) continue;
                Valve.VR.OpenVR.System.AcknowledgeQuit_Exiting();
                return 0;
            }

            while (Valve.VR.OpenVR.Overlay.PollNextOverlayEvent(_overlay, ref vrEvent, size)) Handle(vrEvent);

            var visible = Valve.VR.OpenVR.Overlay.IsOverlayVisible(_overlay);
            if (visible && DateTime.UtcNow >= next)
            {
                next = DateTime.UtcNow + RefreshEvery;
                RefreshInBackground();
            }

            if (_dirty) Present();
            Thread.Sleep(Step);
        }
    }

    /// <summary>Обновляет данные, не задерживая цикл: иначе панель перестанет отзываться на контроллер.</summary>
    private void RefreshInBackground()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var options = await client.ReadOptionsAsync();
                var state = await client.ReadAsync(options);
                if (state.Equals(Volatile.Read(ref _state)) && options == _options) return;
                _options = options;
                Volatile.Write(ref _state, state);
                _dirty = true;
            }
            catch (Exception ex) when (ex is IOException or HubCallException or InvalidOperationException)
            {
                log($"Refresh failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref _refreshing, 0);
            }
        });
    }

    /// <summary>Отдаёт картинку в SteamVR.</summary>
    private void Present()
    {
        _dirty = false;
        var state = Volatile.Read(ref _state);
        _view = PanelLayout.Build(state, _options);

        var pixels = PanelRenderer.RenderRgba(_view, state, _hovered);
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            Valve.VR.OpenVR.Overlay.SetOverlayRaw(_overlay, handle.AddrOfPinnedObject(), PanelLayout.Width, PanelLayout.Height, 4);
        }
        finally
        {
            handle.Free();
        }
    }

    /// <summary>Разбирает событие панели.</summary>
    private void Handle(VREvent_t vrEvent)
    {
        switch ((EVREventType)vrEvent.eventType)
        {
            case EVREventType.VREvent_MouseMove:
                var hovered = TileAt(vrEvent.data.mouse)?.Id;
                if (hovered == _hovered) break;
                _hovered = hovered;
                _dirty = true;
                break;

            case EVREventType.VREvent_MouseButtonUp:
                if (TileAt(vrEvent.data.mouse) is { } tile) Press(tile);
                break;

            case EVREventType.VREvent_KeyboardDone:
                OnKeyboardDone();
                break;

            case EVREventType.VREvent_KeyboardClosed:
                _keyboardFor = null;
                break;
        }
    }

    /// <summary>Кнопка под лучом. У SteamVR начало координат внизу слева, а у панели вверху.</summary>
    private PanelTile? TileAt(VREvent_Mouse_t mouse) => PanelLayout.HitTest(_view, mouse.x, PanelLayout.Height - mouse.y);

    /// <summary>Нажатие на кнопку.</summary>
    private void Press(PanelTile tile)
    {
        var state = Volatile.Read(ref _state);
        if (tile.Id == PanelLayout.EditStatus)
        {
            OpenKeyboard(tile.Id, Loc.T("Личный статус", "Personal status"), state.Status);
            return;
        }

        if (tile.Id == PanelLayout.SendChat)
        {
            OpenKeyboard(tile.Id, Loc.T("Сообщение", "Message"), string.Empty);
            return;
        }

        Run(() => client.PressAsync(tile.Id, state));
    }

    /// <summary>Открывает клавиатуру SteamVR.</summary>
    private void OpenKeyboard(string purpose, string description, string text)
    {
        var result = Valve.VR.OpenVR.Overlay.ShowKeyboardForOverlay(_overlay, (int)EGamepadTextInputMode.k_EGamepadTextInputModeNormal,
            (int)EGamepadTextInputLineMode.k_EGamepadTextInputLineModeSingleLine, 0, description, MaxInputLength, text, 0);
        if (result == EVROverlayError.None) _keyboardFor = purpose;
        else log($"Could not open the keyboard: {result}");
    }

    /// <summary>Пользователь закончил ввод.</summary>
    private void OnKeyboardDone()
    {
        var buffer = new StringBuilder(MaxInputLength * 4);
        Valve.VR.OpenVR.Overlay.GetKeyboardText(buffer, (uint)buffer.Capacity);
        var text = buffer.ToString();
        var purpose = _keyboardFor;
        _keyboardFor = null;

        if (purpose == PanelLayout.EditStatus) Run(() => client.SetStatusAsync(text));
        else if (purpose == PanelLayout.SendChat && text.Length > 0) Run(() => client.SendChatAsync(text));
    }

    /// <summary>Выполняет действие в фоне и сразу обновляет панель.</summary>
    private void Run(Func<Task> action) => _ = Task.Run(async () =>
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or HubCallException or InvalidOperationException)
        {
            log($"Action failed: {ex.Message}");
        }

        RefreshInBackground();
    });
}
