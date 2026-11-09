namespace Glance;

class ScreenLabel : OverlayForm
{
    readonly string text;
    readonly Font font;

    public ScreenLabel(Screen screen, int number)
    {
        text = number.ToString();
        double scale = Native.ScaleFor(screen);
        int size = (int)(240 * scale);
        font = new Font("Segoe UI", (float)(160 * scale), FontStyle.Bold, GraphicsUnit.Pixel);
        BackColor = Color.FromArgb(32, 32, 32);
        Opacity = 0.92;

        var b = screen.Bounds;
        Bounds = new Rectangle(b.X + (b.Width - size) / 2, b.Y + (b.Height - size) / 2, size, size);

        var timer = new System.Windows.Forms.Timer { Interval = 2000 };
        timer.Tick += (_, _) => Close();
        FormClosed += (_, _) => timer.Dispose();
        timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        using var brush = new SolidBrush(Color.White);
        e.Graphics.DrawString(text, font, brush, ClientRectangle, format);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            font.Dispose();
        base.Dispose(disposing);
    }
}
