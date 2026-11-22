using System.Text.Json;
using System.Text.Json.Serialization;
using JHControl.Models;

namespace JHControl.Services;

[JsonSerializable(typeof(List<TvDevice>))]
internal partial class AppJson : JsonSerializerContext { }

/// <summary>Guarda los televisores en el teléfono.</summary>
public static class TvStore
{
    const string KeyTvs = "jh_tvs";
    const string KeyCurrent = "jh_current";
    static List<TvDevice> _cache;

    public static List<TvDevice> All => _cache ??= Load();

    static List<TvDevice> Load()
    {
        try
        {
            var s = Preferences.Default.Get(KeyTvs, "");
            if (!string.IsNullOrEmpty(s))
                return JsonSerializer.Deserialize(s, AppJson.Default.ListTvDevice) ?? new List<TvDevice>();
        }
        catch { }
        return new List<TvDevice>();
    }

    public static void Save()
    {
        try { Preferences.Default.Set(KeyTvs, JsonSerializer.Serialize(All, AppJson.Default.ListTvDevice)); }
        catch { }
    }

    public static string CurrentId
    {
        get => Preferences.Default.Get(KeyCurrent, "");
        set => Preferences.Default.Set(KeyCurrent, value ?? "");
    }

    public static TvDevice Current => All.FirstOrDefault(t => t.Id == CurrentId);

    /// <summary>Agrega o actualiza una TV encontrada (la reconoce por su Uid o IP).</summary>
    public static TvDevice Upsert(TvDevice d)
    {
        var ex = All.FirstOrDefault(t => t.Brand == d.Brand &&
                   ((!string.IsNullOrEmpty(d.Uid) && t.Uid == d.Uid) || (d.Brand != "ir" && t.Ip == d.Ip)));
        if (ex == null)
        {
            ex = new TvDevice { Name = d.Name, Brand = d.Brand, Ip = d.Ip, Model = d.Model, Uid = d.Uid };
            All.Add(ex);
        }
        else
        {
            ex.Ip = d.Ip;
            if (!d.GenericName && !string.IsNullOrWhiteSpace(d.Name)) ex.Name = d.Name;
            ex.Model = d.Model ?? ex.Model;
            ex.Uid = d.Uid ?? ex.Uid;
        }
        Save();
        return ex;
    }

    public static void Remove(string id)
    {
        All.RemoveAll(t => t.Id == id);
        if (CurrentId == id) CurrentId = All.FirstOrDefault()?.Id;
        Save();
    }
}
