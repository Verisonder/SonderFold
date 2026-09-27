using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace SonderFold;

/// <summary>
/// GPU pixel shader for the Ghost and Duo effects, ported from Mac Duo's Metal shader (MIT).
/// The desktop swells around the bottom-centre hinge (more at the top), blur grows
/// with distance from the hinge, edges feather into black, and it disappears at the end.
/// Compiled at runtime with Windows' built-in d3dcompiler_47.dll, so no extra tools are needed.
/// </summary>
public sealed class DuoEffect : ShaderEffect
{
    private const string Hlsl = """
        sampler2D Sharp : register(s0);
        sampler2D Blur1 : register(s1);
        sampler2D Blur2 : register(s2);
        sampler2D Blur3 : register(s3);

        float Progress       : register(c0);
        float Defocus        : register(c1);
        float Perspective    : register(c2);
        float Softness       : register(c3);
        float Shadow         : register(c4);
        float Aspect         : register(c5);
        float Sigma1         : register(c6);
        float Sigma2         : register(c7);
        float Sigma3         : register(c8);
        float Mode           : register(c9);   // 0 = Ghost, 1 = Duo
        float Tilt           : register(c10);  // radians from the resting plane
        float ReferenceAngle : register(c11);  // degrees
        float PixelH         : register(c12);  // 1 / screen height in pixels
        float ViewerDistance : register(c13);  // screen heights from the eye to the screen
        float EyeHeight      : register(c14);  // screen heights above the hinge
        float HStrength      : register(c15);  // horizontal squeeze multiplier
        float VStrength      : register(c16);  // vertical squeeze multiplier
        float Visibility     : register(c17);  // 1 = visible, 0 = black

        float3 SampleBlur(float2 src, float sigma)
        {
            float3 c0 = tex2D(Sharp, src).rgb;
            if (sigma <= 0.0) return c0;
            float3 c1 = tex2D(Blur1, src).rgb;
            float3 c2 = tex2D(Blur2, src).rgb;
            float3 c3 = tex2D(Blur3, src).rgb;
            if (sigma < Sigma1) return lerp(c0, c1, sigma / Sigma1);
            if (sigma < Sigma2) return lerp(c1, c2, (sigma - Sigma1) / (Sigma2 - Sigma1));
            return lerp(c2, c3, saturate((sigma - Sigma2) / (Sigma3 - Sigma2)));
        }

        // Ghost: a stationary viewer looks through the tilting panel at the desktop plane
        // anchored at the resting angle. The desktop appears to stay in place in space.
        float4 Ghost(float2 uv, float p)
        {
            float height = 1.0 - uv.y;
            float viewerDistance = max(ViewerDistance, 0.2);
            float eyeHeight = EyeHeight;
            float reference = clamp(ReferenceAngle, 45.0, 175.0) * 0.01745329;
            float eyeY = eyeHeight * sin(reference) + viewerDistance * cos(reference);
            float eyeZ = viewerDistance * sin(reference) - eyeHeight * cos(reference);
            float angle = clamp(Tilt, 0.0, 1.55);
            float depth = height * sin(angle);
            float rayScale = eyeZ / max(eyeZ - depth, 0.05);

            float fullY = 1.0 - (eyeY + (height * cos(angle) - eyeY) * rayScale);
            float2 src = float2(0.5 + (uv.x - 0.5) * (1.0 + (rayScale - 1.0) * HStrength),
                                lerp(uv.y, fullY, VStrength));

            float onset = saturate(angle / 0.03);
            onset = onset * onset * onset * (onset * (onset * 6.0 - 15.0) + 10.0);
            src = lerp(uv, src, onset);

            float focus = pow(saturate(Defocus) + 0.00001, 1.25);
            float separation = pow(max(height, 0.0001), 1.65);
            float sigma = max(Softness, 0.0) * 0.045 * focus * separation;
            sigma = max(sigma, 0.5 * max(0.0, rayScale - 1.0) * PixelH);
            float3 color = SampleBlur(src, sigma);

            float2 feather = max(float2(3.0 * sigma / Aspect, 3.0 * sigma), float2(1.5 * PixelH / Aspect, 1.5 * PixelH));
            float2 border = smoothstep(-feather, feather, src) * (1.0 - smoothstep(1.0 - feather, 1.0 + feather, src));
            float edgeCoverage = lerp(1.0, border.x * border.y, smoothstep(0.0, 0.025, angle));
            float shade = saturate(1.0 - max(Shadow, 0.0) * 0.10 * focus * height);
            return float4(color * shade * edgeCoverage * saturate(Visibility), 1.0);
        }

        // Duo: the desktop swells around the hinge and leaves through the top.
        float4 Duo(float2 uv, float p)
        {
            float height = 1.0 - uv.y;
            float expansion = 1.0 + p * (0.12 + lerp(0.30, 0.66, max(Perspective, 0.0)) * height);
            float2 src = float2(0.5 + (uv.x - 0.5) / expansion, 1.0 - height / expansion);

            float spread = 0.12 + 0.88 * pow(max(height, 0.0001), 1.15);
            float sigma = max(Softness, 0.0) * 0.052 * saturate(Defocus) * spread;
            float3 color = SampleBlur(src, sigma);

            float soft = max(Softness, 0.0);
            float topW  = p * (0.075 + 0.15 * soft) + 1.5 * sigma;
            float sideW = (p * (0.055 + 0.12 * soft) + 1.5 * sigma) / Aspect;
            float botW  = p * (0.012 + 0.025 * soft) + 0.5 * sigma;
            float mask = smoothstep(0.0, topW, uv.y) * smoothstep(0.0, botW, height)
                       * smoothstep(0.0, sideW, uv.x) * smoothstep(0.0, sideW, 1.0 - uv.x);

            float shade = saturate(1.0 - max(Shadow, 0.0) * 0.12 * p * p * height);
            return float4(color * mask * shade * saturate(Visibility), 1.0);
        }

        float4 main(float2 uv : TEXCOORD) : COLOR
        {
            float p = saturate(Progress);
            if (p < 0.00001 && Tilt < 0.00001 && Visibility > 0.9999) return tex2D(Sharp, uv);
            if (Visibility <= 0.0001) return float4(0, 0, 0, 1);
            return Mode < 0.5 ? Ghost(uv, p) : Duo(uv, p);
        }
        """;

