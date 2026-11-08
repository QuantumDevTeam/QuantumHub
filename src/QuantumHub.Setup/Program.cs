using QuantumHub.Contracts.Ui;
using System.Diagnostics;

namespace QuantumHub.Setup;

/// <summary>
/// Установщик и деинсталлятор в одном exe. С архивом в конце файла он устанавливает программу, без него удаляет.
/// Работает без прав администратора и без установленного .NET: сам он собран одним файлом вместе с рантаймом.
/// </summary>
/// <remarks>
/// Аргументы: <c>--silent</c> работает без окон, <c>--dir</c> задаёт папку программы, <c>--no-launch</c> не запускает
/// программу после установки, <c>--no-autostart</c> не включает автозапуск.
/// </remarks>
internal static class Program
{
    /// <summary>Основной язык в коде языка Windows.</summary>
    private const int PrimaryLanguageMask = 0x3FF;
    /// <summary>Код русского языка.</summary>
    private const int RussianLanguageId = 0x19;

    /// <summary>Внутренний аргумент: деинсталлятор уже работает из временной папки.</summary>
    private const string FromTempArgument = "--from-temp";

    /// <summary>Точка входа.</summary>
    /// <param name="args">Аргументы командной строки.</param>
    /// <returns>0 при успехе, 1 при ошибке, 2 при отмене.</returns>
    [STAThread]
    private static int Main(string[] args)
    {
        var silent = args.Contains("--silent");

        // Установщик собран без данных о культурах, поэтому язык спрашиваем у Windows напрямую.
        Loc.Language = (GetUserDefaultUILanguage() & PrimaryLanguageMask) == RussianLanguageId ? Loc.Russian : Loc.English;

        try
        {
            var exe = Environment.ProcessPath!;
            using var payload = Payload.Open(exe);
            return payload is null ? Uninstall(exe, args, silent) : Install(exe, payload, args, silent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (silent) Console.Error.WriteLine(ex);
            else Dialogs.Error(Loc.T($"Не получилось. Закрой QuantumHub и попробуй ещё раз.\n\n{ex.Message}", $"That did not work. Close QuantumHub and try again.\n\n{ex.Message}"));
            return 1;
        }
    }

    /// <summary>Устанавливает или обновляет программу.</summary>
    private static int Install(string exe, Payload payload, string[] args, bool silent)
    {
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        var existing = ShellIntegration.InstalledDirectory();
        var installer = new Installer(Value(args, "--dir") ?? existing ?? ShellIntegration.DefaultDirectory);

        // Обновление не должно менять выбор, сделанный пользователем в настройках.
        var autostart = !args.Contains("--no-autostart") && (existing is null || ShellIntegration.IsAutostartEnabled());
        if (!silent)
        {
            var (accepted, option) = Dialogs.Confirm(
                existing is null ? Loc.T($"Установить QuantumHub {version}?", $"Install QuantumHub {version}?") : Loc.T($"Обновить QuantumHub до версии {version}?", $"Update QuantumHub to {version}?"),
                Loc.T($"Папка: {installer.Directory}\nПрава администратора и .NET не нужны. Настройки и твои плагины сохранятся.", $"Folder: {installer.Directory}\nNo administrator rights or .NET needed. Your settings and plugins are kept."),
                existing is null ? Loc.T("Установить", "Install") : Loc.T("Обновить", "Update"),
                Loc.T("Запускать вместе с Windows", "Start with Windows"), autostart);
            if (!accepted) return 2;
            autostart = option;
        }

        async Task Work(Action<double> progress)
        {
            await RunningApp.StopAsync(installer.Directory);
            installer.Install(payload.Stream, progress);
            payload.CopyProgramTo(exe, Path.Combine(installer.Directory, ShellIntegration.UninstallerName));
            ShellIntegration.Register(installer.Directory, version, autostart);
        }

        if (silent) Work(_ => { }).GetAwaiter().GetResult();
        else Dialogs.Progress(Loc.T("Устанавливаю QuantumHub…", "Installing QuantumHub…"), Work);

        if (!args.Contains("--no-launch"))
        {
            var gui = Path.Combine(installer.Directory, "gui", "QuantumHub.Gui.exe");
            Process.Start(new ProcessStartInfo(gui) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(gui) })?.Dispose();
        }

        return 0;
    }

    /// <summary>Удаляет программу.</summary>
    private static int Uninstall(string exe, string[] args, bool silent)
    {
        var installer = new Installer(Value(args, "--dir") ?? Path.GetDirectoryName(exe)!);
        if (!installer.IsInstalled)
        {
            if (!silent) Dialogs.Error(Loc.T("Этот файл нужно запускать из папки установленной программы.", "Run this file from the folder of the installed program."));
            return 1;
        }

        // Exe не может удалить сам себя, поэтому работу делает его копия во временной папке.
        if (!args.Contains(FromTempArgument))
        {
            var copy = Path.Combine(Path.GetTempPath(), $"QuantumHub.Uninstall.{Guid.NewGuid():N}.exe");
            File.Copy(exe, copy);
            var start = new ProcessStartInfo(copy) { UseShellExecute = false };
            foreach (var argument in args) start.ArgumentList.Add(argument);
            start.ArgumentList.Add("--dir");
            start.ArgumentList.Add(installer.Directory);
            start.ArgumentList.Add(FromTempArgument);
            Process.Start(start)?.Dispose();
            return 0;
        }

        if (!silent)
        {
            var (accepted, _) = Dialogs.Confirm(Loc.T("Удалить QuantumHub?", "Remove QuantumHub?"),
                Loc.T($"Программа будет удалена из папки {installer.Directory}.\nНастройки останутся в профиле пользователя и пригодятся, если поставишь её снова.", $"The program will be removed from {installer.Directory}.\nYour settings stay in your user profile in case you install it again."),
                Loc.T("Удалить", "Remove"), null, false);
            if (!accepted)
            {
                RemoveSelfLater(exe);
                return 2;
            }
        }

        async Task Work(Action<double> progress)
        {
            await RunningApp.StopAsync(installer.Directory);
            ShellIntegration.Unregister(installer.Directory);
            installer.Uninstall();
            progress(1);
        }

        if (silent) Work(_ => { }).GetAwaiter().GetResult();
        else Dialogs.Progress(Loc.T("Удаляю QuantumHub…", "Removing QuantumHub…"), Work);

        RemoveSelfLater(exe);
        return 0;
    }

    /// <summary>Удаляет временную копию деинсталлятора после её завершения.</summary>
    private static void RemoveSelfLater(string exe) =>
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & del /q \"{exe}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })?.Dispose();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    /// <summary>Значение аргумента командной строки.</summary>
    private static string? Value(string[] args, string name)
    {
        var index = Array.LastIndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
