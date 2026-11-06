namespace QuantumHub.Plugin.AudioDevices;

/// <summary>Определяет микрофоны, подключённые по Bluetooth.</summary>
public static class BluetoothMic
{
    private static readonly string[] Names = ["Hands-Free", "Bluetooth"];

    /// <summary>Решает по свойствам устройства. Шина надёжнее, имя выручает, если свойств нет.</summary>
    /// <param name="enumerator">Шина: BTHENUM, BTHHFENUM, USB.</param>
    /// <param name="driverPath">Путь драйвера, в нём та же шина.</param>
    /// <param name="name">Имя устройства.</param>
    public static bool Detect(string? enumerator, string? driverPath, string name) =>
        enumerator?.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) == true ||
        driverPath?.Contains("BTH", StringComparison.OrdinalIgnoreCase) == true ||
        Names.Any(part => name.Contains(part, StringComparison.OrdinalIgnoreCase));
}
