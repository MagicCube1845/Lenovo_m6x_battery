using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace BatteryTray;

/// <summary>
/// 生成"带数字"的托盘图标：多尺寸 ICO（16/20/24/32/40/48/64），每个尺寸都原生绘制，
/// 避免 Windows 从 32px 缩放到 16px 时糊成一团。
/// </summary>
public static class BatteryIcon
{
    static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64 };

    public static Icon Create(int percent, bool charging, bool connected)
        => Create(percent, charging, connected, Sizes);

    public static Icon Create(int percent, bool charging, bool connected, int[] sizes)
    {
        byte[][] frames = sizes.Select(s => RenderPng(s, percent, charging, connected)).ToArray();
        return new Icon(new MemoryStream(BuildIco(sizes, frames)));
    }

    public static Color Background(int percent, bool charging, bool connected)
    {
        if (!connected) return Color.FromArgb(0x6B, 0x72, 0x80);
        if (charging) return Color.FromArgb(0x15, 0x65, 0xC0);
        if (percent < 0) return Color.FromArgb(0x6B, 0x72, 0x80);
        if (percent < 25) return Color.FromArgb(0xC6, 0x28, 0x28);
        if (percent < 60) return Color.FromArgb(0xD9, 0x8C, 0x00);
        return Color.FromArgb(0x2E, 0x9E, 0x4F);
    }

    public static string Text(int percent, bool connected) => !connected || percent < 0 ? "?" : percent.ToString();

    public static Bitmap RenderBitmap(int size, int percent, bool charging, bool connected)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var bg = Background(percent, charging, connected);
        float inset = size <= 16 ? 0.5f : size * 0.03f;
        var rect = new RectangleF(inset, inset, size - 2 * inset, size - 2 * inset);
        using (var path = RoundedRect(rect, size * 0.22f))
        using (var brush = new SolidBrush(bg))
        {
            g.FillPath(brush, path);
        }

        string text = Text(percent, connected);
        float boxW = rect.Width * 0.92f, boxH = rect.Height * 0.92f;

        using var sf = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoClip | StringFormatFlags.NoWrap,
        };

        float fontSize = size * 0.8f;
        for (int i = 0; i < 12 && fontSize > 4f; i++)
        {
            using var probe = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            var measured = g.MeasureString(text, probe, new PointF(0, 0), sf);
            if (measured.Width <= boxW && measured.Height <= boxH) break;
            fontSize *= 0.93f;
        }

        using (var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var textBrush = new SolidBrush(Color.White))
        {
            g.DrawString(text, font, textBrush, rect, sf);
        }

        return bmp;
    }

    static byte[] RenderPng(int size, int percent, bool charging, bool connected)
    {
        using var bmp = RenderBitmap(size, percent, charging, connected);
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    static byte[] BuildIco(int[] sizes, byte[][] frames)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)sizes.Length);

        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(frames[i].Length);
            w.Write(offset);
            offset += frames[i].Length;
        }

        foreach (var frame in frames) w.Write(frame);
        w.Flush();
        return ms.ToArray();
    }

    public static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
