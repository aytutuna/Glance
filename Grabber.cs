using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Glance;

static class Grabber
{
    const int MaxEdge = 2000;

    public static Bitmap Grab(Screen screen)
    {
        var area = screen.Bounds;
        var full = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(full);
        g.CopyFromScreen(area.Location, Point.Empty, area.Size);
        return full;
    }

    public static byte[] Encode(Bitmap full, Rectangle? crop)
    {
        var part = crop ?? new Rectangle(Point.Empty, full.Size);
        double scale = Math.Min(1.0, (double)MaxEdge / Math.Max(part.Width, part.Height));
        using var ms = new MemoryStream();
        if (scale < 1.0)
        {
            int w = Math.Max(1, (int)Math.Round(part.Width * scale)), h = Math.Max(1, (int)Math.Round(part.Height * scale));
            using var small = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(full, new Rectangle(0, 0, w, h), part, GraphicsUnit.Pixel);
            }
            SaveJpeg(small, ms);
        }
        else if (crop != null)
        {
            using var cut = full.Clone(part, PixelFormat.Format24bppRgb);
            SaveJpeg(cut, ms);
        }
        else
        {
            SaveJpeg(full, ms);
        }
        return ms.ToArray();
    }

    static void SaveJpeg(Bitmap bmp, Stream to)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var args = new EncoderParameters(1);
        args.Param[0] = new EncoderParameter(Encoder.Quality, 90L);
        bmp.Save(to, codec, args);
    }
}
