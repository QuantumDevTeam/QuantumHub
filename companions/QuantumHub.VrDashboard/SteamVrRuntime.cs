using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Valve.VR;

namespace QuantumHub.VrDashboard;

/// <summary>Настоящий SteamVR. Здесь только вызовы OpenVR, логика лежит в <see cref="OverlayHost"/>.</summary>
public sealed class SteamVrRuntime : IOverlayRuntime
{
    private const string OverlayKey = "quantumdevteam.quantumhub.dashboard";
    private const float WidthInMeters = 2.2f;
    private const uint MaxInputLength = 144;
    /// <summary>Флаг клавиатуры: Valve советует его для окон ввода поверх вкладки.</summary>
    private const uint KeyboardModal = 1;

    private static readonly uint EventSize = (uint)Marshal.SizeOf<VREvent_t>();

    private ulong _overlay;

    /// <inheritdoc/>
    public string? Start(string icon)
    {
        var error = EVRInitError.None;
        Valve.VR.OpenVR.Init(ref error, EVRApplicationType.VRApplication_Overlay, string.Empty);
        if (error != EVRInitError.None) return $"SteamVR is unavailable: {error}";

        ulong thumbnail = 0;
        var result = Valve.VR.OpenVR.Overlay.CreateDashboardOverlay(OverlayKey, "QuantumHub", ref _overlay, ref thumbnail);
        if (result != EVROverlayError.None)
        {
            Valve.VR.OpenVR.Shutdown();
            return $"Could not create the overlay: {result}";
        }

        if (File.Exists(icon)) Valve.VR.OpenVR.Overlay.SetOverlayFromFile(thumbnail, icon);

        Valve.VR.OpenVR.Overlay.SetOverlayWidthInMeters(_overlay, WidthInMeters);
        Valve.VR.OpenVR.Overlay.SetOverlayInputMethod(_overlay, VROverlayInputMethod.Mouse);
        var scale = new HmdVector2_t { v0 = PanelLayout.Width, v1 = PanelLayout.Height };
        Valve.VR.OpenVR.Overlay.SetOverlayMouseScale(_overlay, ref scale);
        return null;
    }

    /// <inheritdoc/>
    public void Stop() => Valve.VR.OpenVR.Shutdown();

    /// <inheritdoc/>
    public bool IsVisible => Valve.VR.OpenVR.Overlay.IsOverlayVisible(_overlay);

    /// <inheritdoc/>
    public bool TryPoll(out OverlayInput input)
    {
        var vrEvent = new VREvent_t();
        while (Valve.VR.OpenVR.System.PollNextEvent(ref vrEvent, EventSize))
        {
            if (vrEvent.eventType != (uint)EVREventType.VREvent_Quit) continue;
            Valve.VR.OpenVR.System.AcknowledgeQuit_Exiting();
            input = new OverlayInput(OverlayInputKind.Quit);
            return true;
        }

        while (Valve.VR.OpenVR.Overlay.PollNextOverlayEvent(_overlay, ref vrEvent, EventSize))
        {
            var mouse = vrEvent.data.mouse;
            OverlayInputKind? kind = (EVREventType)vrEvent.eventType switch
            {
                EVREventType.VREvent_MouseMove => OverlayInputKind.Move,
                EVREventType.VREvent_MouseButtonUp => OverlayInputKind.Press,
                EVREventType.VREvent_KeyboardDone => OverlayInputKind.KeyboardDone,
                EVREventType.VREvent_KeyboardClosed => OverlayInputKind.KeyboardClosed,
                _ => null,
            };
            if (kind is null) continue;

            input = new OverlayInput(kind.Value, mouse.x, mouse.y);
            return true;
        }

        input = default;
        return false;
    }

    /// <inheritdoc/>
    public string? ShowPixels(byte[] rgba)
    {
        var handle = GCHandle.Alloc(rgba, GCHandleType.Pinned);
        try
        {
            var result = Valve.VR.OpenVR.Overlay.SetOverlayRaw(_overlay, handle.AddrOfPinnedObject(), PanelLayout.Width, PanelLayout.Height, 4);
            return result == EVROverlayError.None ? null : result.ToString();
        }
        finally
        {
            handle.Free();
        }
    }

    /// <inheritdoc/>
    public string? ShowFile(string path)
    {
        var result = Valve.VR.OpenVR.Overlay.SetOverlayFromFile(_overlay, path);
        return result == EVROverlayError.None ? null : result.ToString();
    }

    /// <inheritdoc/>
    public string? OpenKeyboard(string description, string text)
    {
        var result = Valve.VR.OpenVR.Overlay.ShowKeyboardForOverlay(_overlay, (int)EGamepadTextInputMode.k_EGamepadTextInputModeNormal,
            (int)EGamepadTextInputLineMode.k_EGamepadTextInputLineModeSingleLine, KeyboardModal, description, MaxInputLength, text, 0);
        return result == EVROverlayError.None ? null : result.ToString();
    }

    /// <inheritdoc/>
    public string KeyboardText()
    {
        var buffer = new StringBuilder((int)MaxInputLength * 4);
        Valve.VR.OpenVR.Overlay.GetKeyboardText(buffer, (uint)buffer.Capacity);
        return buffer.ToString();
    }

    /// <summary>Проверяет, что библиотека OpenVR загружается и видит SteamVR. Сам SteamVR при этом не запускается.</summary>
    /// <returns>Отчёт.</returns>
    public static string Check() =>
        $"openvr_api loaded. Runtime installed: {Valve.VR.OpenVR.IsRuntimeInstalled()}. Event size: {EventSize}. Overlay interface: {Valve.VR.OpenVR.IVROverlay_Version}.";
}
