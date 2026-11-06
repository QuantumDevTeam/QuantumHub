using QuantumHub.Plugin.PicoAntiSleep;
using Xunit;

namespace QuantumHub.Tests;

/// <summary>Логика работы со шлемом. Всё на подделке adb: настоящий adb здесь не запускается.</summary>
public class PicoAntiSleepTests
{
    private const string Wifi = "192.168.1.150:5555";

    private sealed class FakeAdb : IAdbRunner
    {
        public List<string> Commands { get; } = [];
        public bool ServerStarted { get; private set; }
        public HashSet<string> Online { get; } = [];
        public string? UsbSerial { get; set; }
        public string UsbIp { get; set; } = "192.168.1.150";
        public bool TcpipEnabled { get; set; } = true;
        public int FailWakes { get; set; }
        public int Wakes { get; private set; }
        public TaskCompletionSource? OnWakes { get; set; }
        public int WakeTarget { get; set; }

        public Task StartServerAsync(CancellationToken token)
        {
            ServerStarted = true;
            return Task.CompletedTask;
        }

        public Task<AdbResult> RunAsync(string arguments, CancellationToken token)
        {
            Commands.Add(arguments);
            return Task.FromResult(Handle(arguments));
        }

        private AdbResult Handle(string arguments)
        {
            if (arguments == "devices")
            {
                var lines = Online.Select(serial => $"{serial}\tdevice").ToList();
                if (UsbSerial is not null) lines.Add($"{UsbSerial}\tdevice");
                return new AdbResult(0, "List of devices attached\n" + string.Join('\n', lines) + "\n");
            }

            if (arguments.StartsWith("connect ", StringComparison.Ordinal))
            {
                if (TcpipEnabled) Online.Add(arguments[8..]);
                return new AdbResult(0, string.Empty);
            }

            if (arguments.StartsWith("disconnect ", StringComparison.Ordinal))
            {
                Online.Remove(arguments[11..]);
                return new AdbResult(0, string.Empty);
            }

            if (arguments.EndsWith("tcpip 5555", StringComparison.Ordinal))
            {
                TcpipEnabled = true;
                return new AdbResult(0, string.Empty);
            }

            if (arguments.Contains("addr show wlan0", StringComparison.Ordinal))
                return new AdbResult(0, $"3: wlan0: <UP>\n    inet {UsbIp}/24 brd 192.168.1.255 scope global wlan0\n");

            if (arguments.EndsWith("keyevent 224", StringComparison.Ordinal))
            {
                if (FailWakes > 0)
                {
                    FailWakes--;
                    return new AdbResult(1, "error: device offline");
                }

                if (++Wakes >= WakeTarget) OnWakes?.TrySetResult();
                return new AdbResult(0, string.Empty);
            }

            return new AdbResult(1, "unknown");
        }
    }

    private static Task NoDelay(TimeSpan time, CancellationToken token) =>
        token.IsCancellationRequested ? Task.FromCanceled(token) : Task.Delay(1, token);

    private readonly FakeAdb _fake = new();

    private AdbClient Client() => new(_fake, NoDelay);

    [Theory]
    [InlineData("192.168.1.150", Wifi)]
    [InlineData(" 192.168.1.150:5555 ", Wifi)]
    [InlineData("192.168.1.150:6000", "192.168.1.150:6000")]
    public void Normalizes_the_address_without_doubling_the_port(string input, string expected) =>
        Assert.Equal(expected, AdbClient.NormalizeAddress(input));

    [Fact]
    public void Parses_adb_devices()
    {
        var devices = AdbClient.ParseDevices("List of devices attached\r\nPA7L\tdevice\r\n192.168.1.150:5555\toffline\r\n\r\n");

        Assert.Equal(2, devices.Count);
        Assert.True(devices[0] is { IsUsb: true, IsReady: true });
        Assert.True(devices[1] is { IsUsb: false, IsReady: false });
    }

