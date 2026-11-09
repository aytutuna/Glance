namespace ScreenCopy;

class AnswerPopup : OverlayForm
{
    Screen screen;
    double scale;
    Font font;
    readonly Panel panel;
    readonly Label label;
    Rectangle target;

    public AnswerPopup(Screen screen, string text)
    {
        this.screen = screen;
        scale = Native.ScaleFor(screen);
        font = NewFont();

        BackColor = Color.FromArgb(32, 32, 32);
        Cursor = Cursors.Hand;

        label = new Label
        {
            Font = font,
            ForeColor = Color.FromArgb(240, 240, 240),
            BackColor = BackColor,
            AutoSize = false,
            UseMnemonic = false,
        };
        panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = BackColor };
        panel.Controls.Add(label);
        Controls.Add(panel);

        Click += (_, _) => Close();
        panel.Click += (_, _) => Close();
        label.Click += (_, _) => Close();

        Fit(text);
        Bounds = target;
    }

    public void SetText(string text)
    {
        // the monitor may have been unplugged or rescaled since the popup was created
        screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == screen.DeviceName) ?? Screen.FromHandle(Handle);
        double now = Native.ScaleFor(screen);
        if (now != scale)
        {
            scale = now;
            var old = font;
            font = NewFont();
            label.Font = font;
            old.Dispose();
        }
        Fit(text);
        Bounds = target;
    }

    Font NewFont()
    {
        return new Font("Segoe UI", (float)(15 * scale), GraphicsUnit.Pixel);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        EnsureBounds();
    }

    // A window created on one monitor can be rescaled by a DPI change on its way to another.
    void EnsureBounds()
    {
        for (int i = 0; i < 3 && Bounds != target; i++)
            Bounds = target;
    }

    void Fit(string text)
    {
        var wa = screen.WorkingArea;
        int margin = (int)(16 * scale), pad = (int)(14 * scale);
        int width = (int)(420 * scale);
        int maxHeight = (int)(wa.Height * 0.6);
        int inner = width - 2 * pad;

        int textHeight = Measure(text, inner);
        int height = textHeight + 2 * pad;
        if (height > maxHeight)
        {
            inner -= (int)(20 * scale);
            textHeight = Measure(text, inner);
            height = maxHeight;
        }

        label.Text = text;
        label.SetBounds(pad, pad, inner, textHeight);
        panel.AutoScrollMinSize = new Size(0, textHeight + 2 * pad);

        target = new Rectangle(wa.Right - width - margin, wa.Bottom - height - margin, width, height);
    }

    int Measure(string text, int width)
    {
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        return TextRenderer.MeasureText(text, font, new Size(width, int.MaxValue), flags).Height + 2;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            font.Dispose();
        base.Dispose(disposing);
    }
}
