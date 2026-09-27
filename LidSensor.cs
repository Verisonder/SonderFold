using Windows.Devices.Sensors;

namespace SonderFold;

/// <summary>
/// Event-driven accelerometer reader -> lid angle, filtered with a One Euro filter
/// (very smooth when the lid is still, very low lag when it moves fast).
/// Calibrated on a Latitude 7450: tilt = atan2(-Y, -Z), lid = 180 - tilt.
/// </summary>
public sealed class LidSensor
{
    private readonly Accelerometer? _acc;
    private readonly object _lock = new();
    private readonly OneEuroFilter _filter = new() { MinCutoff = 1.0, Beta = 0.08 };

    private double _rawLid = double.NaN, _tilt, _x, _y, _z, _g = 1;
    private double? _held;
    private double _gBase = double.NaN;
    private int _rejected;
    private int _count;
    private double _hz;
    private DateTime _hzWindow = DateTime.Now;

    public bool IsAvailable => _acc != null;

    /// <summary>Higher = follows fast lid movement with less lag.</summary>
    public double Beta { get => _filter.Beta; set => _filter.Beta = value; }

    /// <summary>Lower = steadier when the lid is still or moving slowly (One Euro min cutoff, Hz).</summary>
    public double Stability { get => _filter.MinCutoff; set => _filter.MinCutoff = Math.Max(0.001, value); }

    /// <summary>Changes smaller than this (degrees) are ignored — kills small wiggles.</summary>
    public double Deadzone { get; set; } = 0.5;

    /// <summary>
    /// Readings whose total acceleration differs from 1 g by more than this are ignored:
    /// they are caused by a hand pushing the lid or the lid bouncing, not by its angle.
    /// </summary>
    public double ShakeRejection { get; set; } = 0.12;

    public LidSensor()
    {
        _acc = Accelerometer.GetDefault();
        if (_acc == null) return;

        _acc.ReportInterval = Math.Max(_acc.MinimumReportInterval, 8u); // as fast as the hardware allows
        _acc.ReadingChanged += OnReadingChanged;

        var first = _acc.GetCurrentReading();
        if (first != null) Store(first);
    }

    private void OnReadingChanged(Accelerometer sender, AccelerometerReadingChangedEventArgs args) => Store(args.Reading);

    private void Store(AccelerometerReading r)
    {
        double g = Math.Sqrt(r.AccelerationX * r.AccelerationX + r.AccelerationY * r.AccelerationY + r.AccelerationZ * r.AccelerationZ);
        lock (_lock) _g = g;
        if (double.IsNaN(_gBase)) _gBase = g;
        // Compare with this sensor's own resting force (calibration differs per laptop).
        if (!double.IsNaN(_rawLid) && Math.Abs(g - _gBase) > ShakeRejection)
        {
            lock (_lock) _rejected++;
            _gBase += (g - _gBase) * 0.002; // very slow drift so a bad baseline can recover
            return; // shaken / pushed: keep the last good angle
        }
        _gBase += (g - _gBase) * 0.02;

        double tilt = Math.Atan2(-r.AccelerationY, -r.AccelerationZ) * 180.0 / Math.PI;
        if (tilt < -90) tilt += 360; // continuity near fully closed
        double lid = 180.0 - tilt;

        lock (_lock)
        {
            _x = r.AccelerationX; _y = r.AccelerationY; _z = r.AccelerationZ;
            _tilt = tilt;
            _rawLid = lid;

            _count++;
            var now = DateTime.Now;
            double elapsed = (now - _hzWindow).TotalSeconds;
            if (elapsed >= 1)
            {
                _hz = _count / elapsed;
                _count = 0;
                _hzWindow = now;
            }
        }
    }

    /// <summary>Call once per frame with the frame time in seconds.</summary>
    public LidReading? Read(double dt)
    {
        double raw, tilt, x, y, z, hz, g;
        int rejected;
        lock (_lock)
        {
            if (double.IsNaN(_rawLid)) return null;
            raw = _rawLid; tilt = _tilt; x = _x; y = _y; z = _z; hz = _hz; g = _g; rejected = _rejected;
        }

        double filtered = _filter.Filter(raw, dt);

        // Deadzone (backlash): the output only moves once the input has moved more than
        // Deadzone away from it, then follows it continuously — no jumps, no wiggle.
        if (_held is not double held) _held = filtered;
        else
        {
            double diff = filtered - held;
            if (Math.Abs(diff) > Deadzone) _held = filtered - Math.Sign(diff) * Deadzone;
        }

        return new LidReading(x, y, z, tilt, _held.Value, Math.Abs(x) > 0.35, hz, g, rejected);
    }
}

public readonly record struct LidReading(double X, double Y, double Z, double Tilt, double LidAngle, bool Sideways, double SensorHz, double G, int Rejected);

/// <summary>One Euro filter (Casiez et al.): adaptive low-pass, smooth at rest, fast in motion.</summary>
public sealed class OneEuroFilter
{
    public double MinCutoff { get; set; } = 1.0;
    public double Beta { get; set; } = 0.05;
    public double DCutoff { get; set; } = 1.0;

    private double? _x;
    private double _dx;

    public double Filter(double value, double dt)
    {
        if (_x is not double prev) { _x = value; return value; }

        double rawDx = (value - prev) / dt;
        _dx += Alpha(DCutoff, dt) * (rawDx - _dx);

        double cutoff = MinCutoff + Beta * Math.Abs(_dx);
        double x = prev + Alpha(cutoff, dt) * (value - prev);
        _x = x;
        return x;
    }

    private static double Alpha(double cutoff, double dt)
    {
        double tau = 1.0 / (2 * Math.PI * cutoff);
        return 1.0 / (1.0 + tau / dt);
    }
}
