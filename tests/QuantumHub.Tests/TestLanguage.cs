using System.Runtime.CompilerServices;
using QuantumHub.Contracts.Ui;

namespace QuantumHub.Tests;

/// <summary>Тесты сравнивают тексты, поэтому не должны зависеть от языка машины, на которой идут.</summary>
internal static class TestLanguage
{
    [ModuleInitializer]
    internal static void Set() => Loc.Language = Loc.Russian;
}