    private static PixelShader? _shader;

    public static readonly DependencyProperty InputProperty =
        RegisterPixelShaderSamplerProperty("Input", typeof(DuoEffect), 0, SamplingMode.Bilinear);
    public static readonly DependencyProperty Blur1Property =
        RegisterPixelShaderSamplerProperty("Blur1", typeof(DuoEffect), 1, SamplingMode.Bilinear);
    public static readonly DependencyProperty Blur2Property =
        RegisterPixelShaderSamplerProperty("Blur2", typeof(DuoEffect), 2, SamplingMode.Bilinear);
    public static readonly DependencyProperty Blur3Property =
        RegisterPixelShaderSamplerProperty("Blur3", typeof(DuoEffect), 3, SamplingMode.Bilinear);

    public static readonly DependencyProperty ProgressProperty = Constant("Progress", 0);
    public static readonly DependencyProperty DefocusProperty = Constant("Defocus", 1);
    public static readonly DependencyProperty PerspectiveProperty = Constant("Perspective", 2, 0.7);
    public static readonly DependencyProperty SoftnessProperty = Constant("Softness", 3, 0.65);
    public static readonly DependencyProperty ShadowProperty = Constant("Shadow", 4, 0.65);
    public static readonly DependencyProperty AspectProperty = Constant("Aspect", 5, 1.6);
    public static readonly DependencyProperty Sigma1Property = Constant("Sigma1", 6, 0.007);
    public static readonly DependencyProperty Sigma2Property = Constant("Sigma2", 7, 0.028);
    public static readonly DependencyProperty Sigma3Property = Constant("Sigma3", 8, 0.11);
    public static readonly DependencyProperty ModeProperty = Constant("Mode", 9, 0);
    public static readonly DependencyProperty TiltProperty = Constant("Tilt", 10, 0);
    public static readonly DependencyProperty ReferenceAngleProperty = Constant("ReferenceAngle", 11, 105);
    public static readonly DependencyProperty PixelHProperty = Constant("PixelH", 12, 1.0 / 1200);
    public static readonly DependencyProperty ViewerDistanceProperty = Constant("ViewerDistance", 13, 1.9);
    public static readonly DependencyProperty EyeHeightProperty = Constant("EyeHeight", 14, 1.6);
    public static readonly DependencyProperty HStrengthProperty = Constant("HStrength", 15, 1);
    public static readonly DependencyProperty VStrengthProperty = Constant("VStrength", 16, 1);
    public static readonly DependencyProperty VisibilityProperty = Constant("Visibility", 17, 1);

    public DuoEffect()
    {
        PixelShader = _shader ??= CompileShader();
        UpdateShaderValue(InputProperty);
        UpdateShaderValue(Blur1Property);
        UpdateShaderValue(Blur2Property);
        UpdateShaderValue(Blur3Property);
        UpdateShaderValue(ProgressProperty);
        UpdateShaderValue(DefocusProperty);
        UpdateShaderValue(PerspectiveProperty);
        UpdateShaderValue(SoftnessProperty);
        UpdateShaderValue(ShadowProperty);
        UpdateShaderValue(AspectProperty);
        UpdateShaderValue(Sigma1Property);
        UpdateShaderValue(Sigma2Property);
        UpdateShaderValue(Sigma3Property);
        UpdateShaderValue(ModeProperty);
        UpdateShaderValue(TiltProperty);
        UpdateShaderValue(ReferenceAngleProperty);
        UpdateShaderValue(PixelHProperty);
        UpdateShaderValue(ViewerDistanceProperty);
        UpdateShaderValue(EyeHeightProperty);
        UpdateShaderValue(HStrengthProperty);
        UpdateShaderValue(VStrengthProperty);
        UpdateShaderValue(VisibilityProperty);
    }

