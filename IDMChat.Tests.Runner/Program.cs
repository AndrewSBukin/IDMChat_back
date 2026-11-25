using System.Reflection;
using Xunit;

// Консольный раннер юнит-тестов сниппет-билдера (БЕЗ testhost/VSTest).
// Запуск: dotnet run --project IDMChat.Tests.Runner
// Нужен для сред, где testhost.exe падает с Win32Exception(5) в OpenProcess
// (запуск без прав администратора). Стандартный запуск — через `dotnet test` / Обозреватель тестов VS.
// Здесь мы вызываем те же [Fact]-методы напрямую и печатаем PASS/FAIL.

var testType = typeof(IDMChat.Tests.SearchSnippetBuilderTests);

var factMethods = testType
    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
    .Where(m => m.GetCustomAttribute<FactAttribute>() != null)
    .OrderBy(m => m.Name, StringComparer.Ordinal)
    .ToList();

Console.WriteLine($"Running {factMethods.Count} [Fact] tests from {testType.FullName}");
Console.WriteLine();

int passed = 0, failed = 0;
foreach (var method in factMethods)
{
    try
    {
        var instance = Activator.CreateInstance(testType);
        method.Invoke(instance, null);
        Console.WriteLine($"PASS  {method.Name}");
        passed++;
    }
    catch (TargetInvocationException ex)
    {
        Console.WriteLine($"FAIL  {method.Name}: {ex.InnerException?.Message ?? ex.Message}");
        failed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR {method.Name}: {ex.Message}");
        failed++;
    }
}

Console.WriteLine();
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;
