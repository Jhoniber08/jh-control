using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using JHControl.Models;

namespace JHControl.Services;

/// <summary>Protobuf mínimo (solo lo que usa el protocolo de Android TV).</summary>
public sealed class Pb
{
    readonly MemoryStream _ms = new();

    public Pb Varint(int field, long value) { Tag(field, 0); WriteVarint(_ms, (ulong)value); return this; }
    public Pb Bytes(int field, byte[] data) { Tag(field, 2); WriteVarint(_ms, (ulong)data.Length); _ms.Write(data, 0, data.Length); return this; }
    public Pb Str(int field, string s) => Bytes(field, Encoding.UTF8.GetBytes(s));
    public Pb Msg(int field, Pb inner) => Bytes(field, inner.ToArray());
    public byte[] ToArray() => _ms.ToArray();

    void Tag(int field, int wireType) => WriteVarint(_ms, (ulong)((field << 3) | wireType));

    public static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    public readonly record struct Field(int Number, ulong Value, byte[] Data);

    public static List<Field> Parse(byte[] d)
    {
        var list = new List<Field>();
        int i = 0;
        while (i < d.Length)
        {
            ulong key = ReadVarint(d, ref i);
            int number = (int)(key >> 3), wire = (int)(key & 7);
            if (wire == 0) list.Add(new Field(number, ReadVarint(d, ref i), null));
            else if (wire == 2)
            {
                int len = (int)ReadVarint(d, ref i);
                if (len < 0 || i + len > d.Length) break;
                list.Add(new Field(number, 0, d.AsSpan(i, len).ToArray()));
                i += len;
            }
            else if (wire == 5) i += 4;
            else if (wire == 1) i += 8;
            else break;
        }
        return list;
    }

    static ulong ReadVarint(byte[] d, ref int i)
    {
        ulong v = 0; int shift = 0;
        while (i < d.Length && shift < 64)
        {
            byte b = d[i++];
            v |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return v;
    }
}

/// <summary>Certificado propio de JH Control (Android TV exige uno para emparejar).</summary>
public static class AtvCert
{
    static X509Certificate2 _cert;

    public static X509Certificate2 Get()
    {
        if (_cert != null) return _cert;
        try
        {
            var saved = Preferences.Default.Get("atv_cert", "");
            if (!string.IsNullOrEmpty(saved))
                return _cert = X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(saved), null, X509KeyStorageFlags.Exportable);
        }
        catch { }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=atvremote, O=JH Control", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var created = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        var pfx = created.Export(X509ContentType.Pfx);
        try { Preferences.Default.Set("atv_cert", Convert.ToBase64String(pfx)); } catch { }
        return _cert = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }
}

// ════════════════════ ANDROID TV / GOOGLE TV (Android TV Remote v2) ════════════════════
// Sony, TCL Google TV, Xiaomi, Philips Android, Hisense Android, Westinghouse Google TV, Chromecast con Google TV…
public sealed class AndroidTvDriver : TvDriverBase
{
    const int RemotePort = 6466, PairPort = 6467;

    static readonly Dictionary<string, int> Keys = new()
    {
        ["power"] = 26, ["input"] = 178, ["mute"] = 164, ["home"] = 3, ["back"] = 4, ["menu"] = 82,
        ["info"] = 165, ["exit"] = 4, ["up"] = 19, ["down"] = 20, ["left"] = 21, ["right"] = 22, ["ok"] = 23,
        ["volup"] = 24, ["voldown"] = 25, ["chup"] = 166, ["chdown"] = 167,
        ["rew"] = 89, ["play"] = 85, ["ff"] = 90, ["search"] = 84, ["backspace"] = 67, ["replay"] = 89,
        ["n0"] = 7, ["n1"] = 8, ["n2"] = 9, ["n3"] = 10, ["n4"] = 11, ["n5"] = 12, ["n6"] = 13, ["n7"] = 14, ["n8"] = 15, ["n9"] = 16
    };
    static readonly Dictionary<string, string> Apps = new()
    {
        ["netflix"] = "https://www.netflix.com/title", ["youtube"] = "https://www.youtube.com", ["prime"] = "https://app.primevideo.com"
    };

    TcpClient _tcp;
    SslStream _ssl;
    CancellationTokenSource _cts;
    TaskCompletionSource<bool> _ready;
    readonly SemaphoreSlim _gate = new(1, 1);

    public AndroidTvDriver(TvDevice tv) : base(tv) { }

