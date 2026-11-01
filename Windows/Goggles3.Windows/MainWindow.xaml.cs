using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Goggles3.Core;
using ControlAppearance = Wpf.Ui.Controls.ControlAppearance;
using SymbolIcon = Wpf.Ui.Controls.SymbolIcon;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;
using WindowCornerPreference = Wpf.Ui.Controls.WindowCornerPreference;

namespace Goggles3.Windows;

/// <summary>Runs sessions and recordings; what to show comes from <see cref="ViewerState"/> (Core, tested on Linux).</summary>
public partial class MainWindow
{
    private const string SetupQuestion = "The viewer will add the address 192.168.60.1 to the goggles USB adapter only. " +
        "Wi-Fi, Ethernet, DNS and internet settings stay unchanged. Windows asks for admin permission. Set it up now?";

    private FfmpegVideoSession? session;
    private readonly ViewerState state = new();
    private readonly DispatcherTimer stats = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Stopwatch recordingClock = new();
    private readonly WriteableBitmap bitmap = new(1920,1080,96,96,PixelFormats.Bgr32,null);
    private readonly DiagnosticLog log = new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Goggle 3 LiveView", "diagnostic.txt"));
    private double rate;
    private int pendingUi;
    private bool recording, saving, busy, closing, closed, fullscreen, replay;
    private long generation;
    private Rect savedBounds;
    private WindowState savedState;
    private bool savedTopmost;
    private string? currentRecordingPath;
    private CancellationTokenSource? autoConnect;
    private Task autoConnectTask = Task.CompletedTask;

    private double Now => clock.Elapsed.TotalSeconds;

