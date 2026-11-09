namespace Glance;

class AreaPicker : Form
{
    const int MinSize = 8;

    readonly Screen screen;
    readonly Bitmap shot;
    readonly string hint;
    readonly Font font;
    Rectangle sel;
    Point start;
    bool dragging, activated;

    // in pixels of the captured bitmap
    public Rectangle? Selection { get; private set; }

    public AreaPicker(Screen screen, Bitmap shot, string hint)
    {
        this.screen = screen;
        this.shot = shot;
        this.hint = hint;
        font = new Font("Segoe UI", (float)(16 * Native.ScaleFor(screen)), GraphicsUnit.Pixel);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        Bounds = screen.Bounds;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // a DPI change on the way to another monitor can resize the window
        for (int i = 0; i < 3 && Bounds != screen.Bounds; i++)
            Bounds = screen.Bounds;
        Activate();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        activated = true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (activated)
            Close();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        activated = false;
        base.OnFormClosing(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape)
            Close();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            start = e.Location;
            dragging = true;
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging)
            return;
        var old = sel;
        var end = new Point(Math.Clamp(e.X, 0, ClientSize.Width), Math.Clamp(e.Y, 0, ClientSize.Height));
        sel = Rectangle.FromLTRB(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
        var dirty = Rectangle.Union(old, sel);
        dirty.Inflate(4, 4);
        Invalidate(dirty);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Right)
        {
            Close();
            return;
        }
        if (e.Button != MouseButtons.Left || !dragging)
            return;
        dragging = false;
        if (sel.Width >= MinSize && sel.Height >= MinSize)
        {
            double sx = (double)shot.Width / ClientSize.Width, sy = (double)shot.Height / ClientSize.Height;
            var r = Rectangle.FromLTRB((int)Math.Round(sel.Left * sx), (int)Math.Round(sel.Top * sy),
                (int)Math.Round(sel.Right * sx), (int)Math.Round(sel.Bottom * sy));
            r.Intersect(new Rectangle(Point.Empty, shot.Size));
            if (r.Width > 0 && r.Height > 0)
                Selection = r;
        }
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var all = ClientRectangle;
        g.DrawImage(shot, all);

        using var dim = new SolidBrush(Color.FromArgb(120, 0, 0, 0));
        var state = g.Save();
        if (!sel.IsEmpty)
            g.ExcludeClip(sel);
        g.FillRectangle(dim, all);
        g.Restore(state);

        if (!sel.IsEmpty)
        {
            using var pen = new Pen(Color.White, Math.Max(1, DeviceDpi / 96f * 1.5f));
            g.DrawRectangle(pen, sel);
        }
        else if (!dragging)
        {
            var size = TextRenderer.MeasureText(hint, font);
            int pad = size.Height / 2;
            var box = new Rectangle((all.Width - size.Width) / 2 - pad, all.Height / 8, size.Width + 2 * pad, size.Height + pad);
            using var back = new SolidBrush(Color.FromArgb(32, 32, 32));
            g.FillRectangle(back, box);
            TextRenderer.DrawText(g, hint, font, box, Color.FromArgb(240, 240, 240), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            font.Dispose();
        base.Dispose(disposing);
    }
}
