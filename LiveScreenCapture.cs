using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SonderFold;

/// <summary>
/// Captures the primary screen continuously on a background thread and builds a small
/// blur pyramid (1/4 and 1/16 size, box-blurred) for the shader's variable blur.
/// The overlay window is excluded from capture.
/// </summary>
internal sealed class LiveScreenCapture
{
    private readonly int _w, _h, _w1, _h1, _w2, _h2, _w3, _h3;
    private readonly byte[] _frame, _l1, _l2, _l3;
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _firstFrame = new(false);

    private long _frameId, _shownId = -1;
    private Thread? _thread;
    private volatile bool _running;

    public WriteableBitmap Bitmap { get; }
    public WriteableBitmap Level1 { get; }
    public WriteableBitmap Level2 { get; }
    public WriteableBitmap Level3 { get; }
    public int Height => _h;
    public int TargetFps { get; set; } = 60;

    /// <summary>Approximate blur of each level, as a fraction of screen height.</summary>
    public double Sigma1 => 8.5 / _h;
    public double Sigma2 => 34.0 / _h;
    public double Sigma3 => 135.0 / _h;

    public LiveScreenCapture()
    {
        _w = GetSystemMetrics(SM_CXSCREEN);
        _h = GetSystemMetrics(SM_CYSCREEN);
        _w1 = _w / 4; _h1 = _h / 4;
        _w2 = _w1 / 4; _h2 = _h1 / 4;
        _w3 = Math.Max(1, _w2 / 4); _h3 = Math.Max(1, _h2 / 4);

        _frame = new byte[_w * _h * 4];
        _l1 = new byte[_w1 * _h1 * 4];
        _l2 = new byte[_w2 * _h2 * 4];
        _l3 = new byte[_w3 * _h3 * 4];

        Bitmap = new WriteableBitmap(_w, _h, 96, 96, PixelFormats.Bgr32, null);
        Level1 = new WriteableBitmap(_w1, _h1, 96, 96, PixelFormats.Bgr32, null);
        Level2 = new WriteableBitmap(_w2, _h2, 96, 96, PixelFormats.Bgr32, null);
        Level3 = new WriteableBitmap(_w3, _h3, 96, 96, PixelFormats.Bgr32, null);
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _firstFrame.Reset();
        _thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal, Name = "SonderFold capture" };
        _thread.Start();
        _firstFrame.Wait(250);
        PushToBitmaps();
    }

    public void Stop()
    {
        _running = false;
        _thread?.Join(250);
        _thread = null;
    }

    /// <summary>UI thread, once per frame: upload the newest frame and its pyramid.</summary>
    public void PushToBitmaps()
    {
        lock (_lock)
        {
            if (_frameId == _shownId) return;
            Bitmap.WritePixels(new Int32Rect(0, 0, _w, _h), _frame, _w * 4, 0);
            Level1.WritePixels(new Int32Rect(0, 0, _w1, _h1), _l1, _w1 * 4, 0);
            Level2.WritePixels(new Int32Rect(0, 0, _w2, _h2), _l2, _w2 * 4, 0);
            Level3.WritePixels(new Int32Rect(0, 0, _w3, _h3), _l3, _w3 * 4, 0);
            _shownId = _frameId;
        }
    }

    private void Loop()
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = _w,
            biHeight = -_h,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0
        };
        IntPtr dib = CreateDIBSection(screenDc, ref bmi, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr old = SelectObject(memDc, dib);

        var work = new byte[_frame.Length];
        var work1 = new byte[_l1.Length];
        var tmp1 = new byte[_l1.Length];
        var work2 = new byte[_l2.Length];
        var tmp2 = new byte[_l2.Length];
        var work3 = new byte[_l3.Length];
        var tmp3 = new byte[_l3.Length];
        var sw = Stopwatch.StartNew();

        try
        {
            while (_running)
            {
                double start = sw.Elapsed.TotalMilliseconds;

                BitBlt(memDc, 0, 0, _w, _h, screenDc, 0, 0, SRCCOPY);
                GdiFlush();
                Marshal.Copy(bits, work, 0, work.Length);

                Downscale(work, _w, work1, _w1, _h1, 4);
                BoxBlur(work1, tmp1, _w1, _h1, 2);
                BoxBlur(work1, tmp1, _w1, _h1, 2);
                Downscale(work1, _w1, work2, _w2, _h2, 4);
                BoxBlur(work2, tmp2, _w2, _h2, 2);
                BoxBlur(work2, tmp2, _w2, _h2, 2);
                Downscale(work2, _w2, work3, _w3, _h3, 4);
                BoxBlur(work3, tmp3, _w3, _h3, 2);
                BoxBlur(work3, tmp3, _w3, _h3, 2);

                lock (_lock)
                {
                    Buffer.BlockCopy(work, 0, _frame, 0, work.Length);
                    Buffer.BlockCopy(work1, 0, _l1, 0, work1.Length);
                    Buffer.BlockCopy(work2, 0, _l2, 0, work2.Length);
                    Buffer.BlockCopy(work3, 0, _l3, 0, work3.Length);
                    _frameId++;
                }
                _firstFrame.Set();

                double wait = 1000.0 / TargetFps - (sw.Elapsed.TotalMilliseconds - start);
                if (wait >= 1) Thread.Sleep((int)wait);
            }
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(dib);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static void Downscale(byte[] src, int srcW, byte[] dst, int dstW, int dstH, int f)
    {
        int area = f * f;
        for (int y = 0; y < dstH; y++)
        {
            for (int x = 0; x < dstW; x++)
            {
                int b = 0, g = 0, r = 0;
                for (int yy = 0; yy < f; yy++)
                {
                    int i = ((y * f + yy) * srcW + x * f) * 4;
                    for (int xx = 0; xx < f; xx++, i += 4)
                    {
                        b += src[i]; g += src[i + 1]; r += src[i + 2];
                    }
                }
                int o = (y * dstW + x) * 4;
                dst[o] = (byte)(b / area);
                dst[o + 1] = (byte)(g / area);
                dst[o + 2] = (byte)(r / area);
                dst[o + 3] = 255;
            }
        }
    }

    /// <summary>Separable box blur with clamped edges (data -> tmp -> data).</summary>
    private static void BoxBlur(byte[] data, byte[] tmp, int w, int h, int r)
    {
        int div = 2 * r + 1;

        for (int y = 0; y < h; y++)
        {
            int row = y * w * 4;
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += data[row + Math.Clamp(k, 0, w - 1) * 4 + c];
                for (int x = 0; x < w; x++)
                {
                    tmp[row + x * 4 + c] = (byte)(sum / div);
                    int xOut = Math.Clamp(x - r, 0, w - 1), xIn = Math.Clamp(x + r + 1, 0, w - 1);
                    sum += data[row + xIn * 4 + c] - data[row + xOut * 4 + c];
                }
            }
            for (int x = 0; x < w; x++) tmp[row + x * 4 + 3] = 255;
        }

        for (int x = 0; x < w; x++)
        {
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int k = -r; k <= r; k++) sum += tmp[(Math.Clamp(k, 0, h - 1) * w + x) * 4 + c];
                for (int y = 0; y < h; y++)
                {
                    data[(y * w + x) * 4 + c] = (byte)(sum / div);
                    int yOut = Math.Clamp(y - r, 0, h - 1), yIn = Math.Clamp(y + r + 1, 0, h - 1);
                    sum += tmp[(yIn * w + x) * 4 + c] - tmp[(yOut * w + x) * 4 + c];
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    private const int SM_CXSCREEN = 0, SM_CYSCREEN = 1;
    private const int SRCCOPY = 0x00CC0020;

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int x1, int y1, int rop);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}
