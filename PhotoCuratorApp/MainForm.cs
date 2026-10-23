using System.Diagnostics;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using PhotoCurator.Core.Server;

namespace PhotoCuratorApp;

/// <summary>
/// The app window: hosts the local server and shows its UI in WebView2. Provides the things a web page can't do
/// itself: the Windows folder picker and "Show in Explorer". If the WebView2 runtime is missing (very old or locked-down
/// Windows), the UI opens in the default browser instead and this window stays as the off switch.
/// </summary>
sealed class MainForm : Form, IDesktopHost
{
    static readonly Color Background = Color.FromArgb(246, 247, 244);

    readonly WebView2 _web = new() { Dock = DockStyle.Fill, DefaultBackgroundColor = Background };
    readonly System.Windows.Forms.Timer _closeTimer = new() { Interval = 2000 };
    CuratorServer? _server;
    bool _flushed;

    public MainForm()
    {
        Text = "PhotoCurator";
        BackColor = Background;
        StartPosition = FormStartPosition.CenterScreen;
        using (var icon = typeof(MainForm).Assembly.GetManifestResourceStream("PhotoCuratorApp.app.ico"))
        {
            if (icon is not null) Icon = new Icon(icon);
        }
        Controls.Add(_web);
        _closeTimer.Tick += (_, _) => FinishClosing();
        FormClosing += MainForm_FormClosing;
    }

    protected override async void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // Sized in real pixels: a 1080p screen at 150% only has ~720 logical px of height.
        var area = Screen.FromPoint(MousePosition).WorkingArea;
        MinimumSize = new Size(Math.Min(Px(900), area.Width), Math.Min(Px(600), area.Height));
        Size = new Size(Math.Min(Px(1440), area.Width - Px(32)), Math.Min(Px(900), area.Height - Px(32)));
        CenterToScreen();

        try
        {
            _server = new CuratorServer(CuratorServer.DefaultDataDir, this);
            await _server.StartAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"PhotoCurator couldn't start: {ex.Message}", "PhotoCurator", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _flushed = true;
            Close();
            return;
        }

        try
        {
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(CuratorServer.DefaultDataDir, "WebView2"));
            await _web.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            ShowBrowserFallback();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.AreDevToolsEnabled = Debugger.IsAttached;
        core.Settings.IsZoomControlEnabled = false;            // Ctrl +/- resize thumbnails instead
        core.Settings.AreBrowserAcceleratorKeysEnabled = false; // no F5 reload, Ctrl+P, browser find; editing keys still work
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.DocumentTitleChanged += (_, _) => Text = core.DocumentTitle;
        core.WebMessageReceived += (_, args) =>
        {
            if (args.TryGetWebMessageAsString() == "flushed") FinishClosing();
        };
        core.NewWindowRequested += (_, args) =>
        {
            args.Handled = true;
            OpenExternally(args.Uri);
        };
        var origin = $"http://127.0.0.1:{_server.Port}/";
        core.NavigationStarting += (_, args) =>
        {
            if (args.Uri.StartsWith(origin, StringComparison.Ordinal)) return;
            args.Cancel = true;
            OpenExternally(args.Uri);
        };
        core.Navigate(_server.Url);
    }

    // ---------- IDesktopHost ----------

    public Task<string?> PickFolderAsync(string title, string? initialPath)
    {
        var result = new TaskCompletionSource<string?>();
        BeginInvoke(() =>
        {
            try
            {
                using var dialog = new FolderBrowserDialog { Description = title, UseDescriptionForTitle = true, ShowNewFolderButton = true };
                if (initialPath is not null && Directory.Exists(initialPath)) dialog.InitialDirectory = initialPath;
                result.SetResult(dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null);
            }
            catch (Exception ex)
            {
                result.SetException(ex);
            }
        });
        return result.Task;
    }

    public void Reveal(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { /* nothing to show */ }
    }

    // ---------- closing ----------

    /// <summary>Gives the page a moment to save an edit still waiting out its typing pause, then closes for real.</summary>
    void MainForm_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_flushed && _web.CoreWebView2 is { } core)
        {
            e.Cancel = true;
            if (_closeTimer.Enabled) return;
            _closeTimer.Start();
            try { core.PostWebMessageAsString("flush"); }
            catch (InvalidOperationException) { FinishClosing(); }
            return;
        }

        var server = _server;
        _server = null;
        if (server is not null) Task.Run(() => server.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(3));
    }

    void FinishClosing()
    {
        _closeTimer.Stop();
        _flushed = true;
        BeginInvoke(Close);
    }

    // ---------- helpers ----------

    void ShowBrowserFallback()
    {
        Controls.Remove(_web);
        var note = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 11f),
            ForeColor = Color.FromArgb(25, 28, 24),
            Text = "PhotoCurator is open in your web browser.\n\nKeep this window open while you work; closing it stops PhotoCurator.\n\n" +
                   "(Installing the Microsoft Edge WebView2 Runtime lets PhotoCurator run in its own window.)"
        };
        var reopen = new Button { Text = "Open in browser again", Dock = DockStyle.Bottom, Height = Px(44) };
        reopen.Click += (_, _) => OpenExternally(_server!.Url);
        Controls.Add(note);
        Controls.Add(reopen);
        Size = new Size(Px(560), Px(320));
        CenterToScreen();
        _flushed = true;
        OpenExternally(_server!.Url);
    }

    static void OpenExternally(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception) { /* no browser registered */ }
    }

    int Px(int logical) => (int)Math.Round(logical * DeviceDpi / 96.0);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closeTimer.Dispose();
            _web.Dispose();
        }
        base.Dispose(disposing);
    }
}
