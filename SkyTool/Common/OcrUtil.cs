using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace SkyTool.Common;

/// <summary>调用 Windows 10/11 自带 OCR 引擎（Windows.Media.Ocr）识别图片里的文字。
/// 不打包任何模型：识别能力来自系统已安装的语言包，安装包体积不变。</summary>
internal static class OcrUtil
{
    /// <summary>系统是否具备可用的 OCR 引擎（至少装了一种语言包）。</summary>
    public static bool IsAvailable => CreateEngine() != null;

    private static OcrEngine CreateEngine()
    {
        // 优先用用户语言（中文系统通常含中文识别），否则退中文 / 英文
        return OcrEngine.TryCreateFromUserProfileLanguages()
               ?? OcrEngine.TryCreateFromLanguage(new Language("zh-Hans-CN"))
               ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
    }

    /// <summary>识别一张 WPF 位图里的文字；无引擎或无文字时返回空串。</summary>
    public static async Task<string> RecognizeAsync(BitmapSource source)
    {
        var engine = CreateEngine();
        if (engine == null) return null; // 系统未安装 OCR 语言包

        // 关键：屏幕 UI 文字很小，原尺寸识别率差。先放大 ~2.5 倍再识别，
        // 中文准确率显著提升（实测 1x 多处误识，2x 起几乎全对，再大无收益且费内存）。
        int max = (int)OcrEngine.MaxImageDimension;
        int longSide = Math.Max(source.PixelWidth, source.PixelHeight);
        double factor;
        if (longSide > max) factor = (double)max / longSide;          // 超过引擎上限 → 缩小
        else if (longSide < 2000) factor = Math.Min(2.5, (double)max / longSide); // 小图 → 放大提精度
        else factor = 1.0;                                            // 已足够大 → 原样
        if (Math.Abs(factor - 1.0) > 0.01)
        {
            var t = new TransformedBitmap(source, new ScaleTransform(factor, factor));
            t.Freeze();
            source = t;
        }

        // WPF BitmapSource → BMP 内存流 → WinRT SoftwareBitmap
        using var ms = new MemoryStream();
        var enc = new BmpBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        enc.Save(ms);
        ms.Position = 0;

        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ms.AsRandomAccessStream());
        using var bmp = await decoder.GetSoftwareBitmapAsync();
        var result = await engine.RecognizeAsync(bmp);
        return BuildText(result);
    }

    private static string BuildText(OcrResult result)
    {
        var sb = new StringBuilder();
        foreach (var line in result.Lines)
            sb.AppendLine(CleanCjkSpaces(line.Text));
        return sb.ToString().TrimEnd();
    }

    // OCR 会在每个“词”间补空格，对中文会变成“你 好 世 界”。去掉中日韩字符之间的空格，保留英文词距。
    private static readonly Regex CjkSpaces = new(
        @"(?<=[一-鿿　-〿＀-￯])\s+(?=[一-鿿　-〿＀-￯])",
        RegexOptions.Compiled);

    private static string CleanCjkSpaces(string s) => CjkSpaces.Replace(s, "");
}
