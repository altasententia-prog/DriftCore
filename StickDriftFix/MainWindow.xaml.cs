using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DriftCore.Core;
using DriftCore.Input;
using DriftCore.Output;

namespace DriftCore;

// ═══════════════════════════════════════════════════════════════════════════
// Thread-safe snapshot passed from the processing thread to the UI thread.
// ═══════════════════════════════════════════════════════════════════════════
internal struct DisplaySnapshot
{
    public Vector2 RawLeft;
    public Vector2 RawRight;
    public Vector2 CorrLeft;
    public Vector2 CorrRight;
    public Vector2 LearnedCenterLeft;
    public Vector2 LearnedCenterRight;
    public float   LeftTrigger;
    public float   RightTrigger;
    public bool    IsConnected;
    public bool    JustDisconnected;
    public bool    JustConnected;
    public int     ControllerSlot;
    public uint    PacketNumber;
    public int     TotalSpikes;
    public int     PollHz;
}

public partial class MainWindow : Window
{
    // ═══════════════════════════════════════════════════════════════════
    // Core subsystems
    // ═══════════════════════════════════════════════════════════════════
    private readonly XInputReader             _input       = new();
    private readonly DriftCoreEngine          _leftEngine  = new();
    private readonly DriftCoreEngine          _rightEngine = new();
    private readonly VirtualControllerManager _virtual     = new();

    // ═══════════════════════════════════════════════════════════════════
    // Processing thread — 125Hz, high priority
    // ═══════════════════════════════════════════════════════════════════
    private Thread?       _processingThread;
    private volatile bool _threadRunning = false;
    private const int     TargetHz       = 125;
    private const double  TargetDt       = 1.0 / TargetHz;
    private const long    TargetTicks    = (long)(Stopwatch.Frequency * TargetDt);

    // ═══════════════════════════════════════════════════════════════════
    // Thread-safe display snapshot
    // ═══════════════════════════════════════════════════════════════════
    private readonly object _snapshotLock = new();
    private DisplaySnapshot _snapshot     = new();

    // ═══════════════════════════════════════════════════════════════════
    // UI timer — 60Hz display refresh only
    // ═══════════════════════════════════════════════════════════════════
    private readonly DispatcherTimer _uiTimer = new(DispatcherPriority.Render);

    // ═══════════════════════════════════════════════════════════════════
    // Settings & state
    // ═══════════════════════════════════════════════════════════════════
    private AppSettings   _settings           = new();
    private volatile bool _correctionEnabled  = true;
    private bool          _vigemAvailable     = false;

    // Canvas geometry
    private double _leftCanvasR  = 100.0;
    private double _leftCX       = 110.0;
    private double _leftCY       = 110.0;
    private double _rightCanvasR = 100.0;
    private double _rightCX      = 110.0;
    private double _rightCY      = 110.0;

    private const double DotHalfRaw  = 6.0;
    private const double DotHalfCorr = 7.0;
    private const double CanvasPad   = 14.0;

    // ═══════════════════════════════════════════════════════════════════
    // Initialisation
    // ═══════════════════════════════════════════════════════════════════
    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _settings = SettingsManager.Load();
        ApplySettings(_settings);

        if (!double.IsNaN(_settings.WindowLeft) && !double.IsNaN(_settings.WindowTop))
        {
            Left   = _settings.WindowLeft;
            Top    = _settings.WindowTop;
            Width  = _settings.WindowWidth;
            Height = _settings.WindowHeight;
        }

        _vigemAvailable = _virtual.Connect();
        if (!_vigemAvailable)
        {
            LblVigemWarning.Visibility = Visibility.Visible;
            LblVirtual.Text            = "No ViGEmBus";
            LblVirtual.Foreground      = (Brush)FindResource("AmberBrush");
        }
        else
        {
            LblVirtual.Text       = "Active";
            LblVirtual.Foreground = (Brush)FindResource("GreenBrush");
        }

        // Start 125Hz background processing thread
        _threadRunning    = true;
        _processingThread = new Thread(ProcessingLoop)
        {
            IsBackground = true,
            Priority     = ThreadPriority.Highest,
            Name         = "DriftCore.ProcessingThread"
        };
        _processingThread.Start();

        // Start 60Hz UI refresh
        _uiTimer.Interval = TimeSpan.FromMilliseconds(16.67);
        _uiTimer.Tick    += UiTimer_Tick;
        _uiTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _threadRunning = false;
        _processingThread?.Join(500);
        _uiTimer.Stop();

