using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace MonitorTray.Oled;

public interface IOledTransport
{
    Task BindAsync(int icon, CancellationToken token);
    Task SendAsync(string[] lines, CancellationToken token);
    Task ReleaseAsync(CancellationToken token);
}

/// <summary>
/// Cliente GameSense local (SteelSeries GG). Lê o endereço do coreProps.json a cada chamada (a porta muda quando o GG reinicia),
/// aceita só localhost, registra o jogo, liga o evento de tela do OLED 128x40 e envia as 2 linhas. Heartbeat a cada 10 s.
/// </summary>
public sealed class GameSenseClient : IOledTransport
{
    public const string DeviceType = "screened-128x40";
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(3) };
    private readonly string game, display;
    private DateTime lastSend = DateTime.MinValue;
    private const string Event = "OLED_ACTIVE";

    public GameSenseClient(string game = "MARZ_MONITOR", string display = "Marz Monitor") { this.game = game; this.display = display; }

    public static string? CorePropsPath()
    {
        var pd = Environment.GetEnvironmentVariable("PROGRAMDATA") ?? @"C:\ProgramData";
        return new[] { Path.Combine(pd, "SteelSeries", "GG", "coreProps.json"), Path.Combine(pd, "SteelSeries", "SteelSeries Engine 3", "coreProps.json") }.FirstOrDefault(File.Exists);
    }

    public static string Address()
    {
        var p = CorePropsPath() ?? throw new FileNotFoundException("SteelSeries GG não encontrado (coreProps.json ausente).");
        using var doc = JsonDocument.Parse(File.ReadAllText(p));
        var a = doc.RootElement.GetProperty("address").GetString() ?? throw new InvalidDataException("coreProps.json sem endereço.");
        return ValidateLocal(a);
    }

    public static string ValidateLocal(string address)
    {
        if (!address.StartsWith("http://") && !address.StartsWith("https://")) address = "http://" + address;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri)) throw new InvalidDataException("Endereço do GG inválido.");
        if ((uri.Scheme != "http" && uri.Scheme != "https") || !new[] { "localhost", "127.0.0.1", "::1" }.Contains(uri.Host.Trim('[', ']'), StringComparer.OrdinalIgnoreCase)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath != "/")
            throw new InvalidOperationException("GameSense aceita somente o endereço local do GG.");
        return address.TrimEnd('/');
    }

    private static async Task Post(string path, object body, CancellationToken token)
    {
        var url = Address() + path;
        using var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var r = await Http.PostAsync(url, content, token).ConfigureAwait(false);
        if (!r.IsSuccessStatusCode) throw new HttpRequestException($"{path}: {(int)r.StatusCode} {await r.Content.ReadAsStringAsync(token).ConfigureAwait(false)}");
    }

    public async Task BindAsync(int icon, CancellationToken token)
    {
        await Post("/game_metadata", new Dictionary<string, object> { ["game"] = game, ["game_display_name"] = display, ["developer"] = "MarzOps" }, token);
        var handler = new Dictionary<string, object>
        {
            ["device-type"] = DeviceType, ["zone"] = "one", ["mode"] = "screen",
            ["datas"] = new[] { new Dictionary<string, object>
            {
                ["lines"] = new[] { new Dictionary<string, object> { ["has-text"] = true, ["context-frame-key"] = "line1" }, new Dictionary<string, object> { ["has-text"] = true, ["context-frame-key"] = "line2" } },
                ["icon-id"] = icon,
            } },
        };
        await Post("/bind_game_event", new Dictionary<string, object> { ["game"] = game, ["event"] = Event, ["icon_id"] = icon, ["value_optional"] = true, ["handlers"] = new[] { handler } }, token);
    }

    public async Task SendAsync(string[] lines, CancellationToken token)
    {
        if ((DateTime.UtcNow - lastSend).TotalSeconds >= 10) await Post("/game_heartbeat", new Dictionary<string, object> { ["game"] = game }, token);
        var frame = new Dictionary<string, object> { ["line1"] = lines.ElementAtOrDefault(0) ?? " ", ["line2"] = lines.ElementAtOrDefault(1) ?? " " };
        await Post("/game_event", new Dictionary<string, object> { ["game"] = game, ["event"] = Event, ["data"] = new Dictionary<string, object> { ["frame"] = frame, ["value"] = 0 } }, token);
        lastSend = DateTime.UtcNow;
    }

    public async Task ReleaseAsync(CancellationToken token)
    {
        await Post("/stop_game", new Dictionary<string, object> { ["game"] = game }, token);
        lastSend = DateTime.MinValue;
    }
}

public sealed record OledDelivery(bool Enabled, string Status, string? Error, DateTimeOffset? SentAt, string[] Lines);

/// <summary>Único escritor do OLED (bind/send/release serializados). Falha marca liberação pendente antes de religar.</summary>
public sealed class OledOutput
{
    private readonly IOledTransport transport;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool bound, releasePending, enabled = true;
    private int? boundIcon;
    private OledDelivery delivery = new(true, "Aguardando GG", null, null, []);
    public OledOutput(IOledTransport transport) { this.transport = transport; }
    public OledDelivery Snapshot => Volatile.Read(ref delivery);

    public async Task<bool> PresentAsync(string[] lines, int icon, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (releasePending) await Release(token);
            if (!enabled) return false;
            if (!bound || boundIcon != icon)
            {
                releasePending = true;
                await transport.BindAsync(icon, token);
                bound = true; boundIcon = icon; releasePending = false;
            }
            var frame = new[] { lines.ElementAtOrDefault(0) ?? " ", lines.ElementAtOrDefault(1) ?? " " };
            await transport.SendAsync(frame, token);
            Volatile.Write(ref delivery, new(true, "Enviado ao GG", null, DateTimeOffset.UtcNow, frame));
            return true;
        }
        catch (Exception ex)
        {
            releasePending = true;
            Volatile.Write(ref delivery, new(enabled, "Falha no envio", ex is OperationCanceledException ? "GG não respondeu no prazo" : ex.Message, null, []));
            return false;
        }
        finally { gate.Release(); }
    }

    public async Task SetEnabledAsync(bool on, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            enabled = on;
            if (!on && (bound || releasePending)) await Release(token);
            Volatile.Write(ref delivery, new(on, on ? "Aguardando envio" : "Desligado (o GG volta à tela dele)", null, null, []));
        }
        catch (Exception ex) { Volatile.Write(ref delivery, new(on, "Falha ao liberar", ex.Message, null, [])); }
        finally { gate.Release(); }
    }

    private async Task Release(CancellationToken token)
    {
        releasePending = true; bound = false; boundIcon = null;
        await transport.ReleaseAsync(token);
        releasePending = false;
    }
}
