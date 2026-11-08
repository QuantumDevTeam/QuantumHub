using QuantumHub.Contracts.Ui;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using Microsoft.Win32;

namespace QuantumHub.Setup;

/// <summary>Всё, что связывает программу с Windows: ярлык, список программ, автозапуск и файлы .qhp. Только для текущего пользователя.</summary>
internal static class ShellIntegration
{
    /// <summary>Название программы.</summary>
    private const string Product = "QuantumHub";
    /// <summary>Ключ записи в списке установленных программ.</summary>
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\QuantumHub";
    /// <summary>Ключ автозапуска.</summary>
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    /// <summary>Имя значения автозапуска хаба. То же, что в настройках GUI.</summary>
    private const string RunHubValue = "QuantumHub";
    /// <summary>Имя значения автозапуска GUI. То же, что в настройках GUI.</summary>
    private const string RunGuiValue = "QuantumHub GUI";
    /// <summary>Ключ типа файлов плагинов.</summary>
    private const string FileClass = @"Software\Classes\QuantumHub.Plugin";
    /// <summary>Ключ расширения файлов плагинов.</summary>
    private const string FileExtension = @"Software\Classes\.qhp";

    /// <summary>Ключ схемы ссылок quantumhub://.</summary>
    private const string LinkClass = @"Software\Classes\quantumhub";
    /// <summary>Ключ переменных среды пользователя.</summary>
    private const string EnvironmentKey = "Environment";
    /// <summary>Папка CLI внутри программы.</summary>
    private const string CliFolder = "cli";

    /// <summary>Имя деинсталлятора в папке программы.</summary>
    public const string UninstallerName = "Uninstall.exe";

    /// <summary>Сообщение проводнику, что сопоставления файлов изменились.</summary>
    private const int ShcneAssocChanged = 0x08000000;

