using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using JHControl.Models;

namespace JHControl.Services;

public enum StatusKind { None, Wait, Ok, Error }
enum ConnResult { Ok, Fail, Denied }

public interface ITvDriver : IDisposable
{
    bool IsConnected { get; }
    /// <summary>true si la última falla fue porque la TV no respondió (para buscarla de nuevo).</summary>
    bool Unreachable { get; }
    /// <summary>La app pide al usuario el código que muestra la TV (Android TV).</summary>
    Func<Task<string>> RequestCode { get; set; }
    event Action<StatusKind, string> StatusChanged;
    event Action<string> Notice;
    Task<bool> ConnectAsync();
    Task SendAsync(string action);
    Task SendTextAsync(string text);
}

public static class DriverFactory
{
    public static ITvDriver Create(TvDevice tv) => tv.Brand switch
    {
        "roku" => new RokuDriver(tv),
        "samsung" => new SamsungDriver(tv),
        "lg" => new LgDriver(tv),
        "androidtv" => new AndroidTvDriver(tv),
        "ir" => new IrDriver(tv),
        _ => null
    };
}

public abstract class TvDriverBase : ITvDriver
{
    protected readonly TvDevice Tv;
    protected bool Disposed;
    Task<bool> _connecting;

    protected TvDriverBase(TvDevice tv) { Tv = tv; }

    public bool IsConnected { get; protected set; }
    public bool Unreachable { get; protected set; }
    public Func<Task<string>> RequestCode { get; set; }
    public event Action<StatusKind, string> StatusChanged;
    public event Action<string> Notice;

    protected void Status(StatusKind k, string text) { if (!Disposed) StatusChanged?.Invoke(k, text); }
    protected void Say(string text) { if (!Disposed) Notice?.Invoke(text); }
    protected void Unsupported() => Say("Esta TV no tiene ese botón por WiFi");

    public async Task<bool> ConnectAsync()
    {
        if (IsConnected) return true;
        if (_connecting != null) return await _connecting;
        Unreachable = false;
        _connecting = DoConnectAsync();
        try { return await _connecting; }
        finally { _connecting = null; }
    }

    protected abstract Task<bool> DoConnectAsync();
    public abstract Task SendAsync(string action);
    public abstract Task SendTextAsync(string text);
    public virtual void Dispose() { Disposed = true; IsConnected = false; }

    protected static string Digit(string action) =>
        action.Length == 2 && action[0] == 'n' && char.IsDigit(action[1]) ? action[1].ToString() : null;
}

// ════════════════════════════ ROKU (ECP HTTP :8060) ════════════════════════════
public sealed class RokuDriver : TvDriverBase
{
    static readonly Dictionary<string, string> Keys = new()
    {
        ["power"] = "Power", ["mute"] = "VolumeMute", ["home"] = "Home", ["back"] = "Back",
        ["menu"] = "Info", ["info"] = "Info", ["exit"] = "Home",
        ["up"] = "Up", ["down"] = "Down", ["left"] = "Left", ["right"] = "Right", ["ok"] = "Select",
        ["volup"] = "VolumeUp", ["voldown"] = "VolumeDown", ["chup"] = "ChannelUp", ["chdown"] = "ChannelDown",
        ["rew"] = "Rev", ["play"] = "Play", ["ff"] = "Fwd",
        ["search"] = "Search", ["backspace"] = "Backspace", ["replay"] = "InstantReplay"
    };
    static readonly Dictionary<string, string> Apps = new() { ["netflix"] = "12", ["youtube"] = "837", ["prime"] = "13" };
    static readonly string[] Inputs = { "InputHDMI1", "InputHDMI2", "InputHDMI3", "InputAV1", "InputTuner" };
    int _input = -1;

    public RokuDriver(TvDevice tv) : base(tv) { }
    string Base => $"http://{Tv.Ip}:8060";

    protected override async Task<bool> DoConnectAsync()
    {
        Status(StatusKind.Wait, "Conectando…");
        var (code, x) = await Net.GetAsync(Base + "/query/device-info", 4000);
        if (code == 200 && x != null && x.Contains("<device-info"))
        {
            IsConnected = true;
            Status(StatusKind.Ok, "Conectado");
            return true;
        }
        IsConnected = false;
        Unreachable = code == 0;
        Status(StatusKind.Error, code == 0 ? "La TV no responde" : "Activa «Control por aplicaciones móviles» en el Roku");
        return false;
    }

