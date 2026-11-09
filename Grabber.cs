using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Glance;

static class Grabber
{
    const int MaxEdge = 2000;

    public static byte[] Capture(Screen screen)
    {
        var area = screen.Bounds;
        using var full = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(full))
            g.CopyFromScreen(area.Location, Point.Empty, area.Size);

        double scale = Math.Min(1.0, (double)MaxEdge / Math.Max(area.Width, area.Height));
        using var ms = new MemoryStream();
        if (scale < 1.0)
        {
            int w = (int)Math.Round(area.Width * scale), h = (int)Math.Round(area.Height * scale);
            using var small = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(small))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(full, 0, 0, w, h);
            }
            SaveJpeg(small, ms);
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