    protected override async Task<bool> DoConnectAsync()
    {
        Status(StatusKind.Wait, "Conectando…");
        if (Tv.Token != "paired" && !await PairAsync()) return false;

        var r = await OpenRemoteAsync();
        if (r == ConnResult.Denied)
        {
            // La TV olvidó el emparejamiento: emparejar otra vez
            Tv.Token = null; TvStore.Save();
            if (!await PairAsync()) return false;
            r = await OpenRemoteAsync();
        }
        if (r == ConnResult.Ok) { IsConnected = true; Status(StatusKind.Ok, "Conectado"); return true; }

        Unreachable = r == ConnResult.Fail;
        Status(StatusKind.Error, "La TV no responde");
        return false;
    }

    static async Task<SslStream> TlsAsync(TcpClient tcp, string host)
    {
        var ssl = new SslStream(tcp.GetStream(), false);
        try
        {
            using var cts = new CancellationTokenSource(8000);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                ClientCertificates = new X509CertificateCollection { AtvCert.Get() },
                LocalCertificateSelectionCallback = (s, h, local, remote, issuers) => AtvCert.Get(),
                RemoteCertificateValidationCallback = (s, cert, chain, errors) => true
            }, cts.Token);
            return ssl;
        }
        catch { ssl.Dispose(); return null; }
    }

    static async Task<bool> ConnectTcpAsync(TcpClient tcp, string ip, int port)
    {
        try
        {
            using var cts = new CancellationTokenSource(5000);
            await tcp.ConnectAsync(ip, port, cts.Token);
            return true;
        }
        catch { return false; }
    }

    static async Task WriteMsgAsync(Stream s, byte[] msg, SemaphoreSlim gate)
    {
        using var buf = new MemoryStream();
        Pb.WriteVarint(buf, (ulong)msg.Length);
        buf.Write(msg, 0, msg.Length);
        var bytes = buf.ToArray();
        await gate.WaitAsync();
        try { await s.WriteAsync(bytes); await s.FlushAsync(); }
        finally { gate.Release(); }
    }

    static async Task<byte[]> ReadMsgAsync(Stream s, CancellationToken ct)
    {
        ulong len = 0; int shift = 0;
        var one = new byte[1];
        while (true)
        {
            int n = await s.ReadAsync(one, 0, 1, ct);
            if (n == 0) return null;
            len |= (ulong)(one[0] & 0x7F) << shift;
            if ((one[0] & 0x80) == 0) break;
            shift += 7;
            if (shift > 28) return null;
        }
        var data = new byte[len];
        await s.ReadExactlyAsync(data, ct);
        return data;
    }

    // ─────────── Emparejamiento (puerto 6467) ───────────
    async Task<bool> PairAsync()
    {
        Status(StatusKind.Wait, "Emparejando… mira la pantalla de la TV");
        using var tcp = new TcpClient();
        if (!await ConnectTcpAsync(tcp, Tv.Ip, PairPort))
        {
            Unreachable = true;
            Status(StatusKind.Error, "La TV no responde");
            return false;
        }
        using var ssl = await TlsAsync(tcp, Tv.Ip);
        if (ssl == null) { Status(StatusKind.Error, "No se pudo emparejar con la TV"); return false; }

        var gate = new SemaphoreSlim(1, 1);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        async Task<bool> Step(int field, Pb inner)
        {
            var outer = new Pb().Varint(1, 2).Varint(2, 200).Msg(field, inner);
            await WriteMsgAsync(ssl, outer.ToArray(), gate);
            var resp = await ReadMsgAsync(ssl, cts.Token);
            if (resp == null) return false;
            foreach (var f in Pb.Parse(resp))
                if (f.Number == 2 && f.Value != 200) return false;
            return true;
        }

        try
        {
            if (!await Step(10, new Pb().Str(1, "atvremote").Str(2, "JH Control"))) throw new IOException();
            if (!await Step(20, new Pb().Msg(1, new Pb().Varint(1, 3).Varint(2, 6)).Varint(3, 1))) throw new IOException();
            if (!await Step(30, new Pb().Msg(1, new Pb().Varint(1, 3).Varint(2, 6)).Varint(2, 1))) throw new IOException();

            // Ahora la TV muestra un código de 6 caracteres
            if (ssl.RemoteCertificate is not X509Certificate2 serverCert) throw new IOException();
            var sp = serverCert.GetRSAPublicKey().ExportParameters(false);
            var cp = AtvCert.Get().GetRSAPublicKey().ExportParameters(false);

            for (int attempt = 0; attempt < 3; attempt++)
            {
                Status(StatusKind.Wait, "Escribe el código que aparece en la TV");
                var code = RequestCode == null ? null : await RequestCode();
                if (string.IsNullOrWhiteSpace(code)) { Status(StatusKind.Error, "Emparejamiento cancelado"); return false; }
                code = code.Trim().ToUpperInvariant();
                if (!Regex.IsMatch(code, "^[0-9A-F]{6}$")) { Say("El código tiene 6 caracteres (0-9 y A-F)"); continue; }

                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                hash.AppendData(TrimZeros(cp.Modulus));
                hash.AppendData(TrimZeros(cp.Exponent));
                hash.AppendData(TrimZeros(sp.Modulus));
                hash.AppendData(TrimZeros(sp.Exponent));
                hash.AppendData(Convert.FromHexString(code[2..]));
                var secret = hash.GetHashAndReset();
                if (secret[0] != Convert.ToByte(code[..2], 16)) { Say("Código incorrecto, revísalo en la TV"); continue; }

                if (!await Step(40, new Pb().Bytes(1, secret))) { Status(StatusKind.Error, "La TV rechazó el código"); return false; }
                Tv.Token = "paired";
                TvStore.Save();
                Say("¡Emparejado!");
                return true;
            }
            Status(StatusKind.Error, "No se pudo emparejar");
            return false;
        }
        catch
        {
            Status(StatusKind.Error, "No se pudo emparejar con la TV");
            return false;
        }
    }

    static byte[] TrimZeros(byte[] b)
    {
        int i = 0;
        while (i < b.Length - 1 && b[i] == 0) i++;
        return b[i..];
    }

    // ─────────── Control (puerto 6466) ───────────
    async Task<ConnResult> OpenRemoteAsync()
    {
        var tcp = new TcpClient();
        if (!await ConnectTcpAsync(tcp, Tv.Ip, RemotePort)) { tcp.Dispose(); return ConnResult.Fail; }
        var ssl = await TlsAsync(tcp, Tv.Ip);
        if (ssl == null) { tcp.Dispose(); return ConnResult.Denied; }

        _tcp = tcp; _ssl = ssl;
        _ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _cts = new CancellationTokenSource();
        _ = ReadLoopAsync(ssl, _cts.Token);
        await Task.WhenAny(_ready.Task, Task.Delay(4000));
        return ConnResult.Ok;
    }

    async Task ReadLoopAsync(SslStream ssl, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var m = await ReadMsgAsync(ssl, ct);
                if (m == null) break;
                foreach (var f in Pb.Parse(m))
                {
                    switch (f.Number)
                    {
                        case 1: // RemoteConfigure → respondemos con los datos de la app
                            var info = new Pb().Str(1, "JH Control").Str(2, "Jhoniber").Varint(3, 1)
                                               .Str(4, "1").Str(5, "atvremote").Str(6, "1.0.0");
                            await WriteMsgAsync(ssl, new Pb().Msg(1, new Pb().Varint(1, 622).Msg(2, info)).ToArray(), _gate);
                            break;
                        case 2: // RemoteSetActive
                            await WriteMsgAsync(ssl, new Pb().Msg(2, new Pb().Varint(1, 622)).ToArray(), _gate);
                            _ready?.TrySetResult(true);
                            break;
                        case 8: // Ping → Pong
                            ulong v = 0;
                            if (f.Data != null) foreach (var g in Pb.Parse(f.Data)) if (g.Number == 1) v = g.Value;
                            await WriteMsgAsync(ssl, new Pb().Msg(9, new Pb().Varint(1, (long)v)).ToArray(), _gate);
                            break;
                    }
                }
            }
        }
        catch { }
        if (_ssl == ssl && !Disposed)
        {
            IsConnected = false;
            Status(StatusKind.Error, "Desconectado — toca un botón para reconectar");
        }
    }

    async Task SendMsg(Pb msg)
    {
        if (!IsConnected && !await ConnectAsync()) return;
        try { await WriteMsgAsync(_ssl, msg.ToArray(), _gate); }
        catch { IsConnected = false; Status(StatusKind.Error, "Se perdió la conexión — toca de nuevo"); }
    }

    public override async Task SendAsync(string a)
    {
        if (Apps.TryGetValue(a, out var link)) { await SendMsg(new Pb().Msg(90, new Pb().Str(1, link))); return; }
        if (Keys.TryGetValue(a, out var key)) { await SendMsg(new Pb().Msg(10, new Pb().Varint(1, key).Varint(2, 3))); return; }
        Unsupported();
    }

    public override Task SendTextAsync(string text)
    {
        Say("En Android TV usa el teclado de la pantalla de la TV");
        return Task.CompletedTask;
    }

    public override void Dispose()
    {
        base.Dispose();
        try { _cts?.Cancel(); } catch { }
        var s = _ssl; var t = _tcp; _ssl = null; _tcp = null;
        try { s?.Dispose(); } catch { }
        try { t?.Dispose(); } catch { }
    }
}