    async Task Post(string path)
    {
        var code = await Net.PostAsync(Base + path, 3000);
        if (code >= 200 && code < 300)
        {
            if (!IsConnected) { IsConnected = true; Status(StatusKind.Ok, "Conectado"); }
        }
        else if (code == 403) Status(StatusKind.Error, "El Roku bloqueó el control: revisa «Control por aplicaciones móviles»");
        else { IsConnected = false; Status(StatusKind.Error, "La TV no responde"); }
    }

    public override async Task SendAsync(string a)
    {
        var d = Digit(a);
        if (d != null) { await Post("/keypress/Lit_" + d); return; }
        if (a == "input")
        {
            _input = (_input + 1) % Inputs.Length;
            Say(Inputs[_input].Replace("Input", "Entrada: "));
            await Post("/keypress/" + Inputs[_input]);
            return;
        }
        if (Apps.TryGetValue(a, out var app)) { await Post("/launch/" + app); return; }
        if (Keys.TryGetValue(a, out var key)) { await Post("/keypress/" + key); return; }
        Unsupported();
    }

    public override async Task SendTextAsync(string text)
    {
        foreach (var ch in text)
        {
            await Post("/keypress/Lit_" + Uri.EscapeDataString(ch.ToString()));
            await Task.Delay(40);
        }
    }
}

// ════════════════════════════ SAMSUNG (WebSocket, Tizen 2016+) ════════════════════════════
public sealed class SamsungDriver : TvDriverBase
{
    static readonly Dictionary<string, string> Keys = new()
    {
        ["power"] = "KEY_POWER", ["input"] = "KEY_SOURCE", ["mute"] = "KEY_MUTE", ["home"] = "KEY_HOME",
        ["back"] = "KEY_RETURN", ["menu"] = "KEY_MENU", ["info"] = "KEY_INFO", ["exit"] = "KEY_EXIT",
        ["up"] = "KEY_UP", ["down"] = "KEY_DOWN", ["left"] = "KEY_LEFT", ["right"] = "KEY_RIGHT", ["ok"] = "KEY_ENTER",
        ["volup"] = "KEY_VOLUP", ["voldown"] = "KEY_VOLDOWN", ["chup"] = "KEY_CHUP", ["chdown"] = "KEY_CHDOWN",
        ["rew"] = "KEY_REWIND", ["ff"] = "KEY_FF"
    };
    static readonly Dictionary<string, string> Apps = new()
    {
        ["netflix"] = "11101200001", ["youtube"] = "111299001912", ["prime"] = "3201512006785"
    };

    ClientWebSocket _ws;
    CancellationTokenSource _cts;
    readonly SemaphoreSlim _gate = new(1, 1);
    bool _playing;

    public SamsungDriver(TvDevice tv) : base(tv) { }

    protected override async Task<bool> DoConnectAsync()
    {
        var name = Convert.ToBase64String(Encoding.UTF8.GetBytes("JH Control"));
        var urls = new List<string>
        {
            $"wss://{Tv.Ip}:8002/api/v2/channels/samsung.remote.control?name={name}" +
                (string.IsNullOrEmpty(Tv.Token) ? "" : $"&token={Tv.Token}"),
            $"ws://{Tv.Ip}:8001/api/v2/channels/samsung.remote.control?name={name}"
        };
        foreach (var url in urls)
        {
            Status(StatusKind.Wait, "Conectando… si la TV pregunta, elige «Permitir»");
            var r = await TryUrlAsync(url);
            if (r == ConnResult.Ok) { IsConnected = true; Status(StatusKind.Ok, "Conectado"); return true; }
            if (r == ConnResult.Denied) { Status(StatusKind.Error, "La TV rechazó la conexión. Permite «JH Control» en la TV."); return false; }
        }
        Unreachable = true;
        Status(StatusKind.Error, "No se pudo conectar con la TV");
        return false;
    }

