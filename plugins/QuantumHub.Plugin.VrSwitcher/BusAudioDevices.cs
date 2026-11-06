using QuantumHub.Contracts.Bus;

namespace QuantumHub.Plugin.VrSwitcher;

/// <summary>Звук через возможность audio.devices. Кто её предоставляет, этому плагину неважно.</summary>
/// <param name="bus">Шина хаба.</param>
public sealed class BusAudioDevices(IHubBus bus) : IAudioDevices
{
    private const string Target = "cap:audio.devices";

    private sealed record DeviceList(List<AudioDevice> Playback, List<AudioDevice> Recording);

    private sealed record Current(AudioDevice? Playback, AudioDevice? Recording);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AudioDevice>> ListAsync(AudioFlow flow)
    {
        var list = (await bus.CallAsync(Target, "devices.list")).AsJson<DeviceList>();
        return (flow == AudioFlow.Playback ? list?.Playback : list?.Recording) ?? [];
    }

    /// <inheritdoc/>
    public async Task<string?> GetDefaultAsync(AudioFlow flow)
    {
        var current = (await bus.CallAsync(Target, "devices.current")).AsJson<Current>();
        return (flow == AudioFlow.Playback ? current?.Playback : current?.Recording)?.Id;
    }

    /// <inheritdoc/>
    public async Task SetDefaultAsync(string deviceId) =>
        await bus.CallAsync(Target, "devices.set", HubPayload.Json(new { id = deviceId }));
}
