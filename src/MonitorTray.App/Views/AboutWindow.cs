using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MonitorTray.App.Controls;
using MonitorTray.App.Platform;
using MonitorTray.App.Theming;
using MonitorTray.Gpu;

namespace MonitorTray.App.Views;

/// <summary>About: identity, version/build, detected system, credits and licenses. Copy info for support.</summary>
public sealed class AboutWindow : Window
{
    private const double M = 6;

    public AboutWindow(AppState app, GpuFanService? fans, int devices, int entries)
    {
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = true; Title = "About Marz Monitor"; UseLayoutRounding = true;
        SizeToContent = SizeToContent.WidthAndHeight; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/MonitorTray;component/Assets/app.ico")); } catch (Exception) { }

        var root = new StackPanel { Width = 420 };
        // Header with icon, name, version
        var close = FlatButton.Icon(Icons.X, 24, 12, "Close");
        close.Click += Close;
        close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top;
        var logo = new Image { Width = 64, Height = 64, Source = new BitmapImage(new Uri("pack://application:,,,/MonitorTray;component/Assets/app-256.png")), Margin = new Thickness(0, 0, 16, 0) };
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children =
        {
            Ui.T("MARZ MONITOR", 24, weight: 600),
            Ui.T("by " + AppInfo.Publisher, 12, Theme.Muted),
            Ui.T($"Version {AppInfo.Version}  ·  build {AppInfo.Commit}  ·  {AppInfo.BuildDate} UTC", 11, Theme.Muted, tabular: true).With(t => t.Margin = new Thickness(0, 6, 0, 0)),
        } };
        var head = new Grid { Margin = new Thickness(20, 18, 8, 16) };
        head.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { logo, titles } });
        head.Children.Add(close);
        root.Children.Add(Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(0), head).With(b => b.Background = Brushes.Transparent));

        root.Children.Add(Para("Hardware monitor in the Windows tray: CPU, GPU, memory, storage and network at a glance, every sensor in Details and safe control of the NVIDIA GPU fans."));

        // System
        var st = fans?.State;
        var gpu = st?.Reading?.Identity.Name ?? "—";
        var driver = st?.Reading?.Telemetry.Driver ?? "—";
        var control = st == null ? "—" : st.CanControl ? "this app controls the fans" : (st.ReadOnlyReason ?? "read-only");
        root.Children.Add(Section("SYSTEM", [
            ("GPU", gpu), ("Driver NVIDIA", driver), ("Fans", control),
            ("Sensors", $"{devices} devices · {entries} entries"),
            ("Privilege", Autostart.IsAdmin() ? "administrator (all sensors)" : "user (some sensors hidden)"),
            ("Runtime", AppInfo.Runtime), ("Windows", AppInfo.Os),
        ]));
        root.Children.Add(Section("GOOD TO KNOW", [
            ("•", "Quitting from the tray returns the fans to automatic before closing."),
            ("•", "Killing the app (Task Manager, power loss) does not guarantee that hand-back."),
            ("•", "Values are as reported by the sensors; the app does not judge hardware \"health\"."),
        ], bullets: true));
        root.Children.Add(Section("CREDITS", [
            ("Sensors", "LibreHardwareMonitorLib (MPL-2.0)"),
            ("Tray", "H.NotifyIcon (MIT)"),
            ("Fonts", "Barlow / Barlow Condensed (SIL OFL 1.1)"),
            ("Icons", "Lucide (ISC)"),
            ("Fans", "safety logic inherited from GPU by MarzOps"),
        ]));

        // Footer with actions
        var copy = Link("Copy info");
        copy.Click += () =>
        {
            var text = $"{AppInfo.Name} {AppInfo.Version} ({AppInfo.Commit}, {AppInfo.BuildDate} UTC)\nGPU: {gpu} · driver {driver}\nFans: {control}\nSensors: {devices}/{entries}\nAdmin: {Autostart.IsAdmin()}\n{AppInfo.Runtime} · {AppInfo.Os}";
            try { Clipboard.SetText(text); ((TextBlock)copy.Child).Text = "Copied ✓"; } catch (Exception) { }
        };
        var logs = Link("Open data folder");
        logs.Click += () => { try { Process.Start(new ProcessStartInfo("explorer.exe", AppState.DataDir) { UseShellExecute = true }); } catch (Exception) { } };
        var repo = Link("Repository");
        repo.Click += () => { try { Process.Start(new ProcessStartInfo(AppInfo.Repository) { UseShellExecute = true }); } catch (Exception) { } };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Children = { copy, logs, repo } };
        var foot = new DockPanel { Margin = new Thickness(20, 10, 20, 14) };
        var copyright = Ui.T(AppInfo.Copyright, 10, Theme.Muted).With(t => t.VerticalAlignment = VerticalAlignment.Center);
        DockPanel.SetDock(actions, Dock.Left); foot.Children.Add(actions); foot.Children.Add(copyright);
        copyright.HorizontalAlignment = HorizontalAlignment.Right;
        root.Children.Add(Ui.Box(new Thickness(0, 1, 0, 0), new Thickness(0), foot));

        Content = new BlueprintFrame(root, shadow: false) { Margin = new Thickness(M) };
        head.MouseLeftButtonDown += (_, e) => { if (e.OriginalSource is not FlatButton) try { DragMove(); } catch (InvalidOperationException) { } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        SourceInitialized += (_, _) => { Native.SquareCorners(this); Native.Blur(this, app.Settings.Opacity < 100 && Native.TransparencyEffectsEnabled()); };
        SizeChanged += (_, _) => FrameRegion.Apply(this, M, true);
    }

    private static TextBlock Para(string text) => Ui.T(text, 12, head: false).With(t =>
    { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; t.Margin = new Thickness(20, 14, 20, 4); });

    private static StackPanel Section(string title, (string K, string V)[] rows, bool bullets = false)
    {
        var p = new StackPanel { Margin = new Thickness(20, 12, 20, 0) };
        p.Children.Add(Ui.T(title, 11, Theme.Muted, weight: 600).With(t => t.Margin = new Thickness(0, 0, 0, 4)));
        foreach (var (k, v) in rows)
        {
            var g = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(bullets ? 14 : 96) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            var key = Ui.T(k, 12, Theme.Muted, head: false);
            var val = Ui.T(v, 12, head: false).With(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; });
            Grid.SetColumn(val, 1);
            g.Children.Add(key); g.Children.Add(val);
            p.Children.Add(g);
        }
        return p;
    }

    private static FlatButton Link(string text)
    {
        var t = Ui.T(text, 11, Theme.Muted);
        return new FlatButton { Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(-6, 0, 8, 0) }.Content(t);
    }
}
