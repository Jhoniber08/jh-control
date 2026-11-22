using JHControl.Models;
using JHControl.Services;

namespace JHControl;

public partial class MainPage : ContentPage
{
    ITvDriver _driver;
    TvDevice _tv;
    string _connectedIp;
    bool _firstAppear = true, _relocating, _opening;
    (StatusKind Kind, string Text) _lastStatus;

    readonly IDispatcherTimer _repeat;
    string _repeatAction;
    DateTime _repeatStart;
    int _toastVersion;

    public MainPage()
    {
        InitializeComponent();
        _repeat = Dispatcher.CreateTimer();
        _repeat.Interval = TimeSpan.FromMilliseconds(130);
        _repeat.Tick += OnRepeatTick;
        SetPadMode(Preferences.Default.Get("pad_mode", false));
    }

    // ───────────── Ciclo de vida ─────────────
    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _opening = false;
        var cur = TvStore.Current;
        if (cur == null)
        {
            ShowNoTv();
            if (_firstAppear) { _firstAppear = false; await OpenDevicesAsync(); }
            return;
        }
        _firstAppear = false;

        if (_driver == null || _tv?.Id != cur.Id || _connectedIp != cur.Ip)
            await UseTvAsync(cur);
        else if (!_driver.IsConnected)
            await ConnectOrRelocateAsync();
    }

    public async void AppResumed()
    {
        if (_driver != null && !_driver.IsConnected) await ConnectOrRelocateAsync();
    }

    void ShowNoTv()
    {
        _driver?.Dispose();
        _driver = null; _tv = null; _connectedIp = null;
        TvName.Text = "Buscar televisor";
        SetStatus(StatusKind.None, "Toca aquí para encontrar tu TV");
    }

    async Task UseTvAsync(TvDevice tv)
    {
        _driver?.Dispose();
        _tv = tv;
        _connectedIp = tv.Ip;
        TvName.Text = tv.Name;
        _driver = DriverFactory.Create(tv);
        if (_driver == null) { SetStatus(StatusKind.Error, "Esta TV aún no es compatible"); return; }

        var me = _driver;
        _driver.StatusChanged += (k, t) => MainThread.BeginInvokeOnMainThread(() =>
        {
            if (me != _driver) return;
            _lastStatus = (k, t);
            if (!_relocating) SetStatus(k, t);
        });
        _driver.Notice += t => MainThread.BeginInvokeOnMainThread(() => ShowToast(t));
        _driver.RequestCode = () => MainThread.InvokeOnMainThreadAsync(() =>
            DisplayPromptAsync("Código de la TV",
                "Tu TV muestra un código de 6 caracteres. Escríbelo aquí para emparejar (solo la primera vez).",
                "Emparejar", "Cancelar", "Ej. 4F2A9C", 6, Keyboard.Text));
        await ConnectOrRelocateAsync();
    }

    /// <summary>Conecta; si la TV cambió de IP, la vuelve a encontrar sola por su nombre / número de serie.</summary>
    async Task ConnectOrRelocateAsync()
    {
        if (_driver == null || _relocating) return;
        if (await _driver.ConnectAsync()) return;
        if (!_driver.Unreachable || _tv.Brand == "ir") return; // la TV respondió: no hace falta buscarla

        var tv = _tv;
        _relocating = true;
        TvDevice match = null;
        try
        {
            SetStatus(StatusKind.Wait, "Buscando tu TV en la red…");
            using var cts = new CancellationTokenSource();
            await new TvDiscovery().RunAsync(d =>
            {
                if (match != null || d.Brand != tv.Brand) return;
                bool sameUid = !string.IsNullOrEmpty(tv.Uid) && d.Uid == tv.Uid;
                bool sameName = !d.GenericName && d.Name == tv.Name;
                if (sameUid || sameName) { match = d; try { cts.Cancel(); } catch { } }
            }, TimeSpan.FromSeconds(10), cts.Token);
        }
        catch { }
        finally { _relocating = false; }

        if (match == null)
            SetStatus(StatusKind.Error, "No encuentro tu TV. ¿Está encendida y en el mismo WiFi?");
        else if (match.Ip != tv.Ip)
        {
            tv.Ip = match.Ip;
            TvStore.Save();
            ShowToast("Tu TV cambió de dirección; ya la encontré");
            await UseTvAsync(tv);
        }
        else
            SetStatus(_lastStatus.Kind, _lastStatus.Text ?? "La TV no responde");
    }

    // ───────────── Botones ─────────────
    async void OnKeyPressed(object sender, EventArgs e)
    {
        if (sender is not Button b || b.CommandParameter is not string action) return;
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
        if (b.ClassId == "rep")
        {
            _repeatAction = action;
            _repeatStart = DateTime.UtcNow;
            _repeat.Start();
        }
        await SendAsync(action);
    }

    void OnKeyReleased(object sender, EventArgs e)
    {
        _repeat.Stop();
        _repeatAction = null;
    }

    async void OnRepeatTick(object sender, EventArgs e)
    {
        var elapsed = DateTime.UtcNow - _repeatStart;
        if (_repeatAction == null || elapsed.TotalSeconds > 10) { _repeat.Stop(); return; }
        if (elapsed.TotalMilliseconds < 450) return;
        await SendAsync(_repeatAction);
    }

    async Task SendAsync(string action)
    {
        if (_driver == null) { await OpenDevicesAsync(); return; }
        try { await _driver.SendAsync(action); }
        catch { SetStatus(StatusKind.Error, "No se pudo enviar"); }
    }

    // ───────────── Modo Botones / Deslizar ─────────────
    void OnModeButtons(object sender, EventArgs e) => SetPadMode(false);
    void OnModePad(object sender, EventArgs e) => SetPadMode(true);

    void SetPadMode(bool pad)
    {
        PadPanel.IsVisible = pad;
        DpadPanel.IsVisible = !pad;
        ModePad.BackgroundColor = pad ? Color.FromArgb("#4F8CFF") : Colors.Transparent;
        ModeBtns.BackgroundColor = pad ? Colors.Transparent : Color.FromArgb("#4F8CFF");
        // En modo Deslizar la pantalla queda fija para que los gestos no muevan la página
        Scroller.Orientation = pad ? ScrollOrientation.Neither : ScrollOrientation.Vertical;
        if (pad) _ = Scroller.ScrollToAsync(0, 0, false);
        Preferences.Default.Set("pad_mode", pad);
    }

    async void OnSwiped(object sender, SwipedEventArgs e)
    {
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
        await SendAsync(e.Direction switch
        {
            SwipeDirection.Up => "up",
            SwipeDirection.Down => "down",
            SwipeDirection.Left => "left",
            _ => "right"
        });
    }

    async void OnPadTapped(object sender, TappedEventArgs e)
    {
        try { HapticFeedback.Default.Perform(HapticFeedbackType.Click); } catch { }
        await SendAsync("ok");
    }

    void OnToggleNum(object sender, EventArgs e)
    {
        NumPanel.IsVisible = !NumPanel.IsVisible;
        TxtPanel.IsVisible = false;
    }

    void OnToggleTxt(object sender, EventArgs e)
    {
        TxtPanel.IsVisible = !TxtPanel.IsVisible;
        NumPanel.IsVisible = false;
        if (TxtPanel.IsVisible) TxtEntry.Focus();
    }

    async void OnSendText(object sender, EventArgs e)
    {
        var text = TxtEntry.Text;
        if (string.IsNullOrEmpty(text)) return;
        if (_driver == null) { ShowToast("Primero elige tu TV"); return; }
        await _driver.SendTextAsync(text);
        TxtEntry.Text = "";
        ShowToast("Texto enviado");
    }

    async void OnOpenDevices(object sender, EventArgs e) => await OpenDevicesAsync();

    async Task OpenDevicesAsync()
    {
        if (_opening || Navigation.NavigationStack.Count > 1) return;
        _opening = true;
        await Navigation.PushAsync(new DevicesPage());
    }

    // ───────────── UI ─────────────
    void SetStatus(StatusKind kind, string text)
    {
        StatusLbl.Text = text;
        Dot.Fill = new SolidColorBrush(kind switch
        {
            StatusKind.Ok => Color.FromArgb("#2ECC71"),
            StatusKind.Wait => Color.FromArgb("#F5B942"),
            StatusKind.Error => Color.FromArgb("#FF4D5E"),
            _ => Color.FromArgb("#8B93A8")
        });
    }

    async void ShowToast(string text)
    {
        var v = ++_toastVersion;
        ToastLbl.Text = text;
        Toast.IsVisible = true;
        await Toast.FadeTo(1, 150);
        await Task.Delay(2400);
        if (v != _toastVersion) return;
        await Toast.FadeTo(0, 250);
        if (v == _toastVersion) Toast.IsVisible = false;
    }
}
