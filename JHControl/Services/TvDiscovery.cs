using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using JHControl.Models;

namespace JHControl.Services;

/// <summary>
/// Encuentra televisores en el WiFi automáticamente, de dos formas a la vez:
///  1) SSDP / UPnP (el mismo método que usan las apps oficiales).
///  2) Escaneo rápido de tu red local (detecta la IP de tu teléfono solo).
/// Luego le pregunta a cada TV su nombre real.
/// </summary>
public class TvDiscovery
{
    static readonly string[] SearchTargets =
    {
        "roku:ecp",
        "urn:samsung.com:device:RemoteControlReceiver:1",
        "urn:lge-com:service:webos-second-screen:1",
        "urn:dial-multiscreen-org:service:dial:1",
        "upnp:rootdevice"
    };

    // 8060 Roku · 8001/8002 Samsung · 3000/3001 LG · 6466 Android/Google TV · 8008 Chromecast
    static readonly int[] Ports = { 8060, 8001, 6466, 3000, 8002, 3001, 8008 };

    readonly Dictionary<string, TvDevice> _found = new();
    readonly HashSet<string> _identifying = new();
    readonly object _lock = new();
    Action<TvDevice> _onFound;

    /// <summary>Solo para pruebas: red a revisar en vez de la del teléfono.</summary>
    public List<IPAddress> LocalsOverride { get; set; }

    public async Task RunAsync(Action<TvDevice> onFound, TimeSpan duration, CancellationToken ct = default)
    {
        _onFound = onFound;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(duration);
        var locals = LocalsOverride ?? LocalAddresses();
        var mlock = AcquireMulticastLock();
        try
        {
            await Task.WhenAll(SsdpAsync(locals, cts.Token), MdnsAsync(locals, cts.Token), ScanAsync(locals, cts.Token));
        }
        catch { }
        finally { ReleaseMulticastLock(mlock); }
    }

    // ───────────────────────── Resultado ─────────────────────────
    void Report(TvDevice d)
    {
        TvDevice result;
        lock (_lock)
        {
            if (_found.TryGetValue(d.Ip, out var ex))
            {
                if (ex.IsSupported && !d.IsSupported)
                {
                    // Ej.: LG con Chromecast integrado → nos sirve su nombre bonito
                    if (ex.GenericName && !d.GenericName) { ex.Name = d.Name; ex.GenericName = false; }
                    else return;
                }
                else if (!ex.IsSupported && d.IsSupported)
                {
                    if (d.GenericName && !ex.GenericName) { d.Name = ex.Name; d.GenericName = false; }
                    _found[d.Ip] = d;
                }
                else
                {
                    if (ex.Brand != d.Brand) return;
                    bool changed = false;
                    if (ex.GenericName && !d.GenericName) { ex.Name = d.Name; ex.GenericName = false; changed = true; }
                    if (string.IsNullOrEmpty(ex.Model) && !string.IsNullOrEmpty(d.Model)) { ex.Model = d.Model; changed = true; }
                    if (string.IsNullOrEmpty(ex.Uid) && !string.IsNullOrEmpty(d.Uid)) { ex.Uid = d.Uid; changed = true; }
                    if (!changed) return;
                }
            }
            else _found[d.Ip] = d;

            result = _found[d.Ip].Clone();
        }
        try { _onFound?.Invoke(result); } catch { }
    }

    bool Claim(string key)
    {
        lock (_lock) return _identifying.Add(key);
    }

    // ───────────────────────── 1) SSDP ─────────────────────────
    async Task SsdpAsync(List<IPAddress> locals, CancellationToken ct)
    {
        var binds = locals.Count > 0 ? locals : new List<IPAddress> { IPAddress.Any };
        await Task.WhenAll(binds.Select(ip => SsdpOnAsync(ip, ct)));
    }

