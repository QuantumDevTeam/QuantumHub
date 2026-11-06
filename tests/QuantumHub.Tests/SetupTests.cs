using System.IO.Compression;
using QuantumHub.Contracts.Plugin;
using QuantumHub.Setup;
using Xunit;

namespace QuantumHub.Tests;

/// <summary>Файловая часть установщика на временных папках. Реестр, ярлыки и процессы здесь не затрагиваются.</summary>
public sealed class SetupTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("qh-setup-").FullName;

    private string App => Path.Combine(_root, "app");

    /// <summary>Архив с указанными файлами.</summary>
    private string Archive(params string[] files)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(file).Open());
            writer.Write(file);
        }

        return path;
    }

    private void Install(string archive)
    {
        using var stream = File.OpenRead(archive);
        new Installer(App).Install(stream);
    }

    private void Write(string relative)
    {
        var path = Path.Combine(App, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "old");
    }

    private bool Has(string relative) => File.Exists(Path.Combine(App, relative));

    [Fact]
    public void A_fresh_install_unpacks_everything_and_reports_progress()
    {
        var reported = new List<double>();
        using (var stream = File.OpenRead(Archive(Installer.ServiceExe, "gui/QuantumHub.Gui.exe", "plugins/qh.audio/plugin.jsonc")))
            new Installer(App).Install(stream, reported.Add);

        Assert.True(Has("gui/QuantumHub.Gui.exe"));
        Assert.True(Has("plugins/qh.audio/plugin.jsonc"));
        Assert.Equal(1.0, reported[^1]);
    }

    [Fact]
    public void An_update_replaces_the_program_but_keeps_plugins_the_user_installed()
    {
        Write(Installer.ServiceExe);
        Write("gui/stale.dll");
        Write("dotnet/shared/old.dll");
        Write("plugins/qh.audio/stale.dll");
        Write("plugins/acme.mine/plugin.jsonc");

        Install(Archive(Installer.ServiceExe, "gui/new.dll", "plugins/qh.audio/plugin.jsonc"));

        Assert.True(Has("gui/new.dll"));
        Assert.True(Has("plugins/qh.audio/plugin.jsonc"));
        Assert.False(Has("gui/stale.dll"));
        Assert.False(Has("dotnet/shared/old.dll"));
        Assert.False(Has("plugins/qh.audio/stale.dll"));
        Assert.True(Has("plugins/acme.mine/plugin.jsonc"));
    }

    [Fact]
    public void A_folder_that_is_not_ours_is_never_cleaned()
    {
        // Папку могли выбрать по ошибке, и файлы в ней чужие.
        Write("documents/thesis.docx");

        Install(Archive(Installer.ServiceExe));
        Assert.True(Has("documents/thesis.docx"));
    }

    [Fact]
    public void Uninstall_touches_nothing_in_a_folder_that_is_not_ours()
    {
        Write("documents/thesis.docx");
        new Installer(App).Uninstall();
        Assert.True(Has("documents/thesis.docx"));
    }

    [Fact]
    public void Uninstall_removes_the_whole_folder()
    {
        Install(Archive(Installer.ServiceExe, "gui/QuantumHub.Gui.exe"));
        new Installer(App).Uninstall();
        Assert.False(Directory.Exists(App));
    }

    [Fact]
    public void An_archive_cannot_write_outside_the_program_folder()
    {
        var archive = Archive("../escaped.txt");
        Assert.Throws<InvalidDataException>(() => Install(archive));
        Assert.False(File.Exists(Path.Combine(_root, "escaped.txt")));
    }

    [Fact]
    public void The_archive_survives_the_trip_through_the_exe_tail()
    {
        var exe = Path.Combine(_root, "setup.exe");
        File.WriteAllBytes(exe, new byte[12345]);
        Assert.Null(Payload.Open(exe));

        Payload.Append(exe, Archive(Installer.ServiceExe, "gui/QuantumHub.Gui.exe"));

        using var payload = Payload.Open(exe);
        Assert.NotNull(payload);
        Assert.Equal(12345, payload.Offset);

        new Installer(App).Install(payload.Stream);
        Assert.True(Has("gui/QuantumHub.Gui.exe"));

        // Копия без архива становится деинсталлятором: в ней архива уже нет.
        var uninstaller = Path.Combine(_root, "uninstall.exe");
        payload.CopyProgramTo(exe, uninstaller);
        Assert.Equal(12345, new FileInfo(uninstaller).Length);
        Assert.Null(Payload.Open(uninstaller));
    }

    [Fact]
    public void Companions_find_the_runtime_the_hub_runs_on()
    {
        var root = Path.Combine(_root, "dotnet");
        var version = Path.Combine(root, "shared", "Microsoft.NETCore.App", "10.0.8");
        Directory.CreateDirectory(version);

        Assert.Null(CompanionProcess.RuntimeRoot(version));

        Directory.CreateDirectory(Path.Combine(root, "host", "fxr"));
        Assert.Equal(root, CompanionProcess.RuntimeRoot(version));
        Assert.Equal(root, CompanionProcess.RuntimeRoot(version + Path.DirectorySeparatorChar));
        Assert.Null(CompanionProcess.RuntimeRoot(string.Empty));
    }

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(_root, true);
}