    async Task<ConnResult> TryUrlAsync(string url)
    {
        var ws = Net.NewWebSocket();
        try
        {
            using var c = new CancellationTokenSource(6000);
            await ws.ConnectAsync(new Uri(url), c.Token);
        }
        catch { ws.Dispose(); return ConnResult.Fail; }

        // Espera a que el usuario acepte en la TV (hasta 35 s)
        using var wait = new CancellationTokenSource(35000);
        try
        {
            while (true)
            {
                var msg = await Net.ReceiveTextAsync(ws, wait.Token);
                if (msg == null) break;
                string ev = null; string token = null;
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    ev = Net.Str(doc.RootElement, "event");
                    if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                        data.TryGetProperty("token", out var tk))
                        token = tk.ValueKind == JsonValueKind.String ? tk.GetString() : tk.ToString();
                }
                catch { continue; }

                if (ev == "ms.channel.connect")
                {
                    if (!string.IsNullOrEmpty(token)) { Tv.Token = token; TvStore.Save(); }
                    _ws = ws;
                    _cts = new CancellationTokenSource();
                    _ = ReadLoopAsync(ws, _cts.Token);
                    return ConnResult.Ok;
                }
                if (ev == "ms.channel.unauthorized" || ev == "ms.channel.timeOut")
                {
                    ws.Dispose();
                    return ConnResult.Denied;
                }
            }
        }
        catch { }
        ws.Dispose();
        return ConnResult.Fail;
    }

    async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
                if (await Net.ReceiveTextAsync(ws, ct) == null) break;
        }
        catch { }
        if (_ws == ws && !Disposed)
        {
            IsConnected = false;
            Status(StatusKind.Error, "Desconectado — toca un botón para reconectar");
        }
    }

    async Task SendJson(JsonObject o)
    {
        if (!IsConnected && !await ConnectAsync()) return;
        try { await Net.SendTextAsync(_ws, _gate, o.ToJsonString()); }
        catch { IsConnected = false; Status(StatusKind.Error, "Se perdió la conexión — toca de nuevo"); }
    }

    Task SendKey(string key) => SendJson(new JsonObject
    {
        ["method"] = "ms.remote.control",
        ["params"] = new JsonObject
        {
            ["Cmd"] = "Click", ["DataOfCmd"] = key, ["Option"] = "false", ["TypeOfRemote"] = "SendRemoteKey"
        }
    });

    public override async Task SendAsync(string a)
    {
        var d = Digit(a);
        if (d != null) { await SendKey("KEY_" + d); return; }
        if (a == "play") { _playing = !_playing; await SendKey(_playing ? "KEY_PLAY" : "KEY_PAUSE"); return; }
        if (Apps.TryGetValue(a, out var app))
        {
            await SendJson(new JsonObject
            {
                ["method"] = "ms.channel.emit",
                ["params"] = new JsonObject
                {
                    ["event"] = "ed.apps.launch", ["to"] = "host",
                    ["data"] = new JsonObject { ["appId"] = app, ["action_type"] = "DEEP_LINK" }
                }
            });
            return;
        }
        if (Keys.TryGetValue(a, out var key)) { await SendKey(key); return; }
        Unsupported();
    }

    public override Task SendTextAsync(string text) => SendJson(new JsonObject
    {
        ["method"] = "ms.remote.control",
        ["params"] = new JsonObject
        {
            ["Cmd"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
            ["DataOfCmd"] = "base64",
            ["TypeOfRemote"] = "SendInputString"
        }
    });

    public override void Dispose()
    {
        base.Dispose();
        try { _cts?.Cancel(); } catch { }
        var w = _ws; _ws = null;
        try { w?.Dispose(); } catch { }
    }
}

