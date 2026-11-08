using System.Globalization;
using System.Text;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Cli;

/// <summary>Командная строка хаба: <c>quantumhub status</c>, <c>quantumhub plugins</c> и другие. Список в <see cref="CliApp"/>.</summary>
internal static class Program
{
    /// <summary>Точка входа.</summary>
    /// <param name="args">Аргументы.</param>
    /// <returns>Смотри <see cref="CliApp"/>.</returns>
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Loc.Language = Loc.Resolve(Loc.Auto, CultureInfo.CurrentUICulture);

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        return await new CliApp(Console.Out, Console.Error).RunAsync(args, stop.Token);
    }
}
