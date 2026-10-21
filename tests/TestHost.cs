using System;
using System.IO;
using System.Reflection;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Text.Json;

namespace QuietTranslate;

internal static class TestHost
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length == 0) return 2;
        // Before any window/session is constructed, isolate every production Save/Load and credential target.
        string scope = Guid.NewGuid().ToString("N");
        Settings.DirectoryPath = Path.Combine(Environment.CurrentDirectory, "work", "test-profiles", scope);
        Credentials.Prefix = "QuietTranslate.Tests/" + scope + "/";
        if (args.Length == 2 && args[0] == "--clipboard-read-check")
        {
            App.EnablePointerInput();
            var watch = Stopwatch.StartNew(); bool matched = false; string? errorType = null;
            try { matched = Clipboard.GetText() == CopyTest.ExternalSample; } catch (Exception ex) { errorType = ex.GetType().Name; }
            File.WriteAllText(args[1], JsonSerializer.Serialize(new { matched, elapsedMs = watch.ElapsedMilliseconds, errorType }));
            return matched ? 0 : 1;
        }
        if (args.Length == 3 && args[0] == "--product-check")
        {
            var product = Assembly.LoadFile(Path.GetFullPath(args[1]));
            bool passed = product.GetType("QuietTranslate.TranslationService") != null &&
                product.GetType("QuietTranslate.SelfTest") == null && product.GetType("QuietTranslate.TestHost") == null &&
                product.GetType("QuietTranslate.App")!.GetMethod("RunDiagnostics", BindingFlags.NonPublic | BindingFlags.Static) == null;
            File.WriteAllText(args[2], JsonSerializer.Serialize(new { passed, testHarnessExcluded = passed }));
            return passed ? 0 : 1;
        }
        return App.RunDiagnostics(args);
    }
}
