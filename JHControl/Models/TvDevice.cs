using System.Text.Json.Serialization;
using Microsoft.Maui.Graphics;

namespace JHControl.Models;

public class TvDevice
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Name { get; set; } = "";
    /// <summary>roku | samsung | lg | androidtv | ir | other</summary>
    public string Brand { get; set; } = "";
    public string Ip { get; set; } = "";
    public string Model { get; set; }
    /// <summary>Identificador único de la TV (serie / uuid) para encontrarla aunque cambie la IP.</summary>
    public string Uid { get; set; }
    /// <summary>Token / client-key que da la TV al aceptar la conexión.</summary>
    public string Token { get; set; }

    [JsonIgnore] public bool GenericName { get; set; }

    [JsonIgnore] public bool IsSupported => Brand is "roku" or "samsung" or "lg" or "androidtv" or "ir";

    [JsonIgnore]
    public string BrandLabel => Brand switch
    {
        "roku" => "ROKU",
        "samsung" => "SAMSUNG",
        "lg" => "LG",
        "androidtv" => "GOOGLE TV",
        "ir" => "INFRARROJO",
        _ => "TV"
    };

    [JsonIgnore]
    public Color BrandColor => Brand switch
    {
        "roku" => Color.FromArgb("#6F1AB1"),
        "samsung" => Color.FromArgb("#1428A0"),
        "lg" => Color.FromArgb("#A50034"),
        "androidtv" => Color.FromArgb("#1E8E3E"),
        "ir" => Color.FromArgb("#B86E00"),
        _ => Color.FromArgb("#3A4050")
    };

    [JsonIgnore]
    public string Subtitle => Brand switch
    {
        "ir" => "Infrarrojo del teléfono · apunta a la TV",
        "androidtv" => $"Android TV / Google TV · {Ip}",
        _ when IsSupported => string.IsNullOrWhiteSpace(Model) ? Ip : $"{Model} · {Ip}",
        _ => $"{(string.IsNullOrWhiteSpace(Model) ? "Dispositivo" : Model)} · aún no compatible"
    };

    [JsonIgnore] public double RowOpacity => IsSupported ? 1 : 0.55;

    public TvDevice Clone() => new()
    {
        Id = Id, Name = Name, Brand = Brand, Ip = Ip, Model = Model, Uid = Uid, Token = Token, GenericName = GenericName
    };
}