    public Brush Input { get => (Brush)GetValue(InputProperty); set => SetValue(InputProperty, value); }
    public Brush Blur1 { get => (Brush)GetValue(Blur1Property); set => SetValue(Blur1Property, value); }
    public Brush Blur2 { get => (Brush)GetValue(Blur2Property); set => SetValue(Blur2Property, value); }
    public Brush Blur3 { get => (Brush)GetValue(Blur3Property); set => SetValue(Blur3Property, value); }
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public double Defocus { get => (double)GetValue(DefocusProperty); set => SetValue(DefocusProperty, value); }
    public double Perspective { get => (double)GetValue(PerspectiveProperty); set => SetValue(PerspectiveProperty, value); }
    public double Softness { get => (double)GetValue(SoftnessProperty); set => SetValue(SoftnessProperty, value); }
    public double Shadow { get => (double)GetValue(ShadowProperty); set => SetValue(ShadowProperty, value); }
    public double Aspect { get => (double)GetValue(AspectProperty); set => SetValue(AspectProperty, value); }
    public double Sigma1 { get => (double)GetValue(Sigma1Property); set => SetValue(Sigma1Property, value); }
    public double Sigma2 { get => (double)GetValue(Sigma2Property); set => SetValue(Sigma2Property, value); }
    public double Sigma3 { get => (double)GetValue(Sigma3Property); set => SetValue(Sigma3Property, value); }
    public double Mode { get => (double)GetValue(ModeProperty); set => SetValue(ModeProperty, value); }
    public double Tilt { get => (double)GetValue(TiltProperty); set => SetValue(TiltProperty, value); }
    public double ReferenceAngle { get => (double)GetValue(ReferenceAngleProperty); set => SetValue(ReferenceAngleProperty, value); }
    public double PixelH { get => (double)GetValue(PixelHProperty); set => SetValue(PixelHProperty, value); }
    public double ViewerDistance { get => (double)GetValue(ViewerDistanceProperty); set => SetValue(ViewerDistanceProperty, value); }
    public double EyeHeight { get => (double)GetValue(EyeHeightProperty); set => SetValue(EyeHeightProperty, value); }
    public double HStrength { get => (double)GetValue(HStrengthProperty); set => SetValue(HStrengthProperty, value); }
    public double VStrength { get => (double)GetValue(VStrengthProperty); set => SetValue(VStrengthProperty, value); }
    public double Visibility { get => (double)GetValue(VisibilityProperty); set => SetValue(VisibilityProperty, value); }

    private static DependencyProperty Constant(string name, int register, double defaultValue = 0) =>
        DependencyProperty.Register(name, typeof(double), typeof(DuoEffect),
            new UIPropertyMetadata(defaultValue, PixelShaderConstantCallback(register)));

    private static PixelShader CompileShader()
    {
        byte[] source = System.Text.Encoding.ASCII.GetBytes(Hlsl);
        int hr = D3DCompile(source, (IntPtr)source.Length, "duo.hlsl", IntPtr.Zero, IntPtr.Zero,
            "main", "ps_3_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, out ID3DBlob? code, out ID3DBlob? errors);

        if (hr < 0 || code == null)
        {
            string message = errors != null
                ? Marshal.PtrToStringAnsi(errors.GetBufferPointer(), (int)errors.GetBufferSize()) ?? ""
                : $"HRESULT 0x{hr:X8}";
            throw new InvalidOperationException("Shader compile failed: " + message);
        }

        var bytes = new byte[(int)code.GetBufferSize()];
        Marshal.Copy(code.GetBufferPointer(), bytes, 0, bytes.Length);

        var shader = new PixelShader();
        shader.SetStreamSource(new MemoryStream(bytes));
        return shader;
    }

    private const uint D3DCOMPILE_OPTIMIZATION_LEVEL3 = 1 << 15;

    [ComImport, Guid("8BA5FB08-5195-40e2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ID3DBlob
    {
        [PreserveSig] IntPtr GetBufferPointer();
        [PreserveSig] IntPtr GetBufferSize();
    }

    [DllImport("d3dcompiler_47.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
    private static extern int D3DCompile(byte[] pSrcData, IntPtr srcDataSize, string? pSourceName,
        IntPtr pDefines, IntPtr pInclude, string pEntrypoint, string pTarget,
        uint flags1, uint flags2, out ID3DBlob? ppCode, out ID3DBlob? ppErrorMsgs);
}
