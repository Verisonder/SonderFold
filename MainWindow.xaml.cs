using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SonderFold;

public partial class MainWindow : Window
{
    private readonly LidSensor _sensor = new();
    private readonly LiveScreenCapture _capture = new();
    private readonly OverlayWindow _overlay = new();
    private readonly FoldModel _model = new();
    private readonly DispatcherTimer _idleTimer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private bool _overlayShown;
    private bool _renderHooked;
    private double _lastT;
    private double? _previewStart;
    private TimeSpan _lastRenderingTime;

    private const double PreviewSeconds = 5;

    public MainWindow()
    {
        InitializeComponent();

        // Always fit the screen: never taller than the usable screen area, and scroll inside.
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 60);
        SourceInitialized += (_, _) => UseDarkTitleBar();

        _overlay.PlaceOnPrimaryScreen();
        _overlay.SetSources(_capture);

        ResponseSlider.ValueChanged += (_, _) => _sensor.Beta = ResponseSlider.Value;
        PreviewButton.Click += (_, _) => _previewStart = _clock.Elapsed.TotalSeconds;
        _sensor.Beta = ResponseSlider.Value;
        StabilitySlider.ValueChanged += (_, _) => _sensor.Stability = StabilitySlider.Value;
        DeadzoneSlider.ValueChanged += (_, _) => _sensor.Deadzone = DeadzoneSlider.Value;
        ShakeSlider.ValueChanged += (_, _) => _sensor.ShakeRejection = ShakeSlider.Value;
        _sensor.Stability = StabilitySlider.Value;
        _sensor.Deadzone = DeadzoneSlider.Value;
        _sensor.ShakeRejection = ShakeSlider.Value;
        CaptureDefaults();
        ResetButton.Click += (_, _) => ResetDefaults();

        _idleTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _idleTimer.Tick += (_, _) => Update();

        Loaded += (_, _) =>
        {
            StatusText.Text = _sensor.IsAvailable
                ? "Accelerometer found. Move the lid to see the effect."
                : "No accelerometer found. Only the preview will work.";
            _idleTimer.Start();
        };