// ════════════════════════════ LG (webOS SSAP) ════════════════════════════
public sealed class LgDriver : TvDriverBase
{
    static readonly Dictionary<string, string> Buttons = new()
    {
        ["home"] = "HOME", ["back"] = "BACK", ["menu"] = "MENU", ["info"] = "INFO", ["exit"] = "EXIT",
        ["up"] = "UP", ["down"] = "DOWN", ["left"] = "LEFT", ["right"] = "RIGHT", ["ok"] = "ENTER"
    };
    static readonly Dictionary<string, string> Uris = new()
    {
        ["power"] = "ssap://system/turnOff",
        ["volup"] = "ssap://audio/volumeUp", ["voldown"] = "ssap://audio/volumeDown",
        ["chup"] = "ssap://tv/channelUp", ["chdown"] = "ssap://tv/channelDown",
        ["rew"] = "ssap://media.controls/rewind", ["ff"] = "ssap://media.controls/fastForward"
    };
    static readonly Dictionary<string, string> Apps = new()
    {
        ["netflix"] = "netflix", ["youtube"] = "youtube.leanback.v4", ["prime"] = "amazon",
        ["input"] = "com.webos.app.inputpicker"
    };
    static readonly string[] Permissions =
    {
        "LAUNCH", "LAUNCH_WEBAPP", "APP_TO_APP", "CONTROL_AUDIO", "CONTROL_DISPLAY", "CONTROL_INPUT_JOYSTICK",
        "CONTROL_INPUT_MEDIA_PLAYBACK", "CONTROL_INPUT_TV", "CONTROL_POWER", "CONTROL_INPUT_TEXT",
        "CONTROL_MOUSE_AND_KEYBOARD", "READ_INSTALLED_APPS", "READ_CURRENT_CHANNEL", "READ_INPUT_DEVICE_LIST",
        "READ_TV_CHANNEL_LIST", "READ_RUNNING_APPS", "READ_POWER_STATE", "WRITE_NOTIFICATION_TOAST", "CONTROL_TV_SCREEN"
    };

    ClientWebSocket _ws, _pws;
    CancellationTokenSource _cts;
    readonly SemaphoreSlim _gate = new(1, 1), _pgate = new(1, 1);
    readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    int _n;
    bool _secure, _muted, _playing = true;

    public LgDriver(TvDevice tv) : base(tv) { }

    protected override async Task<bool> DoConnectAsync()
    {
        foreach (var url in new[] { $"wss://{Tv.Ip}:3001/", $"ws://{Tv.Ip}:3000/" })
        {
            Status(StatusKind.Wait, "Conectando… acepta el aviso en la TV");
            var r = await TryUrlAsync(url);
            if (r == ConnResult.Ok)
            {
                IsConnected = true;
                Status(StatusKind.Ok, "Conectado");
                _ = OpenPointerAsync();
                return true;
            }
            if (r == ConnResult.Denied) { Status(StatusKind.Error, "La TV rechazó la conexión"); return false; }
        }
        Unreachable = true;
        Status(StatusKind.Error, "No se pudo conectar con la TV");
        return false;
    }

    static JsonObject Manifest() => new()
    {
        ["manifestVersion"] = 1,
        ["appVersion"] = "1.1",
        ["permissions"] = new JsonArray(Permissions.Select(p => (JsonNode)p).ToArray())
    };

