using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace QuantumHub.Plugin.AudioDevices;

/// <summary>
/// Настоящий поток записи. Сам звук никуда не идёт и не сохраняется: он нужен только для того, чтобы Windows считала
/// уровень. Счётчик устройства показывает настоящий сигнал, только пока поток кем-то открыт.
/// </summary>
public sealed class WasapiMicMeter : IMicMeter
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private volatile bool _failed;

    /// <summary>Открывает поток.</summary>
    /// <param name="deviceId">Id микрофона.</param>
    public WasapiMicMeter(string deviceId)
    {
        _device = _enumerator.GetDevice(deviceId);
        _capture = new WasapiCapture(_device);
        _capture.RecordingStopped += (_, _) => _failed = true;
        try
        {
            _capture.StartRecording();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    public bool Failed => _failed;

    /// <inheritdoc/>
    public double ReadPeak()
    {
        try
        {
            return _device.AudioMeterInformation.MasterPeakValue;
        }
        catch (COMException)
        {
            _failed = true; // Устройство пропало.
            return 0;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _capture.Dispose();
        _device.Dispose();
        _enumerator.Dispose();
    }
}
