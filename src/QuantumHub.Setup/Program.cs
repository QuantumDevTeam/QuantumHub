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
    /// <summary>Внутренний аргумент: деинсталлятор уже работает из временной папки.</summary>
    private const string FromTempArgument = "--from-temp";

    /// <summary>Точка входа.</summary>
    /// <param name="args">Аргументы командной строки.</param>
    /// <returns>0 при успехе, 1 при ошибке, 2 при отмене.</returns>
    [STAThread]
    private static int Main(string[] args)
    {
        var silent = args.Contains("--silent");
        try
        {
            var exe = Environment.ProcessPath!;
            using var payload = Payload.Open(exe);
            return payload is null ? Uninstall(exe, args, silent) : Install(exe, payload, args, silent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (silent) Console.Error.WriteLine(ex);
            else Dialogs.Error($"Не получилось. Закрой QuantumHub и попробуй ещё раз.\n\n{ex.Message}");
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
                existing is null ? $"Установить QuantumHub {version}?" : $"Обновить QuantumHub до версии {version}?",
                $"Папка: {installer.Directory}\nПрава администратора и .NET не нужны. Настройки и твои плагины сохранятся.",
                existing is null ? "Установить" : "Обновить",
                "Запускать вместе с Windows", autostart);
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
        else Dialogs.Progress("Устанавливаю QuantumHub…", Work);

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
            if (!silent) Dialogs.Error("Этот файл нужно запускать из папки установленной программы.");
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
            var (accepted, _) = Dialogs.Confirm("Удалить QuantumHub?",
                $"Программа будет удалена из папки {installer.Directory}.\nНастройки останутся в профиле пользователя и пригодятся, если поставишь её снова.",
                "Удалить", null, false);
            if (!accepted)
            {
                RemoveSelfLater(exe);
                return 2;
            }
        }

        async Task Work(Action<double> progress)
        {
            await RunningApp.StopAsync(installer.Directory);
            ShellIntegration.Unregister();
            installer.Uninstall();
            progress(1);
        }

        if (silent) Work(_ => { }).GetAwaiter().GetResult();
        else Dialogs.Progress("Удаляю QuantumHub…", Work);

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

    /// <summary>Значение аргумента командной строки.</summary>
    private static string? Value(string[] args, string name)
    {
        var index = Array.LastIndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
