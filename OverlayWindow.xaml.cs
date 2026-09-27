using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace SonderFold;

/// <summary>Fullscreen, click-through window that shows the live desktop through the Duo shader.</summary>
public partial class OverlayWindow : Window
{
    private readonly DuoEffect _effect = new();

    public OverlayWindow()
    {
        InitializeComponent();
        Desktop.Effect = _effect;
        SourceInitialized += (_, _) => ConfigureWindow();
    }

    public void PlaceOnPrimaryScreen()
    {
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        _effect.Aspect = Width / Math.Max(1, Height);
    }

    internal void SetSources(LiveScreenCapture capture)
    {
        Desktop.Source = capture.Bitmap;
        _effect.Blur1 = new ImageBrush(capture.Level1) { Stretch = Stretch.Fill };
        _effect.Blur2 = new ImageBrush(capture.Level2) { Stretch = Stretch.Fill };
        _effect.Blur3 = new ImageBrush(capture.Level3) { Stretch = Stretch.Fill };
        _effect.Sigma1 = capture.Sigma1;
        _effect.Sigma2 = capture.Sigma2;
        _effect.Sigma3 = capture.Sigma3;
        _effect.PixelH = 1.0 / Math.Max(1, capture.Height);
    }

    public void Render(FoldModel model, RenderSettings r)
    {
        _effect.Mode = r.Mode;
        _effect.Progress = model.Progress;
        _effect.Defocus = model.Defocus;
        _effect.Visibility = model.Visibility;
        _effect.Tilt = Math.Min(model.Tilt * r.TiltStrength, r.MaxTiltDeg * Math.PI / 180);
        _effect.ReferenceAngle = r.FixedPlaneAngle ?? model.ReferenceAngle;
        _effect.Softness = r.Softness;
        _effect.Perspective = r.DuoSwell;
        _effect.Shadow = r.Shadow;
        _effect.ViewerDistance = r.ViewerDistance;
        _effect.EyeHeight = r.EyeHeight;
        _effect.HStrength = r.HStrength;
        _effect.VStrength = r.VStrength;
    }

    /// <summary>Click-through, never focused, hidden from Alt+Tab, excluded from screen capture.</summary>
    private void ConfigureWindow()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE,
            ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED | WS_EX_TRANSPARENT);
        SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const uint LWA_ALPHA = 0x2;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int value);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);
}

public sealed record RenderSettings(
    int Mode, double Softness, double Shadow, double DuoSwell,
    double ViewerDistance, double EyeHeight, double TiltStrength, double MaxTiltDeg,
    double HStrength, double VStrength, double? FixedPlaneAngle);
