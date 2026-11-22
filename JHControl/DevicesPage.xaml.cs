using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using JHControl.Models;
using JHControl.Services;

namespace JHControl;

public partial class DevicesPage : ContentPage
{
    public ObservableCollection<TvDevice> Found { get; } = new();
    public ObservableCollection<TvDevice> Saved { get; } = new();

    CancellationTokenSource _cts;
    bool _searching;

    public DevicesPage()
    {
        InitializeComponent();
        BindingContext = this;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        RefreshSaved();
        IrBox.IsVisible = IrBlaster.Available;
        if (!_searching && Found.Count == 0) _ = SearchAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        try { _cts?.Cancel(); } catch { }
    }

    void RefreshSaved()
    {
        Saved.Clear();
        var cur = TvStore.CurrentId;
        foreach (var t in TvStore.All.OrderByDescending(t => t.Id == cur)) Saved.Add(t);
        SavedTitle.IsVisible = Saved.Count > 0;
    }

    // ───────────── Búsqueda automática ─────────────
    async Task SearchAsync()
    {
        if (_searching) return;
        _searching = true;
        Found.Clear();
        EmptyBox.IsVisible = false;
        Spinner.IsVisible = true;
        _ = PulseAsync();
        SearchBtn.IsEnabled = false;
        SearchBtn.Text = "Buscando…";

        var profiles = Connectivity.Current.ConnectionProfiles;
        bool onWifi = profiles.Contains(ConnectionProfile.WiFi) || profiles.Contains(ConnectionProfile.Ethernet);
        SubLbl.Text = onWifi
            ? "Buscando televisores en tu WiFi…"
            : "⚠️ No estás en WiFi. Conéctate a la misma red que tu TV.";

        Prog.Progress = 0;
        _ = Prog.ProgressTo(1, 12000, Easing.Linear);

        _cts = new CancellationTokenSource();
        try
        {
            await new TvDiscovery().RunAsync(
                d => MainThread.BeginInvokeOnMainThread(() => AddFound(d)),
                TimeSpan.FromSeconds(12), _cts.Token);
        }
        catch { }

        _searching = false;
        Prog.CancelAnimations();
        Prog.Progress = 1;
        Spinner.IsVisible = false;
        SearchBtn.IsEnabled = true;
        SearchBtn.Text = "Buscar de nuevo";
        SubLbl.Text = Found.Count == 0
            ? "No encontré televisores."
            : $"Encontré {Found.Count} {(Found.Count == 1 ? "televisor" : "televisores")}. Toca el tuyo.";
        EmptyBox.IsVisible = Found.Count == 0;
    }

    async Task PulseAsync()
    {
        while (_searching)
        {
            Pulse.Scale = 0.4;
            Pulse.Opacity = 0.6;
            await Task.WhenAll(Pulse.ScaleTo(1.0, 1000, Easing.CubicOut), Pulse.FadeTo(0, 1000));
        }
    }

    void AddFound(TvDevice d)
    {
        var existing = Found.FirstOrDefault(x => x.Ip == d.Ip);
        if (existing != null) { Found[Found.IndexOf(existing)] = d; return; }
        if (d.IsSupported) Found.Insert(Found.Count(x => x.IsSupported), d);
        else Found.Add(d);
        if (_searching) SubLbl.Text = $"Encontrando… ({Found.Count})";
    }

    async void OnSearchClicked(object sender, EventArgs e) => await SearchAsync();

    // ───────────── Elegir TV ─────────────
    async void OnFoundTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is not TvDevice d) return;
        if (!d.IsSupported)
        {
            await DisplayAlert(d.Name,
                "Esta TV usa Android TV / Google TV u otro sistema que todavía no es compatible. " +
                "JH Control funciona con Roku, Samsung y LG.", "Entendido");
            return;
        }
        var saved = TvStore.Upsert(d);
        TvStore.CurrentId = saved.Id;
        await Navigation.PopAsync();
    }

    async void OnSavedTapped(object sender, TappedEventArgs e)
    {
        if (e.Parameter is not TvDevice d) return;
        TvStore.CurrentId = d.Id;
        await Navigation.PopAsync();
    }

    void OnDeleteSaved(object sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not TvDevice d) return;
        TvStore.Remove(d.Id);
        RefreshSaved();
    }

    // ───────────── Infrarrojo ─────────────
    async void OnIrBrand(object sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not string model) return;
        var label = model switch { "samsung" => "Samsung", "lg" => "LG", _ => "Sony" };
        var saved = TvStore.Upsert(new TvDevice { Brand = "ir", Model = model, Uid = "ir-" + model, Name = $"{label} (infrarrojo)" });
        TvStore.CurrentId = saved.Id;
        await Navigation.PopAsync();
    }

    // ───────────── Manual ─────────────
    void OnToggleManual(object sender, EventArgs e) => ManualBox.IsVisible = !ManualBox.IsVisible;

    async void OnAddManual(object sender, EventArgs e)
    {
        var ip = (ManualIp.Text ?? "").Trim();
        if (!Regex.IsMatch(ip, @"^(\d{1,3}\.){3}\d{1,3}$"))
        {
            await DisplayAlert("IP no válida", "Escribe la IP así: 192.168.1.45", "OK");
            return;
        }
        var brand = ManualBrand.SelectedIndex switch { 1 => "samsung", 2 => "lg", 3 => "androidtv", _ => "roku" };
        var name = string.IsNullOrWhiteSpace(ManualName.Text) ? $"TV {ip}" : ManualName.Text.Trim();
        var saved = TvStore.Upsert(new TvDevice { Name = name, Brand = brand, Ip = ip });
        TvStore.CurrentId = saved.Id;
        await Navigation.PopAsync();
    }
}