    /// <summary>Папка установки по умолчанию: пользователю туда можно писать без прав администратора.</summary>
    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Product);

    /// <summary>Путь к ярлыку в меню «Пуск».</summary>
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), $"{Product}.lnk");

    /// <summary>Папка уже установленной программы.</summary>
    /// <returns>Путь или <see langword="null"/>.</returns>
    public static string? InstalledDirectory()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
        return key?.GetValue("InstallLocation") as string;
    }

    /// <summary>Регистрирует программу в Windows.</summary>
    /// <param name="directory">Папка программы.</param>
    /// <param name="version">Версия.</param>
    /// <param name="autostart">Запускать ли вместе с Windows.</param>
    public static void Register(string directory, string version, bool autostart)
    {
        var gui = Path.Combine(directory, "gui", "QuantumHub.Gui.exe");
        var hub = Path.Combine(directory, Installer.ServiceExe);
        var uninstaller = Path.Combine(directory, UninstallerName);

        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", Product);
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "QuantumDevTeam");
            key.SetValue("InstallLocation", directory);
            key.SetValue("DisplayIcon", gui);
            key.SetValue("UninstallString", $"\"{uninstaller}\"");
            key.SetValue("QuietUninstallString", $"\"{uninstaller}\" --silent");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(DirectorySize(directory) / 1024), RegistryValueKind.DWord);
        }

        using (var key = Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (autostart)
            {
                key.SetValue(RunHubValue, $"\"{hub}\" --watch");
                key.SetValue(RunGuiValue, $"\"{gui}\" --minimized");
            }
            else
            {
                key.DeleteValue(RunHubValue, false);
                key.DeleteValue(RunGuiValue, false);
            }
        }

        using (var key = Registry.CurrentUser.CreateSubKey(FileExtension)) key.SetValue(null, "QuantumHub.Plugin");
        using (var key = Registry.CurrentUser.CreateSubKey(FileClass))
        {
            key.SetValue(null, Loc.T("Плагин QuantumHub", "QuantumHub plugin"));
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{gui}\",0");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue(null, $"\"{gui}\" --install \"%1\"");
        }

        using (var key = Registry.CurrentUser.CreateSubKey(LinkClass))
        {
            key.SetValue(null, "URL:QuantumHub");
            key.SetValue("URL Protocol", string.Empty);
            using var icon = key.CreateSubKey("DefaultIcon");
            icon.SetValue(null, $"\"{gui}\",0");
            using var command = key.CreateSubKey(@"shell\open\command");
            command.SetValue(null, $"\"{gui}\" --link \"%1\"");
        }

        SetUserPath(current => PathList.Add(current, Path.Combine(directory, CliFolder)));
        CreateShortcut(ShortcutPath, gui);
        SHChangeNotify(ShcneAssocChanged, 0, 0, 0);
    }

    /// <summary>Меняет PATH пользователя и сообщает об этом запущенным программам.</summary>
    private static void SetUserPath(Func<string, string> change)
    {
        using var key = Registry.CurrentUser.CreateSubKey(EnvironmentKey);

        // Без этого флага %USERPROFILE% и подобное превратились бы в готовые пути навсегда.
        var current = key.GetValue("Path", string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
        var updated = change(current);
        if (updated == current) return;

        if (updated.Length == 0) key.DeleteValue("Path", false);
        else key.SetValue("Path", updated, RegistryValueKind.ExpandString);
        SendMessageTimeoutW(HwndBroadcast, WmSettingChange, 0, EnvironmentKey, SmtoAbortIfHung, SettingChangeTimeoutMs, out _);
    }

    /// <summary>Убирает всё, что добавил <see cref="Register"/>.</summary>
    /// <param name="directory">Папка программы.</param>
    public static void Unregister(string directory)
    {
        SetUserPath(current => PathList.Remove(current, Path.Combine(directory, CliFolder)));
        Registry.CurrentUser.DeleteSubKeyTree(LinkClass, false);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
        Registry.CurrentUser.DeleteSubKeyTree(FileClass, false);
        Registry.CurrentUser.DeleteSubKeyTree(FileExtension, false);
        using (var key = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            key?.DeleteValue(RunHubValue, false);
            key?.DeleteValue(RunGuiValue, false);
        }

        File.Delete(ShortcutPath);
        SHChangeNotify(ShcneAssocChanged, 0, 0, 0);
    }

    /// <summary>Включён ли автозапуск. При обновлении выбор пользователя сохраняется.</summary>
    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunGuiValue) is not null || key?.GetValue(RunHubValue) is not null;
    }

    /// <summary>Размер папки в байтах.</summary>
    private static long DirectorySize(string directory) =>
        new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories).Sum(file => file.Length);

    /// <summary>Создаёт ярлык.</summary>
    private static void CreateShortcut(string path, string target)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
        link.SetDescription(Product);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ((IPersistFile)link).Save(path, true);
        Marshal.ReleaseComObject(link);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, nint item1, nint item2);

    /// <summary>Адрес сообщения для всех окон.</summary>
    private const nint HwndBroadcast = 0xFFFF;
    /// <summary>Сообщение о смене настроек.</summary>
    private const uint WmSettingChange = 0x001A;
    /// <summary>Не ждать зависшие окна.</summary>
    private const uint SmtoAbortIfHung = 2;
    /// <summary>Сколько ждать каждое окно.</summary>
    private const uint SettingChangeTimeoutMs = 1000;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageTimeoutW(nint window, uint message, nint wParam, string lParam, uint flags, uint timeout, out nint result);

    /// <summary>COM-класс ярлыка.</summary>
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    /// <summary>Интерфейс ярлыка. Порядок методов задан таблицей COM, поэтому в нём есть и ненужные.</summary>
    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(nint file, int size, nint data, uint flags);
        void GetIDList(out nint list);
        void SetIDList(nint list);
        void GetDescription(nint name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory(nint dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments(nint args, int size);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short key);
        void SetHotkey(short key);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation(nint path, int size, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