        Closed += (_, _) =>
        {
            _idleTimer.Stop();
            if (_renderHooked) CompositionTarget.Rendering -= OnRendering;
            _capture.Stop();
            _overlay.Close();
        };
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs re)
        {
            if (re.RenderingTime == _lastRenderingTime) return;
            _lastRenderingTime = re.RenderingTime;
        }
        Update();
    }

    private void Update()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Clamp(now - _lastT, 0.001, 0.1);
        _lastT = now;

        _model.ClearAngle = ClearAngleSlider.Value;
        _model.StillDelay = StillSlider.Value;
        _model.ClearWhenStill = ClearStillBox.IsChecked == true;
        _model.FadeStart = FadeStartSlider.Value;
        _model.FadeEnd = FadeEndSlider.Value;

        LidReading? reading = _sensor.Read(dt);

        if (_previewStart is double ps)
        {
            double t = (now - ps) / PreviewSeconds;
            if (t >= 1)
            {
                _previewStart = null;
                _model.UpdateForced(FoldTarget.Clear, now, dt);
            }
            else
            {
                // From just above the start angle down past "fully black" and back up.
                double clear = ClearAngleSlider.Value;
                double angle = clear + 8 - Math.Sin(t * Math.PI) * (clear + 8 - Math.Min(FadeEndSlider.Value, FadeStartSlider.Value) + 2);
                _model.UpdateForced(FoldModel.At(angle, clear, FadeStartSlider.Value, FadeEndSlider.Value), now, dt);
            }
        }
        else if (EnabledBox.IsChecked != true)
        {
            _model.Update(null, now, dt);
        }
        else if (reading is LidReading r)
        {
            if (r.Sideways) _model.UpdateForced(FoldTarget.Clear, now, dt);
            else _model.Update(r.LidAngle, now, dt);
        }
        else
        {
            _model.Update(null, now, dt);
        }

        if (reading is LidReading rr)
        {
            ReadingText.Text =
                $"lid {rr.LidAngle,5:F1}°  from {_model.Reference,3:F0}°  tilt {_model.Tilt * 180 / Math.PI,3:F0}°  {(_model.IsStill ? "still " : "moving")}  {rr.SensorHz,3:F0} Hz\n" +
                $"progress {_model.Progress,4:F2}  blur {_model.Defocus,4:F2}  light {_model.Visibility,4:F2}  force {rr.G,4:F2} g  bumps {rr.Rejected}" +
                (rr.Sideways ? "\n(tilted sideways, ignored)" : "");
        }

        bool visible = _model.IsVisible;
        if (!_overlayShown && visible)
        {
            _capture.Start(); // first frame is captured before the overlay covers the screen
            RenderOverlay();
            _overlay.Show();
            _overlayShown = true;
        }
        else if (_overlayShown && !visible)
        {
            _overlay.Hide();
            _capture.Stop();
            _overlayShown = false;
        }

        if (_overlayShown)
        {
            _capture.PushToBitmaps();
            RenderOverlay();
        }

        SetActiveMode(_overlayShown || _previewStart != null);
    }

    private void RenderOverlay() =>
        _overlay.Render(_model, new RenderSettings(
            EffectBox.SelectedIndex, SoftnessSlider.Value, ShadowSlider.Value, PerspectiveSlider.Value,
            ViewerDistanceSlider.Value, EyeHeightSlider.Value, TiltStrengthSlider.Value, MaxTiltSlider.Value,
            HStrengthSlider.Value, VStrengthSlider.Value,
            FixedPlaneBox.IsChecked == true ? PlaneAngleSlider.Value : null));

    private readonly Dictionary<System.Windows.Controls.Slider, (double min, double max, double value)> _defaults = new();

    private IEnumerable<System.Windows.Controls.Slider> AllSliders() => new[]
    {
        ClearAngleSlider, StillSlider, FadeStartSlider, FadeEndSlider, ResponseSlider,
        DeadzoneSlider, StabilitySlider, ShakeSlider,
        SoftnessSlider, ShadowSlider, PerspectiveSlider,
        ViewerDistanceSlider, EyeHeightSlider, TiltStrengthSlider, MaxTiltSlider,
        HStrengthSlider, VStrengthSlider, PlaneAngleSlider
    };

    private void CaptureDefaults()
    {
        foreach (var s in AllSliders()) _defaults[s] = (s.Minimum, s.Maximum, s.Value);
    }

    private void ResetDefaults()
    {
        foreach (var (s, d) in _defaults)
        {
            s.Minimum = Math.Min(s.Minimum, d.min);
            s.Maximum = Math.Max(s.Maximum, d.max);
            s.Value = d.value;
            s.Minimum = d.min;
            s.Maximum = d.max;
        }
        FixedPlaneBox.IsChecked = true;
        ClearStillBox.IsChecked = true;
        EffectBox.SelectedIndex = 0;
    }

    private void SetActiveMode(bool active)
    {
        if (active && !_renderHooked)
        {
            _idleTimer.Stop();
            CompositionTarget.Rendering += OnRendering;
            _renderHooked = true;
        }
        else if (!active && _renderHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderHooked = false;
            _idleTimer.Start();
        }
    }

    /// <summary>
    /// Number boxes have no upper or lower limit: typing a value outside the slider's
    /// range stretches the slider to fit it. Enter or leaving the box applies it.
    /// </summary>
    private void OnValueBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        Commit(box);
        box.SelectAll();
    }

    private void OnValueBoxLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) Commit(box);
    }

    private static void Commit(TextBox box)
    {
        if (box.Parent is not Grid grid || grid.Children.OfType<Slider>().FirstOrDefault() is not Slider slider) return;

        string text = box.Text.Trim().Replace(',', '.');
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value) || !double.IsFinite(value))
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget(); // restore the current value
            return;
        }

        if (value > slider.Maximum) slider.Maximum = value;
        if (value < slider.Minimum) slider.Minimum = value;
        slider.Value = value;
        box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }


    private void UseDarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int on = 1;
        DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
