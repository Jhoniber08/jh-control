using JHControl.Models;

namespace JHControl.Services;

/// <summary>Infrarrojo del teléfono (Xiaomi, Redmi, Poco, algunos Huawei/Honor…). Funciona hasta con TVs que no son Smart.</summary>
public static class IrBlaster
{
    public static bool Available
    {
        get
        {
#if ANDROID
            try { var ir = Manager(); return ir != null && ir.HasIrEmitter; }
            catch { return false; }
#else
            return false;
#endif
        }
    }

#if ANDROID
    static Android.Hardware.ConsumerIrManager Manager() =>
        (Android.Hardware.ConsumerIrManager)Android.App.Application.Context.GetSystemService(Android.Content.Context.ConsumerIrService);
#endif

    public static bool Transmit(int carrierHz, int[] pattern)
    {
#if ANDROID
        try { Manager()?.Transmit(carrierHz, pattern); return true; }
        catch { return false; }
#else
        return false;
#endif
    }

    /// <summary>Protocolo NEC (LG y muchas marcas) o Samsung32.</summary>
    public static int[] Nec(int address, int command, bool samsung)
    {
        var p = new List<int> { samsung ? 4500 : 9000, 4500 };
        byte a = (byte)address, c = (byte)command;
        byte[] bytes = samsung ? new[] { a, a, c, (byte)~c } : new[] { a, (byte)~a, c, (byte)~c };
        foreach (var b in bytes)
            for (int i = 0; i < 8; i++)
            {
                p.Add(560);
                p.Add(((b >> i) & 1) == 1 ? 1690 : 560);
            }
        p.Add(560);
        p.Add(20000);
        return p.ToArray();
    }

    /// <summary>Protocolo Sony SIRC de 12 bits (se repite 3 veces).</summary>
    public static int[] Sony(int address, int command)
    {
        var p = new List<int>();
        for (int r = 0; r < 3; r++)
        {
            var frame = new List<int> { 2400, 600 };
            for (int i = 0; i < 7; i++) { frame.Add(((command >> i) & 1) == 1 ? 1200 : 600); frame.Add(600); }
            for (int i = 0; i < 5; i++) { frame.Add(((address >> i) & 1) == 1 ? 1200 : 600); frame.Add(600); }
            frame[^1] += Math.Max(0, 45000 - frame.Sum());
            p.AddRange(frame);
        }
        return p.ToArray();
    }
}

public sealed class IrDriver : TvDriverBase
{
    static readonly Dictionary<string, int> Samsung = new()
    {
        ["power"] = 0x02, ["input"] = 0x01, ["volup"] = 0x07, ["voldown"] = 0x0B, ["mute"] = 0x0F,
        ["chup"] = 0x12, ["chdown"] = 0x10, ["menu"] = 0x1A, ["home"] = 0x79, ["info"] = 0x1F, ["exit"] = 0x2D,
        ["up"] = 0x60, ["down"] = 0x61, ["left"] = 0x65, ["right"] = 0x62, ["ok"] = 0x68, ["back"] = 0x58,
        ["play"] = 0x47, ["rew"] = 0x45, ["ff"] = 0x48,
        ["n1"] = 0x04, ["n2"] = 0x05, ["n3"] = 0x06, ["n4"] = 0x08, ["n5"] = 0x09, ["n6"] = 0x0A,
        ["n7"] = 0x0C, ["n8"] = 0x0D, ["n9"] = 0x0E, ["n0"] = 0x11
    };
    static readonly Dictionary<string, int> Lg = new()
    {
        ["power"] = 0x08, ["input"] = 0x0B, ["volup"] = 0x02, ["voldown"] = 0x03, ["mute"] = 0x09,
        ["chup"] = 0x00, ["chdown"] = 0x01, ["menu"] = 0x43, ["home"] = 0x7C, ["info"] = 0xAA, ["exit"] = 0x5B,
        ["up"] = 0x40, ["down"] = 0x41, ["left"] = 0x07, ["right"] = 0x06, ["ok"] = 0x44, ["back"] = 0x28,
        ["play"] = 0xB0, ["rew"] = 0x8F, ["ff"] = 0x8E,
        ["n0"] = 0x10, ["n1"] = 0x11, ["n2"] = 0x12, ["n3"] = 0x13, ["n4"] = 0x14,
        ["n5"] = 0x15, ["n6"] = 0x16, ["n7"] = 0x17, ["n8"] = 0x18, ["n9"] = 0x19
    };
    static readonly Dictionary<string, int> SonyCodes = new()
    {
        ["power"] = 21, ["input"] = 37, ["volup"] = 18, ["voldown"] = 19, ["mute"] = 20,
        ["chup"] = 16, ["chdown"] = 17, ["home"] = 96, ["menu"] = 96, ["info"] = 58, ["back"] = 99, ["exit"] = 99,
        ["up"] = 116, ["down"] = 117, ["left"] = 52, ["right"] = 51, ["ok"] = 101,
        ["n1"] = 0, ["n2"] = 1, ["n3"] = 2, ["n4"] = 3, ["n5"] = 4, ["n6"] = 5, ["n7"] = 6, ["n8"] = 7, ["n9"] = 8, ["n0"] = 9
    };

    public IrDriver(TvDevice tv) : base(tv) { }

    protected override Task<bool> DoConnectAsync()
    {
        if (IrBlaster.Available)
        {
            IsConnected = true;
            Status(StatusKind.Ok, "Infrarrojo listo · apunta el teléfono a la TV");
            return Task.FromResult(true);
        }
        Status(StatusKind.Error, "Este teléfono no tiene infrarrojo");
        return Task.FromResult(false);
    }

    public override Task SendAsync(string a)
    {
        bool ok = Tv.Model switch
        {
            "samsung" => Samsung.TryGetValue(a, out var s) && IrBlaster.Transmit(38000, IrBlaster.Nec(0x07, s, true)),
            "lg" => Lg.TryGetValue(a, out var l) && IrBlaster.Transmit(38000, IrBlaster.Nec(0x04, l, false)),
            "sony" => SonyCodes.TryGetValue(a, out var y) && IrBlaster.Transmit(40000, IrBlaster.Sony(1, y)),
            _ => false
        };
        if (!ok) Unsupported();
        return Task.CompletedTask;
    }

    public override Task SendTextAsync(string text)
    {
        Say("Por infrarrojo no se puede escribir texto");
        return Task.CompletedTask;
    }
}
