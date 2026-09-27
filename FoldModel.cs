namespace SonderFold;

public readonly record struct FoldTarget(double Progress, double Defocus, double Tilt, double Reference, double Visibility = 1)
{
    public static readonly FoldTarget Clear = new(0, 0, 0, 105, 1);
    public bool IsClear => Progress <= 0 && Defocus <= 0 && Tilt <= 0;
}

/// <summary>
/// Lid angle -> visual state, ported from Mac Duo's FoldCore (MIT, Dhananjay Bhosale).
/// - The effect starts from the angle where the lid last RESTED (capped by ClearAngle).
/// - Holding the lid still clears the effect and re-anchors.
/// - Geometry (progress), physical tilt, and optical blur (defocus) follow separate curves.
/// </summary>
public sealed class FoldModel
{
    public double ClearAngle { get; set; } = 105;
    public double StillDelay { get; set; } = 2;
    public bool ClearWhenStill { get; set; } = true;
    /// <summary>Lid angle (°) where the picture starts fading to black.</summary>
    public double FadeStart { get; set; } = 15;
    /// <summary>Lid angle (°) where the picture is fully black.</summary>
    public double FadeEnd { get; set; } = 5;

    public double Progress { get; private set; }
    public double Defocus { get; private set; }
    /// <summary>Physical rotation away from the resting plane, in radians.</summary>
    public double Tilt { get; private set; }
    /// <summary>Resting angle (degrees) the desktop plane is anchored to.</summary>
    public double ReferenceAngle { get; private set; } = 105;
    /// <summary>1 = fully visible, 0 = black. Driven only by the lid angle (FadeStart/FadeEnd).</summary>
    public double Visibility { get; private set; } = 1;

    public bool IsStill => _isStill;
    public bool IsVisible => Progress > 0.0005 || Defocus > 0.0005 || Tilt > 0.0005 || Visibility < 0.9995;

    private const double ClearDuration = 0.6;
    private const double Tau = 0.045;
    private const double TiltTau = 0.015;

    private double? _anchor;
    private double _stationarySince;
    private double? _lastReadingAt;
    private bool _isStill;

    private double? _resting;
    private bool _wasStill;

    private double? _clearStart;
    private FoldTarget _clearFrom;

    public double Reference
    {
        get
        {
            double setting = Math.Max(1, ClearAngle);
            return _resting is double r ? Math.Max(1, Math.Min(r, setting)) : setting;
        }
    }

    public void Update(double? angle, double now, double dt)
    {
        FoldTarget target;
        if (angle is not double a)
        {
            ResetMotion();
            target = FoldTarget.Clear;
        }
        else
        {
            ObserveStillness(a, now);
            if (ClearWhenStill)
            {
                if (_isStill && !_wasStill) _resting = a;
                _wasStill = _isStill;
            }
            else
            {
                _resting = null;
                _wasStill = false;
            }
            target = (_isStill && ClearWhenStill) ? FoldTarget.Clear : At(a, Reference, FadeStart, FadeEnd);
        }
        Animate(target, now, dt);
    }

    public void UpdateForced(FoldTarget target, double now, double dt) => Animate(target, now, dt);

    public static FoldTarget At(double angle, double reference, double fadeStart, double fadeEnd)
    {
        reference = Math.Max(1, reference);
        double delta = Math.Max(0, reference - angle);
        if (delta <= 0) return FoldTarget.Clear with { Reference = reference };

        double hi = Math.Max(fadeStart, fadeEnd), lo = Math.Min(fadeStart, fadeEnd);
        double visibility = Ease((angle - lo) / Math.Max(0.1, hi - lo));

        double span = Math.Max(20, reference - 5);
        double p = Ease(delta / span);
        double d = 0.16 * Ease(delta / 15) * Ease(delta / 6)
                 + 0.84 * Ease((delta - 15) / Math.Max(10, span - 15));
        double tilt = Math.Min(85, delta) * Math.PI / 180;
        return new FoldTarget(p, Math.Min(1, d), tilt, reference, visibility);
    }

    private void Animate(FoldTarget target, double now, double dt)
    {
        if (_clearStart is double cs)
        {
            double t = Math.Clamp((now - cs) / ClearDuration, 0, 1);
            double remaining = 1 - Ease(t);
            Progress = _clearFrom.Progress * remaining;
            Defocus = _clearFrom.Defocus * remaining;
            Tilt = _clearFrom.Tilt * remaining;
            Visibility = 1 - (1 - _clearFrom.Visibility) * remaining;
            if (t >= 1) { Progress = 0; Defocus = 0; Tilt = 0; Visibility = 1; _clearStart = null; }
            if (!target.IsClear) _clearStart = null; // retarget from current pixels
            return;
        }

        if (target.IsClear)
        {
            if (IsVisible)
            {
                _clearFrom = new FoldTarget(Progress, Defocus, Tilt, ReferenceAngle, Visibility);
                _clearStart = now;
            }
            else
            {
                Progress = 0; Defocus = 0; Tilt = 0; Visibility = 1;
            }
            return;
        }

        double mix = 1 - Math.Exp(-dt / Tau);
        double tiltMix = 1 - Math.Exp(-dt / TiltTau);

        // Seed a new plane while invisible; otherwise glide the reference without jumping.
        if (!IsVisible) ReferenceAngle = target.Reference;
        else ReferenceAngle += (target.Reference - ReferenceAngle) * tiltMix;

        Progress += (target.Progress - Progress) * mix;
        Defocus += (target.Defocus - Defocus) * mix;
        Tilt += (target.Tilt - Tilt) * tiltMix;
        Visibility += (target.Visibility - Visibility) * tiltMix;
    }

    private void ObserveStillness(double angle, double now)
    {
        if (_lastReadingAt is double prev && (now < prev || now - prev > 1))
            ResetStillness();
        _lastReadingAt = now;

        double interval = Math.Max(0.05, StillDelay);
        if (_anchor is null || Math.Abs(angle - _anchor.Value) >= 2)
        {
            _anchor = angle;
            _stationarySince = now;
            _isStill = false;
        }
        else if (now - _stationarySince >= interval)
        {
            _isStill = true;
        }
    }

    private void ResetStillness()
    {
        _anchor = null;
        _lastReadingAt = null;
        _isStill = false;
    }

    private void ResetMotion()
    {
        ResetStillness();
        _resting = null;
        _wasStill = false;
    }

    public static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }
}
