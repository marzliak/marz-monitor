using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MonitorTray.App.Controls;
using MonitorTray.App.Theming;
using MonitorTray.Gpu;

namespace MonitorTray.App.Views;

internal static class FanText
{
    public static string Mode(FanState s) => s.Mode switch
    {
        FanMode.Automatic => "AUTO", FanMode.Manual => s.RequestedPercent is uint p ? $"MANUAL {p}%" : "MANUAL", FanMode.Mixed => "MISTO", _ => "—",
    };
    public static string Speeds(FanState s) => s.Reading == null ? "—"
        : string.Join("  ", s.Reading.Fans.Select(f => $"{f.Index + 1}: {(f.SpeedPercent is uint v ? v.ToString(CultureInfo.InvariantCulture) + "%" : "—")}"));
}

/// <summary>Seção "Controle das fans" no painel da GPU (Detalhes). Clique aplica, sem diálogo; presets inseguros ficam desabilitados.</summary>
public sealed class FanPanel : StackPanel
{
    public static readonly uint[] Presets = [0, 10, 15, 25, 30, 50, 75, 100];
    private readonly GpuFanService service;
    private readonly TextBlock meta, notice;
    private readonly StackPanel fanRows;
    private readonly List<(uint? Value, FlatButton Button, TextBlock Text)> buttons = [];
    private readonly List<FanRowView> rows = [];

    public FanPanel(GpuFanService service)
    {
        this.service = service;
        var head = new DockPanel();
        var title = Ui.T("CONTROLE DAS FANS", 13, weight: 600);
        meta = Ui.T("", 11, Theme.Muted, tabular: true).With(t => t.VerticalAlignment = VerticalAlignment.Bottom);
        DockPanel.SetDock(meta, Dock.Right);
        head.Children.Add(meta); head.Children.Add(title);
        Children.Add(Ui.Box(new Thickness(0, 0, 0, 1), new Thickness(0, 0, 0, 4), head).With(b => b.Margin = new Thickness(0, 14, 0, 0)));

        fanRows = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        Children.Add(fanRows);

        var bar = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        AddButton(bar, null, "AUTO");
        foreach (var p in Presets) AddButton(bar, p, p.ToString(CultureInfo.InvariantCulture) + "%");
        Children.Add(bar);
        notice = Ui.T("", 11, Theme.Muted, head: false).With(t => { t.TextWrapping = TextWrapping.Wrap; t.TextTrimming = TextTrimming.None; t.Margin = new Thickness(0, 6, 0, 0); });
        Children.Add(notice);
        Update(service.State);
    }

    private void AddButton(Panel bar, uint? value, string label)
    {
        var t = Ui.T(label, 12, Theme.Text).With(x => x.VerticalAlignment = VerticalAlignment.Center);
        var b = new FlatButton(Theme.Text) { Height = 24, MinWidth = value == null ? 52 : 44, Padding = new Thickness(8, 0, 8, 0), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 6, 6), ActiveBg = Theme.Fill }.Content(t);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        b.SetResourceReference(Border.BorderBrushProperty, Theme.Line);
        b.Click += async () =>
        {
            if (!b.IsEnabled) return;
            try { await service.CommandAsync(value); }
            catch (Exception) { /* o erro já está em State.Error e aparece na linha de aviso */ }
        };
        buttons.Add((value, b, t));
        bar.Children.Add(b);
    }

    public void Update(FanState s)
    {
        var now = DateTimeOffset.UtcNow;
        meta.Text = FanText.Mode(s) + (s.Reading?.TemperatureC is uint c ? $" · core {c} °C" : "");
        var fans = s.Reading?.Fans ?? [];
        while (rows.Count < fans.Count) { var r = new FanRowView(); rows.Add(r); fanRows.Children.Add(r); }
        while (rows.Count > fans.Count) { fanRows.Children.Remove(rows[^1]); rows.RemoveAt(rows.Count - 1); }
        for (int i = 0; i < fans.Count; i++) rows[i].Set(fans[i]);

        bool manual = s.Mode == FanMode.Manual;
        foreach (var (v, b, t) in buttons)
        {
            string? block = v is uint p ? s.BlockFor(p, now) : (!s.CanControl ? s.ReadOnlyReason ?? "Controle indisponível." : s.Busy ? "Comando em andamento." : s.Reading == null ? "Sem leitura." : null);
            bool enabled = block == null;
            b.IsEnabled = enabled; b.Opacity = enabled ? 1 : 0.45; b.ToolTip = block ?? (v is null ? "Devolver ao automático do driver" : $"Fixar todas as fans em {v}%");
            bool active = v is null ? s.Mode == FanMode.Automatic : manual && s.RequestedPercent == v;
            b.Active = active;
            t.SetResourceReference(TextBlock.ForegroundProperty, active ? Theme.Bg : Theme.Text);
        }
        var msg = s.Error ?? s.SafetyNotice ?? (!s.CanControl ? s.ReadOnlyReason : null);
        if (msg == null && s.Reading?.Telemetry is { HasFanRange: true } tel) msg = $"Faixa do driver: {tel.MinFanPercent}–{tel.MaxFanPercent}%. Ao sair, as fans voltam ao automático.";
        notice.Text = msg ?? "";
        notice.SetResourceReference(TextBlock.ForegroundProperty, s.Error != null || s.SafetyNotice != null ? Theme.UStroke : Theme.Muted);
    }

    private sealed class FanRowView : DockPanel
    {
        private readonly TextBlock name, value, rpm, target; private readonly Bar bar;
        public FanRowView()
        {
            Margin = new Thickness(0, 0, 0, 4);
            name = Ui.T("", 12, head: false).With(t => { t.Width = 56; t.VerticalAlignment = VerticalAlignment.Center; });
            value = Ui.T("", 17, weight: 600, tabular: true).With(t => { t.Width = 50; t.TextTrimming = TextTrimming.None; t.VerticalAlignment = VerticalAlignment.Center; });
            rpm = Ui.T("", 11, Theme.Muted, tabular: true).With(t => { t.Width = 70; t.VerticalAlignment = VerticalAlignment.Center; });
            target = Ui.T("", 11, Theme.Muted, tabular: true).With(t => { t.Width = 90; t.TextAlignment = TextAlignment.Right; t.VerticalAlignment = VerticalAlignment.Center; });
            bar = new Bar { Height = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
            foreach (var e in new FrameworkElement[] { name, value, rpm }) { SetDock(e, Dock.Left); Children.Add(e); }
            SetDock(target, Dock.Right); Children.Add(target);
            Children.Add(bar);
        }
        public void Set(FanReading f)
        {
            name.Text = $"Fan {f.Index + 1}";
            value.Text = f.SpeedPercent is uint s ? s + "%" : "—";
            rpm.Text = f.SpeedRpm is uint r ? r + " RPM" : "— RPM";
            target.Text = f.Policy switch { 0 => "automático", 1 => $"alvo {f.TargetPercent?.ToString(CultureInfo.InvariantCulture) ?? "—"}%", _ => "política —" };
            bar.SetFraction((f.SpeedPercent ?? 0) / 100.0, (f.SpeedPercent ?? 0) >= 85);
        }
    }
}
