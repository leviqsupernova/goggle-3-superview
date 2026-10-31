using System.Collections.Concurrent;
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
using System.Windows.Shell;
using System.Windows.Threading;
using Goggles3.Core;

namespace Goggles3.Windows;

public partial class MainWindow : Window
{
    private FfmpegVideoSession? session;
    private readonly DispatcherTimer stats = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly WriteableBitmap bitmap = new(1920,1080,96,96,PixelFormats.Bgr32,null);
    private double lastFrame, rate;
    private int frames, pendingUi;
    private bool recording, saving, busy, closing, closed, fullscreen, replay;
    private long generation;
    private Rect savedBounds;
    private WindowState savedState;
    private bool savedTopmost;
    private readonly Queue<string> log = [];
    // Any thread may log; the 1 s timer moves lines to the UI queue and the diagnostics file (no per-line UI work).
    private readonly ConcurrentQueue<string> incoming = new();
    private int writingLog;
    private bool logDirty;
    private Task logWrite = Task.CompletedTask;
    private string? currentRecordingPath;
    private CancellationTokenSource? autoConnect;
    private Task autoConnectTask = Task.CompletedTask;
    private string? lastStatus;

    public MainWindow()
    {
        InitializeComponent();
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
        };
        var diagnosticMenu = new System.Windows.Controls.ContextMenu();
        var item = new System.Windows.Controls.MenuItem { Header = "Diagnostics" };
        item.Click += (_,_) => { FlushLog(); MessageBox.Show(this, string.Join(Environment.NewLine, log), "USB diagnostics"); };
        diagnosticMenu.Items.Add(item); Hud.ContextMenu = diagnosticMenu;
    }

    private async void ConnectClick(object sender, RoutedEventArgs e)
    {
        if (busy || closing) return;
        busy = true; ConnectButton.IsEnabled = false;
        try
        {
            if (session != null || autoConnect != null) { await DisconnectAsync(); return; }
            Status.Text = AutoConnect.Connecting;
            if ((await AdapterLocator.FindAsync()).State == AdapterState.NeedsSetup)
            {
                if (MessageBox.Show(this, "The Goggles 3 RNDIS adapter needs to be configured. The address 192.168.60.1/24 will be added only to that adapter. Wi-Fi, Ethernet, DNS and your default gateway will not be changed. Windows will ask for administrator permission. Set it up now?",
                    "Set up USB connection", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                { Status.Text = "Connection cancelled"; return; }
                await AdapterLocator.PrepareAsync();
            }
            if (closing) return;
            StartAutoConnect();
        }
        catch (Exception error) { AddLog(error.Message); await DisconnectAsync(); Status.Text = error.Message; }
        finally { busy = false; if (!closing) ConnectButton.IsEnabled = true; }
    }

    /// <summary>Connect until Disconnect/close: retries while the goggles are absent, reconnects when a session ends.</summary>
    private void StartAutoConnect()
    {
        replay = false; autoConnect = new CancellationTokenSource();
        ShowConnectedLook();
        var loop = new AutoConnect(_ => AdapterLocator.FindAsync(),
            (address, token) => Dispatcher.InvokeAsync(() => RunSessionAsync(address, token)).Task.Unwrap(),
            text => Dispatcher.BeginInvoke(new Action(() => ShowStatus(text))));
        autoConnectTask = loop.RunAsync(autoConnect.Token);
    }

    private void ShowStatus(string text)
    {
        if (autoConnect == null || closing) return; // a late update after Disconnect
        Status.Text = text;
        if (text != lastStatus) AddLog(text); // the loop repeats its status every 2 s: log changes only
        lastStatus = text;
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
        if (!File.Exists(DecoderPath)) throw new FileNotFoundException("Decoder missing. Extract the entire ZIP, not just Goggle 3 LiveView.exe.");
        long token = ++generation;
        var active = new FfmpegVideoSession(DecoderPath); session = active;
        frames = 0; rate = 0; lastFrame = clock.Elapsed.TotalSeconds;
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
                    Video.Source = bitmap; Idle.Visibility = Visibility.Collapsed;
                    frames++; lastFrame = clock.Elapsed.TotalSeconds;
                    Status.Text = replay ? "Local test · recorded footage, not live USB" : "Live · video received over USB";
                    StatusDot.Fill = (Brush)FindResource("Mint");
                    RecordButton.IsEnabled = !saving;
                }
                finally { Interlocked.Exchange(ref pendingUi,0); }
            }));
        };
        active.Bitrate += value => Dispatcher.BeginInvoke(new Action(() => { if (token == generation) rate = value; }));
        active.Log += text => { if (token == Interlocked.Read(ref generation)) AddLog(text); };
        active.Ended += message => Dispatcher.BeginInvoke(new Action(() => { if (token == generation) ended(message); }));
        active.RecordingEnded += result => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (result.Path != currentRecordingPath) return;
            recording = false; saving = false; RecordLabel.Text = "Record"; RecordDot.Fill = Brushes.White;
            RecordingNotice.Visibility = Visibility.Visible;
            RecordingText.Foreground = Brushes.LightGray;
            RecordingText.Text = result.Error ?? $"Saved to Videos/Goggle 3 LiveView: {Path.GetFileName(result.Path)}" +
                (result.Dropped > 0 ? $" · some video was skipped (computer too busy)" : "");
            RecordButton.IsEnabled = session != null && Video.Source != null;
        }));
        ShowConnectedLook();
        return active;
    }

    private void ShowConnectedLook()
    {
        ConnectLabel.Text = "Disconnect";
        ConnectGlyph.Data = Geometry.Parse("M 0,0 L 12,0 L 12,12 L 0,12 Z");
        IdleTitle.Text = "Waiting for video"; IdleHint.Text = "Check LiveView and your goggles settings.";
    }

    private void StartReplay(string path)
    {
        try
        {
            replay = true;
            CreateSession(async message => { if (closing) return; await DisconnectAsync(); Status.Text = message; }).StartReplay(path);
        }
        catch (Exception error) { Status.Text = error.Message; AddLog(error.Message); }
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
        Status.Text = "Disconnected"; StatusDot.Fill = new SolidColorBrush(Color.FromArgb(96,255,255,255));
        ConnectLabel.Text = "Connect"; ConnectGlyph.Data = Geometry.Parse("M 7,0 L 0,8 L 5,8 L 3,15 L 11,6 L 6,6 Z");
        IdleTitle.Text = "Your view, on a bigger screen"; IdleHint.Text = "Connect your Goggles 3 via USB-C and click Connect.";
    }

    /// <summary>Ends the current session (saving a running recording) and clears the video, keeps the connect state.</summary>
    private async Task StopSessionAsync()
    {
        generation++; FfmpegVideoSession? active = session; session = null;
        if (recording) { recording = false; saving = true; RecordLabel.Text = "Saving…"; }
        if (active != null) await active.DisposeAsync();
        Video.Source = null; Idle.Visibility = Visibility.Visible;
        RecordButton.IsEnabled = false; Fps.Text = Resolution.Text = Bitrate.Text = "—";
        frames = 0; rate = 0;
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
        FlushLog();
        if (session == null) return;
        if (clock.Elapsed.TotalSeconds - lastFrame > 3)
        {
            Status.Text = "Waiting for video · check LiveView on the goggles";
            Video.Source = null; Idle.Visibility = Visibility.Visible;
            RecordButton.IsEnabled = recording && !saving; // Stop must stay usable when video drops
            Fps.Text = Resolution.Text = Bitrate.Text = "—";
            StatusDot.Fill = Brushes.Gray;
        }
        else if (Video.Source != null)
        {
            Fps.Text = frames.ToString(); Resolution.Text = "1920×1080";
            Bitrate.Text = replay || rate <= 0 ? "—" : $"{rate:F1} Mbps";
        }
        frames = 0;
    }

    private async void RecordClick(object sender, RoutedEventArgs e)
    {
        FfmpegVideoSession? active = session;
        if (active == null || saving) return;
        try
        {
            if (recording)
            {
                recording = false; saving = true; RecordButton.IsEnabled = false;
                RecordLabel.Text = "Saving…"; await active.StopRecordingAsync();
            }
            else
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),"Goggle 3 LiveView");
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory,$"Goggle3LiveView_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{Guid.NewGuid().ToString()[..4]}.mp4");
                currentRecordingPath = path; active.StartRecording(path); recording = true;
                RecordLabel.Text = "Stop"; RecordDot.Fill = Brushes.Tomato;
                RecordingText.Foreground = Brushes.Tomato; RecordingText.Text = "Recording · " + Path.GetFileName(path);
                RecordingNotice.Visibility = Visibility.Visible;
            }
        }
        catch (Exception error) { RecordingText.Text = error.Message; RecordingNotice.Visibility = Visibility.Visible; AddLog(error.Message); }
    }

    private void FullscreenClick(object sender, RoutedEventArgs e) => SetFullscreen(true);
    private void SetFullscreen(bool enable)
    {
        if (enable == fullscreen) return;
        fullscreen = enable;
        if (enable)
        {
            savedBounds = RestoreBounds; savedState = WindowState; savedTopmost = Topmost;
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(new WindowInteropHelper(this).Handle,2),ref info))
            { fullscreen = false; return; }
            Matrix transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            Point origin = transform.Transform(new Point(info.Monitor.Left,info.Monitor.Top));
            Point extent = transform.Transform(new Point(info.Monitor.Right,info.Monitor.Bottom));
            WindowChrome.SetWindowChrome(this,null); WindowState = WindowState.Normal; ResizeMode = ResizeMode.NoResize;
            Left = origin.X; Top = origin.Y; Width = extent.X-origin.X; Height = extent.Y-origin.Y; Topmost = true;
            CaptionRow.Height = new GridLength(0); CaptionControls.Visibility = Hud.Visibility = Visibility.Collapsed;
        }
        else
        {
            Topmost = savedTopmost; ResizeMode = ResizeMode.CanResize;
            WindowChrome.SetWindowChrome(this,new WindowChrome { CaptionHeight = 28, ResizeBorderThickness = new Thickness(6), GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(14) });
            Left = savedBounds.Left; Top = savedBounds.Top; Width = savedBounds.Width; Height = savedBounds.Height;
            WindowState = savedState; CaptionRow.Height = new GridLength(28); CaptionControls.Visibility = Hud.Visibility = Visibility.Visible;
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
        await logWrite; FlushLog(); await logWrite; // the last lines (why it stopped) must reach the file
        closed = true; Close();
    }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void AddLog(string text) => incoming.Enqueue(DateTime.Now.ToString("HH:mm:ss") + " " + text);

    private void FlushLog()
    {
        while (incoming.TryDequeue(out string? line))
        {
            if (log.Count >= 100) log.Dequeue();
            log.Enqueue(line); logDirty = true;
        }
        // File write off the UI thread; if the last write is still running, a later tick writes the newer lines.
        if (!logDirty || Interlocked.CompareExchange(ref writingLog, 1, 0) != 0) return;
        logDirty = false;
        string[] lines = [.. log];
        logWrite = Task.Run(() =>
        {
            try
            {
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Goggle 3 LiveView");
                Directory.CreateDirectory(directory);
                File.WriteAllLines(Path.Combine(directory, "diagnostic.txt"), lines);
            }
            catch (IOException) { /* Diagnostics must not interrupt the viewer. */ }
            catch (UnauthorizedAccessException) { }
            finally { Interlocked.Exchange(ref writingLog, 0); }
        });
    }
    private void TutorialClick(object sender, RoutedEventArgs e) => new TutorialWindow { Owner = this }.ShowDialog();

    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor,Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Auto)] [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
}
