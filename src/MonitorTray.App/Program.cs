using System.Windows;

namespace MonitorTray.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var opt = Options.Parse(args);
        if (opt.Preview != null) return Preview.Run(opt);
        var at = Array.IndexOf(args, "--ac-test");
        if (at >= 0 && at + 2 < args.Length) return AcTest.Run(args[at + 1], args[at + 2]);
        var rt = Array.IndexOf(args, "--read-test");
        if (rt >= 0 && rt + 1 < args.Length) return ReadTest.Run(args[rt + 1]);
        using var instance = new Platform.SingleInstance();
        if (!instance.IsFirst)
        {
            // Instância elevada já aberta: o pipe dela pode recusar um cliente não elevado (dono = Administradores).
            // Nesse caso pedimos via tarefa elevada, que vira um cliente elevado e entrega o "show".
            if (!instance.Send(args.Contains("--game") ? "game" : opt.Details ? "details" : "show") && !Platform.Autostart.IsAdmin())
                Platform.Autostart.TryRelaunchElevated();
            return 0;
        }
        // Atalho/duplo clique na cópia instalada sem elevação: abre pela tarefa elevada (sem UAC) e sai.
        if (opt.Simulate == null && Platform.Autostart.TryRelaunchElevated()) return 0;
        var app = new App(opt, instance);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Crash(e.ExceptionObject as Exception);
        app.DispatcherUnhandledException += (_, e) => { Crash(e.Exception); e.Handled = true; };
        return app.Run();
    }

    private static void Crash(Exception? ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(AppState.DataDir, "logs");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "errors.log"), $"{DateTimeOffset.Now:O} {ex}\r\n");
        }
        catch (Exception) { }
    }
}
