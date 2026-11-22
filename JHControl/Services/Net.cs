using System.Net;
using System.Net.Security;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JHControl.Services;

/// <summary>Utilidades de red compartidas.</summary>
public static class Net
{
    // Acepta los certificados propios de las TVs (Samsung :8002, LG :3001).
    public static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(3),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
        }
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<(int Code, string Body)> GetAsync(string url, int timeoutMs, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeoutMs);
        try
        {
            using var r = await Http.GetAsync(url, cts.Token);
            var body = await r.Content.ReadAsStringAsync(cts.Token);
            return ((int)r.StatusCode, body);
        }
        catch { return (0, null); }
    }

    public static async Task<int> PostAsync(string url, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            using var r = await Http.PostAsync(url, new StringContent(""), cts.Token);
            return (int)r.StatusCode;
        }
        catch { return 0; }
    }

    public static ClientWebSocket NewWebSocket()
    {
        var ws = new ClientWebSocket();
        ws.Options.RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true;
        ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        return ws;
    }

    /// <summary>Lee un mensaje de texto completo. Devuelve null si se cerró.</summary>
    public static async Task<string> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (true)
        {
            var r = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public static async Task SendTextAsync(WebSocket ws, SemaphoreSlim gate, string text)
    {
        await gate.WaitAsync();
        try
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        finally { gate.Release(); }
    }

    /// <summary>Lee el contenido de una etiqueta XML simple.</summary>
    public static string Tag(string xml, string tag)
    {
        if (string.IsNullOrEmpty(xml)) return null;
        var m = Regex.Match(xml, $"<{tag}>([^<]*)</{tag}>", RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        var v = WebUtility.HtmlDecode(m.Groups[1].Value).Trim();
        return v.Length > 0 ? v : null;
    }

    public static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
}