    async Task<ConnResult> TryUrlAsync(string url)
    {
        var ws = Net.NewWebSocket();
        try
        {
            using var c = new CancellationTokenSource(6000);
            await ws.ConnectAsync(new Uri(url), c.Token);
        }
        catch { ws.Dispose(); return ConnResult.Fail; }

        var payload = new JsonObject { ["forcePairing"] = false, ["pairingType"] = "PROMPT", ["manifest"] = Manifest() };
        if (!string.IsNullOrEmpty(Tv.Token)) payload["client-key"] = Tv.Token;
        var reg = new JsonObject { ["type"] = "register", ["id"] = "reg0", ["payload"] = payload };
        try { await Net.SendTextAsync(ws, _gate, reg.ToJsonString()); }
        catch { ws.Dispose(); return ConnResult.Fail; }

        using var wait = new CancellationTokenSource(45000);
        try
        {
            while (true)
            {
                var msg = await Net.ReceiveTextAsync(ws, wait.Token);
                if (msg == null) break;
                string type, id, key = null;
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    type = Net.Str(doc.RootElement, "type");
                    id = Net.Str(doc.RootElement, "id");
                    if (doc.RootElement.TryGetProperty("payload", out var p)) key = Net.Str(p, "client-key");
                }
                catch { continue; }

                if (type == "registered")
                {
                    if (!string.IsNullOrEmpty(key)) { Tv.Token = key; TvStore.Save(); }
                    _ws = ws;
                    _secure = url.StartsWith("wss");
                    _cts = new CancellationTokenSource();
                    _ = ReadLoopAsync(ws, _cts.Token);
                    return ConnResult.Ok;
                }
                if (type == "error" && id == "reg0") { ws.Dispose(); return ConnResult.Denied; }
            }
        }
        catch { }
        ws.Dispose();
        return ConnResult.Fail;
    }

    async Task ReadLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await Net.ReceiveTextAsync(ws, ct);
                if (msg == null) break;
                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    var id = Net.Str(doc.RootElement, "id");
                    if (id != null && _pending.TryRemove(id, out var tcs)) tcs.TrySetResult(doc.RootElement.Clone());
                }
                catch { }
            }
        }
        catch { }
        if (_ws == ws && !Disposed)
        {
            IsConnected = false;
            Status(StatusKind.Error, "Desconectado — toca un botón para reconectar");
        }
    }

    async Task<JsonElement?> Request(string uri, JsonObject payload = null)
    {
        if (!IsConnected && !await ConnectAsync()) return null;
        var id = "r" + Interlocked.Increment(ref _n);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var msg = new JsonObject { ["type"] = "request", ["id"] = id, ["uri"] = uri, ["payload"] = payload ?? new JsonObject() };
        try { await Net.SendTextAsync(_ws, _gate, msg.ToJsonString()); }
        catch
        {
            _pending.TryRemove(id, out _);
            IsConnected = false;
            Status(StatusKind.Error, "Se perdió la conexión — toca de nuevo");
            return null;
        }
        var done = await Task.WhenAny(tcs.Task, Task.Delay(5000));
        _pending.TryRemove(id, out _);
        return done == tcs.Task ? tcs.Task.Result : null;
    }

    async Task OpenPointerAsync()
    {
        var r = await Request("ssap://com.webos.service.networkinput/getPointerInputSocket");
        if (r is not JsonElement el || !el.TryGetProperty("payload", out var p)) return;
        var path = Net.Str(p, "socketPath");
        if (path == null) return;
        if (_secure) path = path.Replace("ws://", "wss://").Replace(":3000", ":3001");
        var pws = Net.NewWebSocket();
        try
        {
            using var c = new CancellationTokenSource(5000);
            await pws.ConnectAsync(new Uri(path), c.Token);
            _pws = pws;
        }
        catch { pws.Dispose(); }
    }

    async Task PressButton(string name)
    {
        if (!IsConnected && !await ConnectAsync()) return;
        if (_pws == null || _pws.State != WebSocketState.Open) await OpenPointerAsync();
        if (_pws == null || _pws.State != WebSocketState.Open) { Say("Navegación no disponible en esta TV"); return; }
        try { await Net.SendTextAsync(_pws, _pgate, $"type:button\nname:{name}\n\n"); }
        catch { _pws = null; }
    }

    public override async Task SendAsync(string a)
    {
        var d = Digit(a);
        if (d != null) { await PressButton(d); return; }
        if (Buttons.TryGetValue(a, out var b)) { await PressButton(b); return; }
        if (a == "mute") { _muted = !_muted; await Request("ssap://audio/setMute", new JsonObject { ["mute"] = _muted }); return; }
        if (a == "play") { _playing = !_playing; await Request(_playing ? "ssap://media.controls/play" : "ssap://media.controls/pause"); return; }
        if (Apps.TryGetValue(a, out var app)) { await Request("ssap://system.launcher/launch", new JsonObject { ["id"] = app }); return; }
        if (Uris.TryGetValue(a, out var uri)) { await Request(uri); return; }
        Unsupported();
    }

    public override Task SendTextAsync(string text) =>
        Request("ssap://com.webos.service.ime/insertText", new JsonObject { ["text"] = text, ["replace"] = 0 });

    public override void Dispose()
    {
        base.Dispose();
        try { _cts?.Cancel(); } catch { }
        var w = _ws; var p = _pws; _ws = null; _pws = null;
        try { w?.Dispose(); } catch { }
        try { p?.Dispose(); } catch { }
    }
}
