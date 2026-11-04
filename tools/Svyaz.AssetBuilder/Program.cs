using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var directory = Path.GetFullPath(args.Length == 0 ? "src/Svyaz.App/Assets" : args[0]);
        Directory.CreateDirectory(directory);
        var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
        var frames = sizes.Select(Render).ToArray();
        File.WriteAllBytes(Path.Combine(directory, "svyaz.png"), frames[^1]);
        using var file = File.Create(Path.Combine(directory, "svyaz.ico"));
        using var writer = new BinaryWriter(file);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
            writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32);
            writer.Write(frames[i].Length); writer.Write(offset); offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
        var branding = Path.GetFullPath("installer/assets"); Directory.CreateDirectory(branding);
        RenderBanner(Path.Combine(branding, "welcome.bmp"), 164, 314, true, frames[^1]);
        RenderBanner(Path.Combine(branding, "header.bmp"), 150, 57, false, frames[^1]);
        Console.WriteLine("Generated PNG and seven-size ICO: " + directory);
    }
    private static void RenderBanner(string path, int width, int height, bool welcome, byte[] icon)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = new MemoryStream(icon); image.EndInit();
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush((Color)ColorConverter.ConvertFromString("#272045"), (Color)ColorConverter.ConvertFromString("#101C2F"), 65), null, new Rect(0, 0, width, height));
            var side = welcome ? 122d : 48d;
            dc.DrawImage(image, new Rect(welcome ? 21 : 3, welcome ? 49 : 4, side, side));
            var title = new FormattedText(welcome ? "связь" : "Связь", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), welcome ? 31 : 17, Brushes.White, 1);
            dc.DrawText(title, new Point(welcome ? 26 : 58, welcome ? 182 : 14));
            if (welcome)
            {
                var caption = new FormattedText("DESKTOP\n\nВаши сервисы.\nВаш маршрут.", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"), FlowDirection.LeftToRight,
                    new Typeface("Segoe UI"), 12, new SolidColorBrush(Color.FromRgb(184, 178, 219)), 1);
                dc.DrawText(caption, new Point(28, 230));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var rgb = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
        var encoder = new BmpBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rgb)); using var output = File.Create(path); encoder.Save(output);
    }
    private static byte[] Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 512d, size / 512d));
            var tile = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            tile.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#A291FF"), 0));
            tile.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#7760DC"), 0.5));
            tile.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString("#493697"), 1));
            dc.DrawRoundedRectangle(tile, new Pen(new SolidColorBrush(Color.FromArgb(90, 207, 195, 255)), 3), new Rect(16, 16, 480, 480), 112, 112);
            var pen = new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F8F7FF")), 34)
            { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, Geometry.Parse("M344,150 C302,109 226,116 197,159 C168,202 195,238 245,253 L269,260 C318,275 344,311 313,354 C282,397 206,397 164,355"));
            var mint = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8CF1D5"));
            dc.DrawEllipse(mint, null, new Point(344, 150), 20, 20); dc.DrawEllipse(mint, null, new Point(164, 355), 20, 20);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
}