    [Fact]
    public async Task Starts_the_server_before_anything_else()
    {
        await Client().ConnectAsync(Wifi, CancellationToken.None);

        Assert.True(_fake.ServerStarted);
    }

    [Fact]
    public async Task Reuses_a_live_connection()
    {
        _fake.Online.Add(Wifi);

        Assert.Equal(Wifi, await Client().ConnectAsync(Wifi, CancellationToken.None));
        Assert.DoesNotContain(_fake.Commands, command => command.StartsWith("connect", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connects_over_wifi_without_touching_the_cable()
    {
        _fake.UsbSerial = "PA7L";

        Assert.Equal(Wifi, await Client().ConnectAsync(Wifi, CancellationToken.None));
        Assert.DoesNotContain(_fake.Commands, command => command.Contains("tcpip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enables_network_adb_over_the_cable_and_learns_the_real_address()
    {
        _fake.TcpipEnabled = false;
        _fake.UsbSerial = "PA7L";
        _fake.UsbIp = "192.168.1.77";

        var serial = await Client().ConnectAsync("192.168.1.151", CancellationToken.None);

        Assert.Equal("192.168.1.77:5555", serial);
        Assert.Contains("-s PA7L tcpip 5555", _fake.Commands);
    }

    [Fact]
    public async Task Reports_no_connection_without_wifi_or_cable()
    {
        _fake.TcpipEnabled = false;

        Assert.Null(await Client().ConnectAsync(Wifi, CancellationToken.None));
    }

    [Fact]
    public async Task Names_the_device_so_a_plugged_in_cable_does_not_break_the_command()
    {
        await Client().WakeAsync(Wifi, CancellationToken.None);

        Assert.Equal($"-s {Wifi} shell input keyevent 224", _fake.Commands[0]);
    }

    private AntiSleepEngine Engine() =>
        new(Client(), () => new AntiSleepOptions(Wifi, TimeSpan.FromMilliseconds(1)), NoDelay, _ => { });

    [Fact]
    public async Task Sends_the_first_signal_at_once_and_keeps_going()
    {
        _fake.WakeTarget = 3;
        _fake.OnWakes = new TaskCompletionSource();
        var engine = Engine();

        await engine.StartAsync();
        await _fake.OnWakes.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync();

        Assert.False(engine.IsRunning);
        Assert.Equal("Выключен", engine.Status);
    }

    [Fact]
    public async Task Reconnects_after_a_lost_connection_and_never_gives_up()
    {
        _fake.FailWakes = 25;
        _fake.WakeTarget = 1;
        _fake.OnWakes = new TaskCompletionSource();
        var engine = Engine();

        await engine.StartAsync();
        await _fake.OnWakes.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await engine.StopAsync();

        Assert.True(_fake.Commands.Count(command => command == $"disconnect {Wifi}") >= 25);
    }

    [Fact]
    public async Task Disconnects_when_stopped()
    {
        _fake.WakeTarget = 1;
        _fake.OnWakes = new TaskCompletionSource();
        var engine = Engine();

        await engine.StartAsync();
        await _fake.OnWakes.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync();

        Assert.Contains($"disconnect {Wifi}", _fake.Commands);
    }

    [Fact]
    public async Task Starting_twice_runs_one_loop()
    {
        _fake.WakeTarget = 1;
        _fake.OnWakes = new TaskCompletionSource();
        var engine = Engine();

        await engine.StartAsync();
        await engine.StartAsync();
        await _fake.OnWakes.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await engine.StopAsync();

        Assert.False(engine.IsRunning);
    }

    [Fact]
    public void Finds_adb_from_the_setting_as_a_file_or_a_folder()
    {
        var dir = Directory.CreateTempSubdirectory("qh-adb").FullName;
        var adb = Path.Combine(dir, "adb.exe");
        File.WriteAllText(adb, string.Empty);
        try
        {
            Assert.Equal(adb, AdbLocator.Find(adb, "missing"));
            Assert.Equal(adb, AdbLocator.Find(dir, "missing"));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
