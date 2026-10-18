using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace QuietTranslate;

public sealed partial class App
{
    internal Action<Exception>? TestException;
    partial void OnManualShown(ManualWindow window) => manualShownForTest?.Invoke(window);

    internal static int RunDiagnostics(string[] args)
    {
        // Windows 10+ pointer input avoids the legacy WISP pen worker which can
        // hang during dispatcher shutdown while enumerating tablet devices.
        EnablePointerInput();
        Mutex? startupInstance = null;
        string startupScope = args.Length > 1 && args[0] == "--instance-fixture" ? ".InstanceTest." + args[1] : "";
        if ((args.Length == 0 || startupScope.Length > 0) && !AcquireInstance(startupScope, out startupInstance)) return 0;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, instance = startupInstance };
        app.DispatcherUnhandledException += (_, e) => { e.Handled = true; if (app.TestException != null) app.TestException(e.Exception); else MessageBox.Show("工具遇到错误：" + e.Exception.Message, "轻译", MessageBoxButton.OK, MessageBoxImage.Warning); };
        app.Startup += async (_, _) =>
        {
            if (args.Length > 1 && args[0] == "--ocr-settings-test") { int code = await OcrSettingsTest.RunAsync(args[1]); app.Shutdown(code); return; }
            if (args.Length > 1 && args[0] == "--discord-selection-test")
            {
                Directory.CreateDirectory(args[1]);
                app.TestException = ex => { File.WriteAllText(Path.Combine(args[1], "test-error.txt"), ex.ToString()); app.Shutdown(1); };
                int code = await DiscordSelectionTest.RunAsync(args[1]); app.Shutdown(code); return;
            }
            if (args.Length > 2 && args[0] == "--discord-selection-fixture") { DiscordSelectionTest.OpenFixture(app, args[1], args[2]); return; }
            if (args.Length > 0 && args[0] == "--table-test") { int code = await TableSelectionTest.RunAsync(args[1]); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--self-test") { int code = await SelfTest.RunAsync(args.Length > 1 ? args[1] : Path.Combine(Environment.CurrentDirectory, "self-test-results.json")); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--ui-smoke") { int code = await SelfTest.UiSmokeAsync(args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--v13-test") { int code = await V13Test.RunAsync(args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--bubble-test") { int code = await BubbleTest.RunAsync(args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--growth-repro") { int code = await GrowthRepro.RunAsync(args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--upgrade-test") { int code = await UpgradeTest.RunAsync(app, args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--refinement-test") { int code = await RefinementTest.RunAsync(args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--hover-test") { int code = await HoverTest.RunAsync(app, args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--lifecycle-test") { int code = await app.RunLifecycleAsync(args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--selection-fixture") { SelfTest.OpenSelectionFixture(app); return; }
            if (args.Length > 0 && args[0] == "--scroll-selection-fixture") { InteractionTest.OpenFixture(app); return; }
            if (args.Length > 0 && args[0] == "--interaction-test") { int code = await InteractionTest.RunAsync(app, args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--motion-test") { int code = await MotionTest.RunAsync(app, args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--docs-capture") { int code = await DocsCapture.RunAsync(app, args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--polish-test") { int code = await PolishTest.RunAsync(app, args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 0 && args[0] == "--copy-test") { int code = await CopyTest.RunAsync(app, args.Length > 1 ? args[1] : Environment.CurrentDirectory); app.Shutdown(code); return; }
            if (args.Length > 2 && args[0] == "--instance-fixture") { app.OpenInstanceFixture(args[1], args[2]); return; }
            app.Shutdown(2); // Unknown test mode never loads the user profile.
        };
        app.Exit += (_, _) => app.Cleanup(); return app.Run();
    }
}