    async Task SsdpOnAsync(IPAddress local, CancellationToken ct)
    {
        UdpClient udp;
        try { udp = new UdpClient(new IPEndPoint(local, 0)); }
        catch { return; }

        using (udp)
        {
            try { udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 4); } catch { }
            if (!local.Equals(IPAddress.Any))
            {
                try { udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes()); } catch { }
            }
            var group = new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900);

            // Envía las búsquedas 3 veces (los paquetes UDP a veces se pierden)
            _ = Task.Run(async () =>
            {
                for (int round = 0; round < 3 && !ct.IsCancellationRequested; round++)
                {
                    foreach (var st in SearchTargets)
                    {
                        var msg = "M-SEARCH * HTTP/1.1\r\n" +
                                  "HOST: 239.255.255.250:1900\r\n" +
                                  "MAN: \"ssdp:discover\"\r\n" +
                                  "MX: 2\r\n" +
                                  $"ST: {st}\r\n\r\n";
                        var bytes = Encoding.ASCII.GetBytes(msg);
                        try { await udp.SendAsync(bytes, bytes.Length, group); } catch { }
                        try { await Task.Delay(60, ct); } catch { return; }
                    }
                    try { await Task.Delay(1500, ct); } catch { return; }
                }
            });

            var seen = new HashSet<string>();
            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(ct); }
                catch { break; }

                var h = ParseHeaders(Encoding.UTF8.GetString(r.Buffer));
                var ip = r.RemoteEndPoint.Address.ToString();
                h.TryGetValue("location", out var loc);
                h.TryGetValue("st", out var st);
                h.TryGetValue("server", out var server);
                h.TryGetValue("usn", out var usn);
                if (!seen.Add(loc ?? ip + st)) continue;

                _ = Task.Run(() => IdentifySsdpAsync(ip, st ?? "", server ?? "", loc, usn ?? "", ct));
            }
        }
    }

    static Dictionary<string, string> ParseHeaders(string text)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var i = line.IndexOf(':');
            if (i <= 0) continue;
            d[line[..i].Trim().ToLowerInvariant()] = line[(i + 1)..].Trim();
        }
        return d;
    }

    async Task IdentifySsdpAsync(string ip, string st, string server, string loc, string usn, CancellationToken ct)
    {
        try
        {
            var s = (st + " " + server).ToLowerInvariant();
            if (s.Contains("roku")) { await IdentifyRokuAsync(ip, ct, true); return; }
            if (s.Contains("samsung") && await IdentifySamsungAsync(ip, ct)) return;

            if (s.Contains("lge") || s.Contains("webos"))
            {
                var u = await ReadUpnpAsync(loc, ct);
                Report(new TvDevice
                {
                    Brand = "lg", Ip = ip,
                    Name = u.Friendly ?? "LG webOS TV", GenericName = u.Friendly == null,
                    Model = u.Model, Uid = UuidFrom(usn)
                });
                return;
            }

            if (loc == null) return;
            var info = await ReadUpnpAsync(loc, ct);
            if (info.Manufacturer == null) return;
            var m = info.Manufacturer.ToLowerInvariant();
            bool tvLike = st.Contains("dial") || (info.DeviceType ?? "").ToLowerInvariant().Contains("dial")
                          || (info.DeviceType ?? "").Contains("MediaRenderer") || (info.Model ?? "").ToLowerInvariant().Contains("tv");

            if (m.Contains("roku")) await IdentifyRokuAsync(ip, ct, true);
            else if (m.Contains("samsung")) await IdentifySamsungAsync(ip, ct);
            else if ((m.StartsWith("lg") || m.Contains("lg electronics")) && tvLike)
                Report(new TvDevice
                {
                    Brand = "lg", Ip = ip, Name = info.Friendly ?? "LG webOS TV", GenericName = info.Friendly == null,
                    Model = info.Model, Uid = UuidFrom(usn)
                });
            else if (st.Contains("dial"))
                Report(new TvDevice
                {
                    Brand = "other", Ip = ip, Name = info.Friendly ?? "Televisor", GenericName = info.Friendly == null,
                    Model = $"{info.Manufacturer} {info.Model}".Trim()
                });
        }
        catch { }
    }

    record struct UpnpInfo(string Friendly, string Manufacturer, string Model, string DeviceType);

    static async Task<UpnpInfo> ReadUpnpAsync(string location, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(location)) return default;
        var (code, xml) = await Net.GetAsync(location, 3000, ct);
        if (code != 200 || xml == null) return default;
        return new UpnpInfo(Net.Tag(xml, "friendlyName"), Net.Tag(xml, "manufacturer"),
                            Net.Tag(xml, "modelName"), Net.Tag(xml, "deviceType"));
    }

    static string UuidFrom(string usn)
    {
        if (string.IsNullOrEmpty(usn)) return null;
        var i = usn.IndexOf("uuid:", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = usn[(i + 5)..];
        var end = rest.IndexOf("::", StringComparison.Ordinal);
        return end > 0 ? rest[..end] : rest;
    }


    // ───────────────────────── 3) mDNS (Android TV / Google TV / Chromecast) ─────────────────────────
    async Task MdnsAsync(List<IPAddress> locals, CancellationToken ct)
    {
        var binds = locals.Count > 0 ? locals : new List<IPAddress> { IPAddress.Any };
        await Task.WhenAll(binds.Select(ip => MdnsOnAsync(ip, ct)));
    }

    async Task MdnsOnAsync(IPAddress local, CancellationToken ct)
    {
        UdpClient udp;
        try { udp = new UdpClient(new IPEndPoint(local, 0)); }
        catch { return; }

        using (udp)
        {
            if (!local.Equals(IPAddress.Any))
            {
                try { udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes()); } catch { }
            }
            var query = BuildMdnsQuery("_androidtvremote2._tcp.local", "_googlecast._tcp.local");
            var group = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
            _ = Task.Run(async () =>
            {
                for (int r = 0; r < 3 && !ct.IsCancellationRequested; r++)
                {
                    try { await udp.SendAsync(query, query.Length, group); } catch { }
                    try { await Task.Delay(1500, ct); } catch { return; }
                }
            });

            while (!ct.IsCancellationRequested)
            {
                UdpReceiveResult r;
                try { r = await udp.ReceiveAsync(ct); }
                catch { break; }
                var ip = r.RemoteEndPoint.Address.ToString();
                try
                {
                    foreach (var rec in ParseDns(r.Buffer))
                    {
                        if (rec.Type == 12 && rec.Target != null &&
                            rec.Name.StartsWith("_androidtvremote2", StringComparison.OrdinalIgnoreCase))
                        {
                            var label = rec.Target.Split('.')[0];
                            Report(new TvDevice { Brand = "androidtv", Ip = ip, Name = label, Uid = label });
                        }
                        else if (rec.Type == 16 && rec.Txt != null &&
                                 rec.Name.Contains("_googlecast", StringComparison.OrdinalIgnoreCase))
                        {
                            var fn = rec.Txt.FirstOrDefault(t => t.StartsWith("fn="))?[3..];
                            var md = rec.Txt.FirstOrDefault(t => t.StartsWith("md="))?[3..];
                            if (!string.IsNullOrWhiteSpace(fn))
                                Report(new TvDevice { Brand = "other", Ip = ip, Name = fn, Model = md });
                        }
                    }
                }
                catch { }
            }
        }
    }

    static byte[] BuildMdnsQuery(params string[] names)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0, 0, 0, 0, 0, (byte)names.Length, 0, 0, 0, 0, 0, 0 });
        foreach (var n in names)
        {
            foreach (var label in n.Split('.'))
            {
                var b = Encoding.ASCII.GetBytes(label);
                ms.WriteByte((byte)b.Length);
                ms.Write(b);
            }
            ms.WriteByte(0);
            ms.Write(new byte[] { 0, 12, 0x80, 0x01 }); // PTR, pedir respuesta directa
        }
        return ms.ToArray();
    }

    record DnsRecord(string Name, int Type, string Target, List<string> Txt);

    static List<DnsRecord> ParseDns(byte[] d)
    {
        var list = new List<DnsRecord>();
        if (d.Length < 12) return list;
        int qd = (d[4] << 8) | d[5], an = (d[6] << 8) | d[7], ns = (d[8] << 8) | d[9], ar = (d[10] << 8) | d[11];
        int i = 12;
        for (int q = 0; q < qd && i < d.Length; q++) { ReadName(d, ref i); i += 4; }
        for (int k = 0; k < an + ns + ar && i + 10 <= d.Length; k++)
        {
            var name = ReadName(d, ref i);
            if (i + 10 > d.Length) break;
            int type = (d[i] << 8) | d[i + 1];
            int rdlen = (d[i + 8] << 8) | d[i + 9];
            i += 10;
            int start = i;
            if (start + rdlen > d.Length) break;
            string target = null; List<string> txt = null;
            if (type == 12) { int j = start; target = ReadName(d, ref j); }
            else if (type == 16)
            {
                txt = new List<string>();
                int j = start;
                while (j < start + rdlen)
                {
                    int l = d[j++];
                    if (j + l > start + rdlen) break;
                    txt.Add(Encoding.UTF8.GetString(d, j, l));
                    j += l;
                }
            }
            list.Add(new DnsRecord(name, type, target, txt));
            i = start + rdlen;
        }
        return list;
    }

    static string ReadName(byte[] d, ref int i)
    {
        var labels = new List<string>();
        int pos = i, jumps = 0;
        bool jumped = false;
        while (pos < d.Length)
        {
            int len = d[pos];
            if (len == 0) { pos++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= d.Length) break;
                int ptr = ((len & 0x3F) << 8) | d[pos + 1];
                if (!jumped) { i = pos + 2; jumped = true; }
                pos = ptr;
                if (++jumps > 20) break;
                continue;
            }
            pos++;
            if (pos + len > d.Length) break;
            labels.Add(Encoding.UTF8.GetString(d, pos, len));
            pos += len;
        }
        if (!jumped) i = pos;
        return string.Join(".", labels);
    }

    // ───────────────────────── 2) Escaneo de la red ─────────────────────────
    async Task ScanAsync(List<IPAddress> locals, CancellationToken ct)
    {
        var prefixes = locals.Select(a => { var b = a.GetAddressBytes(); return $"{b[0]}.{b[1]}.{b[2]}"; })
                             .Distinct().ToList();
        var mine = locals.Select(a => a.ToString()).ToHashSet();
        using var gate = new SemaphoreSlim(120);
        var tasks = new List<Task>();
        foreach (var port in Ports)
            foreach (var p in prefixes)
                for (int i = 1; i < 255; i++)
                {
                    var ip = $"{p}.{i}";
                    if (mine.Contains(ip)) continue;
                    tasks.Add(ProbeAsync(ip, port, gate, ct));
                }
        try { await Task.WhenAll(tasks); } catch { }
    }

    async Task ProbeAsync(string ip, int port, SemaphoreSlim gate, CancellationToken ct)
    {
        try { await gate.WaitAsync(ct); }
        catch { return; }
        bool open;
        try { open = await IsOpenAsync(ip, port, ct); }
        finally { gate.Release(); }
        if (!open || ct.IsCancellationRequested) return;

        try
        {
            switch (port)
            {
                case 8060:
                    if (Claim(ip + "|roku")) await IdentifyRokuAsync(ip, ct, true);
                    break;
                case 8001:
                case 8002:
                    if (Claim(ip + "|samsung")) await IdentifySamsungAsync(ip, ct);
                    break;
                case 3000:
                case 3001:
                    if (Claim(ip + "|lg"))
                        Report(new TvDevice { Brand = "lg", Ip = ip, Name = "LG webOS TV", GenericName = true });
                    break;
                case 6466:
                    if (Claim(ip + "|atv"))
                    {
                        Report(new TvDevice { Brand = "androidtv", Ip = ip, Name = "Android TV", GenericName = true });
                        if (Claim(ip + "|cast")) await IdentifyCastAsync(ip, ct); // su nombre real
                    }
                    break;
                case 8008:
                    if (Claim(ip + "|cast")) await IdentifyCastAsync(ip, ct);
                    break;
            }
        }
        catch { }
    }

    static async Task<bool> IsOpenAsync(string ip, int port, CancellationToken ct)
    {
        using var client = new TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(600);
        try
        {
            await client.ConnectAsync(ip, port, cts.Token);
            return client.Connected;
        }
        catch { return false; }
    }

    // ───────────────────────── Identificar cada marca ─────────────────────────
    async Task<bool> IdentifyRokuAsync(string ip, CancellationToken ct, bool reportEvenIfBlocked)
    {
        var (code, x) = await Net.GetAsync($"http://{ip}:8060/query/device-info", 3000, ct);
        if (code == 200 && x != null && x.Contains("<device-info"))
        {
            var name = Net.Tag(x, "user-device-name") ?? Net.Tag(x, "friendly-device-name") ?? Net.Tag(x, "default-device-name");
            Report(new TvDevice
            {
                Brand = "roku", Ip = ip,
                Name = name ?? "Roku", GenericName = name == null,
                Model = Net.Tag(x, "friendly-model-name") ?? Net.Tag(x, "model-name"),
                Uid = Net.Tag(x, "serial-number") ?? Net.Tag(x, "device-id")
            });
            return true;
        }
        if (reportEvenIfBlocked && code != 0)
        {
            // El Roku respondió pero tiene limitado el control por apps
            Report(new TvDevice { Brand = "roku", Ip = ip, Name = "Roku", GenericName = true, Model = "Roku" });
            return true;
        }
        return false;
    }

    async Task<bool> IdentifySamsungAsync(string ip, CancellationToken ct)
    {
        var (code, j) = await Net.GetAsync($"http://{ip}:8001/api/v2/", 3000, ct);
        if (code != 200 || string.IsNullOrEmpty(j))
            (code, j) = await Net.GetAsync($"https://{ip}:8002/api/v2/", 3000, ct);
        if (code != 200 || string.IsNullOrEmpty(j)) return false;
        try
        {
            using var doc = JsonDocument.Parse(j);
            var root = doc.RootElement;
            if (!root.TryGetProperty("device", out var dev)) return false;
            var name = Net.Str(dev, "name") ?? Net.Str(root, "name");
            if (name != null) name = WebUtility.HtmlDecode(name).Replace("[TV]", "").Trim();
            Report(new TvDevice
            {
                Brand = "samsung", Ip = ip,
                Name = string.IsNullOrEmpty(name) ? "Samsung TV" : name, GenericName = string.IsNullOrEmpty(name),
                Model = Net.Str(dev, "modelName"),
                Uid = Net.Str(dev, "id") ?? Net.Str(dev, "duid")
            });
            return true;
        }
        catch { return false; }
    }

    async Task IdentifyCastAsync(string ip, CancellationToken ct)
    {
        var (code, xml) = await Net.GetAsync($"http://{ip}:8008/ssdp/device-desc.xml", 3000, ct);
        if (code != 200 || xml == null) return;
        var friendly = Net.Tag(xml, "friendlyName");
        var manu = Net.Tag(xml, "manufacturer") ?? "";
        var model = Net.Tag(xml, "modelName") ?? "";
        Report(new TvDevice
        {
            Brand = "other", Ip = ip, Name = friendly ?? "Google TV", GenericName = friendly == null,
            Model = $"{manu} {model}".Trim()
        });
    }

    // ───────────────────────── IP del teléfono ─────────────────────────
    public static List<IPAddress> LocalAddresses()
    {
        var list = new List<(IPAddress Ip, int Score)>();

#if ANDROID
        try
        {
            var ctx = Android.App.Application.Context;
            var cm = (Android.Net.ConnectivityManager)ctx.GetSystemService(Android.Content.Context.ConnectivityService);
            foreach (var network in cm.GetAllNetworks())
            {
                var caps = cm.GetNetworkCapabilities(network);
                if (caps == null) continue;
                bool wifi = caps.HasTransport(Android.Net.TransportType.Wifi) || caps.HasTransport(Android.Net.TransportType.Ethernet);
                if (!wifi) continue;
                var lp = cm.GetLinkProperties(network);
                if (lp == null) continue;
                foreach (var la in lp.LinkAddresses)
                {
                    if (la.Address is Java.Net.Inet4Address v4 && IPAddress.TryParse(v4.HostAddress, out var ip) && IsPrivate(ip))
                        list.Add((ip, 0));
                }
            }
        }
        catch { }
#endif

        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus == OperationalStatus.Down) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var n = (ni.Name ?? "").ToLowerInvariant();
                if (n.StartsWith("rmnet") || n.StartsWith("pdp_ip") || n.StartsWith("ccmni") || n.StartsWith("tun") ||
                    n.StartsWith("utun") || n.StartsWith("ipsec") || n.StartsWith("dummy") || n.StartsWith("p2p")) continue;
                int score = n.StartsWith("wlan") || n == "en0" || n.StartsWith("wl") ? 1 : 2;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(ua.Address))
                        list.Add((ua.Address, score));
                }
            }
        }
        catch { }

        return list.OrderBy(x => x.Score).Select(x => x.Ip).Distinct().Take(3).ToList();
    }

    static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }

    // Android necesita este "candado" para recibir respuestas multicast (SSDP)
    static object AcquireMulticastLock()
    {
#if ANDROID
        try
        {
            var wifi = (Android.Net.Wifi.WifiManager)Android.App.Application.Context.GetSystemService(Android.Content.Context.WifiService);
            var l = wifi.CreateMulticastLock("jhcontrol");
            l.SetReferenceCounted(false);
            l.Acquire();
            return l;
        }
        catch { return null; }
#else
        return null;
#endif
    }

    static void ReleaseMulticastLock(object o)
    {
#if ANDROID
        try { (o as Android.Net.Wifi.WifiManager.MulticastLock)?.Release(); } catch { }
#endif
    }
}