    public MainWindow()
    {
        InitializeComponent();
        Video.Source = bitmap;
        stats.Tick += (_,_) => RefreshStats(); stats.Start();
        Loaded += (_,_) =>
        {
            if (SystemParameters.WorkArea.Width >= MinWidth) Width = Math.Min(Width,SystemParameters.WorkArea.Width);
            if (SystemParameters.WorkArea.Height >= MinHeight) Height = Math.Min(Height,SystemParameters.WorkArea.Height);
            Left = SystemParameters.WorkArea.Left + Math.Max(0, (SystemParameters.WorkArea.Width - Width) / 2);
            Top = SystemParameters.WorkArea.Top + Math.Max(0, (SystemParameters.WorkArea.Height - Height) / 2);
            string[] args = Environment.GetCommandLineArgs();
            int replayIndex = Array.IndexOf(args, "--replay");
            if (replayIndex >= 0 && replayIndex + 1 < args.Length) StartReplay(args[replayIndex + 1]);
            else if (args.Contains("--connect")) StartAutoConnect(); // superflash: connect and keep reconnecting, no dialogs
            ConnectButton.Focus();
            Render();
        };
        var diagnosticMenu = new System.Windows.Controls.ContextMenu();
        var item = new System.Windows.Controls.MenuItem { Header = "Diagnostics" };
        item.Click += (_,_) => { log.Flush(); MessageBox.Show(this, string.Join(Environment.NewLine, log.Lines), "USB diagnostics"); };
        diagnosticMenu.Items.Add(item); Stage.ContextMenu = diagnosticMenu;
    }

    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (busy || closing) return;
        // While --connect waits for setup the button reads "Set up": set up and let the running loop connect.
        bool setupOnly = autoConnect != null && session == null && state.Connection == ViewerStatus.NeedsSetup;
        busy = true; ConnectButton.IsEnabled = false;
        try
        {
            if ((session != null || autoConnect != null) && !setupOnly) { await DisconnectAsync(); return; }
            if (!setupOnly) { state.Connection = ViewerStatus.Connecting; Render(); }
            if ((await AdapterLocator.FindAsync()).State == AdapterState.NeedsSetup)
            {
                if (MessageBox.Show(this, SetupQuestion, "Set up USB connection", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                { if (!setupOnly) state.Connection = ViewerStatus.SetupCancelled; return; }
                await AdapterLocator.PrepareAsync();
            }
            if (closing || setupOnly) return;
            StartAutoConnect();
        }
        catch (Exception error) { log.Add(error.Message); await DisconnectAsync(); state.Connection = ViewerStatus.Stopped(error.Message); }
        finally { busy = false; if (!closing) { ConnectButton.IsEnabled = true; Render(); } }
    }

    /// <summary>Connect until Disconnect/close: retries while the goggles are absent, reconnects when a session ends.</summary>
    private void StartAutoConnect()
    {
        replay = false; autoConnect = new CancellationTokenSource();
        state.Connection = ViewerStatus.Connecting; Render();
        var loop = new AutoConnect(_ => AdapterLocator.FindAsync(),
            (address, token) => Dispatcher.InvokeAsync(() => RunSessionAsync(address, token)).Task.Unwrap(),
            status => Dispatcher.BeginInvoke(new Action(() => ShowConnection(status))));
        autoConnectTask = loop.RunAsync(autoConnect.Token);
    }

    private void ShowConnection(ViewerStatus status)
    {
        if (autoConnect == null || closing) return; // a late update after Disconnect
        if (status != state.Connection) log.Add($"{status.Badge}: {status.Title}"); // the loop repeats its status every 2 s: log changes only
        state.Connection = status; Render();
    }

    /// <summary>One live session on the UI thread; completes with the reason it ended.</summary>
    private async Task<string> RunSessionAsync(IPAddress address, CancellationToken token)
    {
        var ended = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CreateSession(message => ended.TrySetResult(message)).Start(new UdpVideoReceiver(address));
        string reason;
        using (token.Register(() => ended.TrySetResult("Disconnected")))
            reason = await ended.Task;
        await StopSessionAsync();
        return reason;
    }

    private string DecoderPath => Path.Combine(AppContext.BaseDirectory,"ffmpeg","ffmpeg.exe");

    private FfmpegVideoSession CreateSession(Action<string> ended)
    {
        if (!File.Exists(DecoderPath)) throw new FileNotFoundException("Decoder missing. Extract the whole ZIP, not just the .exe file.");
        long token = ++generation;
        var active = new FfmpegVideoSession(DecoderPath); session = active;
        rate = 0; state.SessionStarted(Now, replay);
        active.Frame += () =>
        {
            if (Interlocked.CompareExchange(ref pendingUi,1,0) != 0) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
            {
                try
                {
                    if (token != generation || closing) return;
                    if (active.TakeLatestFrame() is not { } pixels) return;
                    bitmap.WritePixels(new Int32Rect(0,0,1920,1080), pixels,1920*4,0);
                    bool hadVideo = state.HasVideo(Now);
                    state.FrameShown(Now);
                    if (!hadVideo) Render(); // video (re)appeared: show it now, not at the next tick
                }
                finally { Interlocked.Exchange(ref pendingUi,0); }
            }));
        };
        active.Bitrate += value => Dispatcher.BeginInvoke(new Action(() => { if (token == generation) rate = value; }));
        active.Log += text => { if (token == Interlocked.Read(ref generation)) log.Add(text); };
        active.Ended += message => Dispatcher.BeginInvoke(new Action(() => { if (token == generation) ended(message); }));
        active.RecordingEnded += result => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (result.Path != currentRecordingPath) return;
            recording = false; saving = false; recordingClock.Reset();
            RecordingText.Foreground = (Brush)FindResource(result.Error == null ? "Muted" : "Alert");
            RecordingText.Text = result.Error ?? "Saved: " + Path.GetFileName(result.Path) +
                (result.Dropped > 0 ? " (some video was skipped, the computer was too busy)" : "");
            RecordingText.ToolTip = result.Path;
            Render();
        }));
        return active;
    }

    private void StartReplay(string path)
    {
        try
        {
            replay = true; state.Connection = ViewerStatus.Connecting;
            CreateSession(async message =>
            {
                if (closing) return;
                await DisconnectAsync(); state.Connection = ViewerStatus.Stopped(message); Render();
            }).StartReplay(path);
        }
        catch (Exception error) { state.Connection = ViewerStatus.Stopped(error.Message); log.Add(error.Message); }
        Render();
    }

    // One disconnect at a time: a Close during a running disconnect (still saving) must wait for that one.
    private Task disconnecting = Task.CompletedTask;
    private Task DisconnectAsync() => disconnecting = disconnecting.IsCompleted ? DisconnectCoreAsync() : disconnecting;

    private async Task DisconnectCoreAsync()
    {
        CancellationTokenSource? loop = autoConnect; autoConnect = null;
        if (loop != null)
        {
            loop.Cancel();
            try { await autoConnectTask; } catch (OperationCanceledException) { } // the loop stops its own session
            loop.Dispose();
        }
        await StopSessionAsync();
        state.Connection = ViewerStatus.NotConnected; Render();
    }

    /// <summary>Ends the current session (saving a running recording) and clears the video, keeps the connect state.</summary>
    private async Task StopSessionAsync()
    {
        generation++; FfmpegVideoSession? active = session; session = null;
        state.SessionStopped();
        if (recording) { recording = false; saving = true; }
        Render();
        if (active != null) await active.DisposeAsync();
        rate = 0; Render();
    }

    /// <summary>Second launch (single instance): show this window; with --connect also connect if idle.</summary>
    public void BringToFront(bool connect)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
        if (connect && session == null && autoConnect == null && !busy && !closing) StartAutoConnect();
    }

    private void RefreshStats()
    {
        log.Flush();
        int frames = state.TakeFrameCount();
        bool video = state.HasVideo(Now);
        Fps.Text = video ? $"{frames} fps" : "—";
        Bitrate.Text = video && !replay && rate > 0 ? $"{rate:F1} Mbps" : "—";
        if (recording) RecordingText.Text = $"Recording · {recordingClock.Elapsed:h\\:mm\\:ss}";
        Render();
    }

    /// <summary>Shows <see cref="state"/>: status badge, idle text, video, buttons. Called on each change and once per second.</summary>
    private void Render()
    {
        double now = Now;
        ViewerStatus shown = state.Status(now);
        bool video = state.HasVideo(now);
        Status.Text = shown.Badge;
        StatusDot.Fill = (Brush)FindResource(shown.Light switch
        {
            ViewerLight.Live => "Mint", ViewerLight.Waiting => "Amber", ViewerLight.Problem => "Alert", _ => "Off",
        });
        IdleTitle.Text = shown.Title; IdleHint.Text = shown.Hint;
        IdleHint.Visibility = shown.Hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Video.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
        Idle.Visibility = video ? Visibility.Collapsed : Visibility.Visible;

        if (session == null && autoConnect == null) SetButton(ConnectButton, "Connect", SymbolRegular.PlugConnected24, ControlAppearance.Primary);
        else if (session == null && state.Connection == ViewerStatus.NeedsSetup) SetButton(ConnectButton, "Set up", SymbolRegular.Wrench24, ControlAppearance.Primary);
        else SetButton(ConnectButton, "Disconnect", SymbolRegular.PlugDisconnected24, ControlAppearance.Secondary);

        if (saving) SetButton(RecordButton, "Saving…", SymbolRegular.RecordStop24, ControlAppearance.Secondary);
        else if (recording) SetButton(RecordButton, "Stop", SymbolRegular.RecordStop24, ControlAppearance.Danger);
        else SetButton(RecordButton, "Record", SymbolRegular.Record24, ControlAppearance.Secondary);
        RecordButton.IsEnabled = !saving && !closing && (recording || (video && session != null)); // Stop stays usable when video drops
    }

    private static void SetButton(Wpf.Ui.Controls.Button button, string label, SymbolRegular icon, ControlAppearance look)
    {
        if (Equals(button.Content, label)) return; // new icon objects only on a change
        button.Content = label; button.Icon = new SymbolIcon { Symbol = icon }; button.Appearance = look;
    }

    private async void RecordClick(object sender, RoutedEventArgs e)
    {
        FfmpegVideoSession? active = session;
        if (active == null || saving) return;
        try
        {
            if (recording)
            {
                recording = false; saving = true; Render();
                await active.StopRecordingAsync();
            }
            else
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"Goggle 3 LiveView");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory,$"Goggle3LiveView_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString()[..4]}.mp4");
                currentRecordingPath = path; active.StartRecording(path); recording = true; recordingClock.Restart();
                RecordingText.Foreground = (Brush)FindResource("Alert");
                RecordingText.Text = "Recording · 0:00:00"; RecordingText.ToolTip = path;
                Render();
            }
        }
        catch (Exception error)
        {
            RecordingText.Foreground = (Brush)FindResource("Alert"); RecordingText.Text = error.Message; RecordingText.ToolTip = null;
            log.Add(error.Message);
        }
    }

    private void FullscreenClick(object sender, RoutedEventArgs e) => SetFullscreen(true);
    private void SetFullscreen(bool enable)
    {
        if (enable == fullscreen) return;
        if (enable)
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(new WindowInteropHelper(this).Handle,2),ref info)) return;
            fullscreen = true;
            savedBounds = RestoreBounds; savedState = WindowState; savedTopmost = Topmost;
            Matrix transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            Point origin = transform.Transform(new Point(info.Monitor.Left,info.Monitor.Top));
            Point extent = transform.Transform(new Point(info.Monitor.Right,info.Monitor.Bottom));
            WindowState = WindowState.Normal; ResizeMode = ResizeMode.NoResize; WindowCornerPreference = WindowCornerPreference.DoNotRound;
            Left = origin.X; Top = origin.Y; Width = extent.X-origin.X; Height = extent.Y-origin.Y; Topmost = true;
            TitleBar.Visibility = ControlBar.Visibility = Visibility.Collapsed;
        }
        else
        {
            fullscreen = false;
            Topmost = savedTopmost; ResizeMode = ResizeMode.CanResize; WindowCornerPreference = WindowCornerPreference.Round;
            Left = savedBounds.Left; Top = savedBounds.Top; Width = savedBounds.Width; Height = savedBounds.Height;
            WindowState = savedState; TitleBar.Visibility = ControlBar.Visibility = Visibility.Visible;
        }
    }
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && fullscreen) { SetFullscreen(false); e.Handled = true; }
        else if (e.Key == Key.F11) { SetFullscreen(!fullscreen); e.Handled = true; }
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closed) return; // cleanup finished: let the window close
        e.Cancel = true;     // every Close/Alt-F4 waits until the recording is saved
        if (closing) return;
        closing = true; ConnectButton.IsEnabled = RecordButton.IsEnabled = false;
        stats.Stop(); await DisconnectAsync();
        await log.CloseAsync(); // the last lines (why it stopped) must reach the file
        closed = true; Close();
    }
    private void TutorialClick(object sender, RoutedEventArgs e) => new TutorialWindow { Owner = this }.ShowDialog();

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor,Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Auto)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
}