        _settings.WindowLeft   = Left;
        _settings.WindowTop    = Top;
        _settings.WindowWidth  = Width;
        _settings.WindowHeight = Height;
        SettingsManager.Save(_settings);

        _virtual.Dispose();
        _input.Dispose();
    }

    // ═══════════════════════════════════════════════════════════════════
    // 125Hz processing loop — never touches UI elements
    // ═══════════════════════════════════════════════════════════════════
    private void ProcessingLoop()
    {
        // Raise Windows scheduler resolution to 1ms
        NativeMethods.TimeBeginPeriod(1);

        var  sw             = Stopwatch.StartNew();
        long lastTicks      = sw.ElapsedTicks;
        long reconnectTimer = 0;
        int  frameCount     = 0;
        long hzAccum        = 0;
        int  measuredHz     = 0;
        int  totalSpikes    = 0;
        bool connected      = _input.TryConnect();

        if (connected)
        {
            WriteSnapshot(s =>
            {
                s.IsConnected    = true;
                s.JustConnected  = true;
                s.ControllerSlot = _input.UserIndex;
                return s;
            });
        }

        try
        {
            while (_threadRunning)
            {
                // ── Precise 125Hz timing ───────────────────────────────────
                long nowTicks = sw.ElapsedTicks;
                long elapsed  = nowTicks - lastTicks;

                if (elapsed < TargetTicks)
                {
                    long remainMs = ((TargetTicks - elapsed) * 1000L / Stopwatch.Frequency) - 1;
                    if (remainMs > 0) Thread.Sleep((int)remainMs);
                    while (sw.ElapsedTicks - lastTicks < TargetTicks) { /* spin */ }
                    nowTicks = sw.ElapsedTicks;
                }

                double dt  = Math.Clamp((double)(nowTicks - lastTicks) / Stopwatch.Frequency, 0.0001, 0.1);
                lastTicks  = nowTicks;

                // ── Hz measurement ─────────────────────────────────────────
                frameCount++;
                hzAccum += (long)(dt * Stopwatch.Frequency);
                if (hzAccum >= Stopwatch.Frequency)
                {
                    measuredHz  = frameCount;
                    frameCount  = 0;
                    hzAccum     = 0;
                }

                // ── Reconnect when disconnected ────────────────────────────
                if (!connected)
                {
                    reconnectTimer += (long)(dt * Stopwatch.Frequency);
                    if (reconnectTimer >= Stopwatch.Frequency)
                    {
                        reconnectTimer = 0;
                        connected = _input.TryConnect();
                        if (connected)
                        {
                            _leftEngine.Reset();
                            _rightEngine.Reset();
                            int slot = _input.UserIndex;
                            WriteSnapshot(s =>
                            {
                                s.IsConnected    = true;
                                s.JustConnected  = true;
                                s.ControllerSlot = slot;
                                s.PollHz         = measuredHz;
                                return s;
                            });
                        }
                    }
                    WriteSnapshot(s => { s.IsConnected = false; s.PollHz = measuredHz; return s; });
                    continue;
                }

                // ── Poll ───────────────────────────────────────────────────
                if (!_input.Poll())
                {
                    connected = false;
                    WriteSnapshot(s => { s.IsConnected = false; s.JustDisconnected = true; return s; });
                    continue;
                }

                // ── DriftCore ──────────────────────────────────────────────
                Vector2 rawL = _input.LeftStick;
                Vector2 rawR = _input.RightStick;

                _leftEngine.IsEnabled  = _correctionEnabled;
                _rightEngine.IsEnabled = _correctionEnabled;

                Vector2 corrL = _leftEngine.Process(rawL,  (float)dt);
                Vector2 corrR = _rightEngine.Process(rawR, (float)dt);

                if (_leftEngine.IsSpikeActive || _rightEngine.IsSpikeActive)
                    totalSpikes++;

                // ── ViGEm output ───────────────────────────────────────────
                if (_vigemAvailable)
                    _virtual.SendState(corrL, corrR, _input.LeftTrigger, _input.RightTrigger, _input.Buttons);

                // ── Write snapshot ─────────────────────────────────────────
                float  lt  = _input.LeftTrigger;
                float  rt  = _input.RightTrigger;
                uint   pkt = _input.LastState.PacketNumber;
                Vector2 lcL = _leftEngine.LearnedCenter;
                Vector2 lcR = _rightEngine.LearnedCenter;
                int    sp  = totalSpikes;
                int    hz  = measuredHz;

                WriteSnapshot(s =>
                {
                    s.RawLeft            = rawL;
                    s.RawRight           = rawR;
                    s.CorrLeft           = corrL;
                    s.CorrRight          = corrR;
                    s.LearnedCenterLeft  = lcL;
                    s.LearnedCenterRight = lcR;
                    s.LeftTrigger        = lt;
                    s.RightTrigger       = rt;
                    s.IsConnected        = true;
                    s.JustDisconnected   = false;
                    s.PacketNumber       = pkt;
                    s.TotalSpikes        = sp;
                    s.PollHz             = hz;
                    return s;
                });
            }
        }
        finally
        {
            NativeMethods.TimeEndPeriod(1);
        }
    }

    private void WriteSnapshot(Func<DisplaySnapshot, DisplaySnapshot> update)
    {
        lock (_snapshotLock) { _snapshot = update(_snapshot); }
    }

    private DisplaySnapshot ReadSnapshot()
    {
        lock (_snapshotLock) { return _snapshot; }
    }

    // ═══════════════════════════════════════════════════════════════════
    // 60Hz UI refresh — reads snapshot, never blocks processing thread
    // ═══════════════════════════════════════════════════════════════════
    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        DisplaySnapshot snap = ReadSnapshot();

        if (snap.JustConnected)
        {
            LblDevice.Text            = $"Controller {snap.ControllerSlot + 1}";
            LblSlot.Text              = $"Slot {snap.ControllerSlot + 1}";
            LblStatus.Text            = "Connected";
            LblStatus.Foreground      = (Brush)FindResource("GreenBrush");
            StatusIndicator.Fill      = (Brush)FindResource("GreenBrush");
            TitleControllerLabel.Text = $"Xbox Controller — Slot {snap.ControllerSlot + 1}";
            WriteSnapshot(s => { s.JustConnected = false; return s; });
        }

        if (snap.JustDisconnected)
        {
            LblStatus.Text            = "Disconnected";
            LblStatus.Foreground      = (Brush)FindResource("RedBrush");
            StatusIndicator.Fill      = (Brush)FindResource("RedBrush");
            TitleControllerLabel.Text = "Controller disconnected";
            WriteSnapshot(s => { s.JustDisconnected = false; return s; });
            DrawIdleState();
            LblPollRate.Text = $"Poll: {snap.PollHz} Hz";
            return;
        }

        if (!snap.IsConnected)
        {
            DrawIdleState();
            LblPollRate.Text = $"Poll: {snap.PollHz} Hz";
            return;
        }

        UpdateStickVisualizer(
            CanvasLeft,
            LeftBgCircle, LeftHLine, LeftVLine,
            LeftDeadzoneCircle, LeftCenterH, LeftCenterV,
            LeftRawDot, LeftCorrectedDot,
            snap.RawLeft, snap.CorrLeft,
            _leftCX, _leftCY, _leftCanvasR,
            _leftEngine.DeadzoneRadius);

        UpdateStickVisualizer(
            CanvasRight,
            RightBgCircle, RightHLine, RightVLine,
            RightDeadzoneCircle, RightCenterH, RightCenterV,
            RightRawDot, RightCorrectedDot,
            snap.RawRight, snap.CorrRight,
            _rightCX, _rightCY, _rightCanvasR,
            _rightEngine.DeadzoneRadius);

        LblLeftRaw.Text  = $"{snap.RawLeft.X:+0.000;-0.000}, {snap.RawLeft.Y:+0.000;-0.000}";
        LblLeftOut.Text  = $"{snap.CorrLeft.X:+0.000;-0.000}, {snap.CorrLeft.Y:+0.000;-0.000}";
        LblRightRaw.Text = $"{snap.RawRight.X:+0.000;-0.000}, {snap.RawRight.Y:+0.000;-0.000}";
        LblRightOut.Text = $"{snap.CorrRight.X:+0.000;-0.000}, {snap.CorrRight.Y:+0.000;-0.000}";

        LblLeftCenter.Text  = $"{snap.LearnedCenterLeft.X:+0.00;-0.00}, {snap.LearnedCenterLeft.Y:+0.00;-0.00}";
        LblRightCenter.Text = $"{snap.LearnedCenterRight.X:+0.00;-0.00}, {snap.LearnedCenterRight.Y:+0.00;-0.00}";
        LblSpikes.Text      = snap.TotalSpikes.ToString("N0");
        LblLT.Text          = snap.LeftTrigger.ToString("0.00");
        LblRT.Text          = snap.RightTrigger.ToString("0.00");

        LblPollRate.Text   = $"Poll: {snap.PollHz} Hz";
        LblPacket.Text     = $"Packet: {snap.PacketNumber}";
        LblSpikeCount.Text = $"Spikes suppressed: {snap.TotalSpikes}";
    }

    private void DrawIdleState()
    {
        LblLeftRaw.Text  = "—";
        LblLeftOut.Text  = "—";
        LblRightRaw.Text = "—";
        LblRightOut.Text = "—";
    }

    // ═══════════════════════════════════════════════════════════════════
    // Canvas drawing
    // ═══════════════════════════════════════════════════════════════════
    private void UpdateStickVisualizer(
        System.Windows.Controls.Canvas canvas,
        Ellipse bgCircle, Line hLine, Line vLine,
        Ellipse dzCircle, Line centerH, Line centerV,
        Ellipse rawDot, Ellipse corrDot,
        Vector2 raw, Vector2 corr,
        double cx, double cy, double r,
        float deadzoneRadius)
    {
        double diameter = r * 2;
        bgCircle.Width  = diameter;
        bgCircle.Height = diameter;
        System.Windows.Controls.Canvas.SetLeft(bgCircle, cx - r);
        System.Windows.Controls.Canvas.SetTop(bgCircle,  cy - r);

        hLine.X1 = cx - r; hLine.Y1 = cy; hLine.X2 = cx + r; hLine.Y2 = cy;
        vLine.X1 = cx; vLine.Y1 = cy - r; vLine.X2 = cx; vLine.Y2 = cy + r;

        double ch = 6.0;
        centerH.X1 = cx - ch; centerH.Y1 = cy; centerH.X2 = cx + ch; centerH.Y2 = cy;
        centerV.X1 = cx; centerV.Y1 = cy - ch; centerV.X2 = cx; centerV.Y2 = cy + ch;

        double dzPixels = deadzoneRadius * r;
        dzCircle.Width  = dzPixels * 2;
        dzCircle.Height = dzPixels * 2;
        System.Windows.Controls.Canvas.SetLeft(dzCircle, cx - dzPixels);
        System.Windows.Controls.Canvas.SetTop(dzCircle,  cy - dzPixels);

        System.Windows.Controls.Canvas.SetLeft(rawDot, cx + raw.X * r - DotHalfRaw);
        System.Windows.Controls.Canvas.SetTop(rawDot,  cy - raw.Y * r - DotHalfRaw);

        System.Windows.Controls.Canvas.SetLeft(corrDot, cx + corr.X * r - DotHalfCorr);
        System.Windows.Controls.Canvas.SetTop(corrDot,  cy - corr.Y * r - DotHalfCorr);
    }

    private void CanvasLeft_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _leftCX      = e.NewSize.Width  / 2.0;
        _leftCY      = e.NewSize.Height / 2.0;
        _leftCanvasR = Math.Min(e.NewSize.Width, e.NewSize.Height) / 2.0 - CanvasPad;
    }

    private void CanvasRight_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        _rightCX      = e.NewSize.Width  / 2.0;
        _rightCY      = e.NewSize.Height / 2.0;
        _rightCanvasR = Math.Min(e.NewSize.Width, e.NewSize.Height) / 2.0 - CanvasPad;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Button handlers
    // ═══════════════════════════════════════════════════════════════════
    private void BtnToggle_Click(object sender, RoutedEventArgs e)
    {
        _correctionEnabled = !_correctionEnabled;
        if (_correctionEnabled)
        {
            BtnToggle.Content            = "⬤  CORRECTION ON";
            BtnToggle.Background         = (Brush)FindResource("AccentBrush");
            LblCorrectionMode.Text       = "Mode: ENABLED";
            LblCorrectionMode.Foreground = (Brush)FindResource("GreenBrush");
        }
        else
        {
            BtnToggle.Content            = "◯  CORRECTION OFF";
            BtnToggle.Background         = new SolidColorBrush(Color.FromRgb(0x3A, 0x1A, 0x1A));
            LblCorrectionMode.Text       = "Mode: BYPASSED";
            LblCorrectionMode.Foreground = (Brush)FindResource("AmberBrush");
        }
        PersistCurrentSettings();
    }

    private void BtnCalibrate_Click(object sender, RoutedEventArgs e)
    {
        if (!_input.IsConnected) return;
        _leftEngine.CalibrateNow(_input.LeftStick);
        _rightEngine.CalibrateNow(_input.RightStick);
    }

    private void BtnReconnect_Click(object sender, RoutedEventArgs e)
    {
        if (_input.TryConnect())
        {
            _leftEngine.Reset();
            _rightEngine.Reset();
            LblDevice.Text            = _input.ControllerLabel;
            LblSlot.Text              = $"Slot {_input.UserIndex + 1}";
            LblStatus.Text            = "Connected";
            LblStatus.Foreground      = (Brush)FindResource("GreenBrush");
            StatusIndicator.Fill      = (Brush)FindResource("GreenBrush");
            TitleControllerLabel.Text = $"Xbox Controller — Slot {_input.UserIndex + 1}";
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    // Settings
    // ═══════════════════════════════════════════════════════════════════
    private void ApplySettings(AppSettings s)
    {
        SliderDeadzone.Value   = s.DeadzoneRadius;
        SliderHysteresis.Value = s.HysteresisMargin;
        SliderSmoothing.Value  = s.SmoothingFactor;
        SliderLearning.Value   = s.LearningRate;
        SliderSpike.Value      = s.SpikeThreshold;

        _correctionEnabled = s.CorrectionEnabled;
        if (!_correctionEnabled)
        {
            BtnToggle.Content            = "◯  CORRECTION OFF";
            BtnToggle.Background         = new SolidColorBrush(Color.FromRgb(0x3A, 0x1A, 0x1A));
            LblCorrectionMode.Text       = "Mode: BYPASSED";
            LblCorrectionMode.Foreground = (Brush)FindResource("AmberBrush");
        }
    }

    private void PersistCurrentSettings()
    {
        _settings.DeadzoneRadius    = _leftEngine.DeadzoneRadius;
        _settings.HysteresisMargin  = _leftEngine.HysteresisMargin;
        _settings.SmoothingFactor   = _leftEngine.SmoothingFactor;
        _settings.LearningRate      = _leftEngine.LearningRate;
        _settings.SpikeThreshold    = _leftEngine.SpikeThreshold;
        _settings.CorrectionEnabled = _correctionEnabled;
        SettingsManager.Save(_settings);
    }

    private void SliderDeadzone_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        float v = (float)e.NewValue;
        _leftEngine.DeadzoneRadius  = v;
        _rightEngine.DeadzoneRadius = v;
        LblDeadzoneVal.Text = v.ToString("0.00");
        PersistCurrentSettings();
    }

    private void SliderHysteresis_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        float v = (float)e.NewValue;
        _leftEngine.HysteresisMargin  = v;
        _rightEngine.HysteresisMargin = v;
        LblHysteresisVal.Text = v.ToString("0.000");
        PersistCurrentSettings();
    }

    private void SliderSmoothing_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        float v = (float)e.NewValue;
        _leftEngine.SmoothingFactor  = v;
        _rightEngine.SmoothingFactor = v;
        LblSmoothingVal.Text = v.ToString("0.00");
        PersistCurrentSettings();
    }

    private void SliderLearning_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        float v = (float)e.NewValue;
        _leftEngine.LearningRate  = v;
        _rightEngine.LearningRate = v;
        LblLearningVal.Text = v.ToString("0.0000");
        PersistCurrentSettings();
    }

    private void SliderSpike_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        float v = (float)e.NewValue;
        _leftEngine.SpikeThreshold  = v;
        _rightEngine.SpikeThreshold = v;
        LblSpikeVal.Text = v.ToString("0.00");
        PersistCurrentSettings();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Window chrome
    // ═══════════════════════════════════════════════════════════════════
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void BtnClose_Click(object sender, RoutedEventArgs e)
        => Close();
}

// ═══════════════════════════════════════════════════════════════════════════
// Windows multimedia timer — raises scheduler resolution to 1ms
// Same technique used by games, DAWs, and professional input tools.
// ═══════════════════════════════════════════════════════════════════════════
internal static class NativeMethods
{
    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    public static extern uint TimeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    public static extern uint TimeEndPeriod(uint uPeriod);
}
