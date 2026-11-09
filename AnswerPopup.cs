namespace Glance;

class AnswerPopup : OverlayForm
{
    Screen screen;
    double scale;
    Font font;
    readonly Panel panel;
    readonly Label label;
    readonly Panel bar;
    readonly Label copy, close;
    readonly System.Windows.Forms.Timer reset = new() { Interval = 1500 };
    string text;
    Rectangle target;

    public event Action? CloseClicked;

    public AnswerPopup(Screen screen, string text, bool reading = false)
    {
        this.text = text;
        this.screen = screen;
        scale = Native.ScaleFor(screen);
        font = NewFont();

        BackColor = Color.FromArgb(32, 32, 32);

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
        bar = new Panel { Dock = DockStyle.Top, BackColor = BackColor };
        copy = NewButton("Copy", ContentAlignment.MiddleRight);
        close = NewButton("×", ContentAlignment.MiddleCenter);
        copy.Click += (_, _) => CopyText();
        close.Click += (_, _) => CloseClicked?.Invoke();
        bar.Controls.Add(copy);
        bar.Controls.Add(close);
        Controls.Add(panel);
        Controls.Add(bar);
        SetReading(reading);
        reset.Tick += (_, _) =>
        {
            reset.Stop();
            copy.Text = "Copy";
        };

        Fit(text);
        Bounds = target;
    }

    Label NewButton(string text, ContentAlignment align)
    {
        return new Label
        {
            Text = text,
            Font = font,
            ForeColor = Color.FromArgb(170, 170, 170),
            BackColor = BackColor,
            AutoSize = false,
            UseMnemonic = false,
            TextAlign = align,
            Cursor = Cursors.Hand,
        };
    }

    void SetReading(bool reading)
    {
        copy.Enabled = !reading;
        copy.ForeColor = reading ? Color.FromArgb(90, 90, 90) : Color.FromArgb(170, 170, 170);
        copy.Cursor = reading ? Cursors.Default : Cursors.Hand;
    }

    void CopyText()
    {
        copy.Text = TrayApp.Copy(text) ? "Copied" : "Failed";
        reset.Stop();
        reset.Start();
    }

    public void SetText(string text)
    {
        this.text = text;
        SetReading(false);
        // the monitor may have been unplugged or rescaled since the popup was created
        screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == screen.DeviceName) ?? Screen.FromHandle(Handle);
        double now = Native.ScaleFor(screen);
        if (now != scale)
        {
            scale = now;
            var old = font;
            font = NewFont();
            label.Font = font;
            copy.Font = font;
            close.Font = font;
            old.Dispose();
        }
        Fit(text);
        EnsureBounds();
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
        int barHeight = (int)(28 * scale);
        int height = textHeight + 2 * pad + barHeight;
        if (height > maxHeight)
        {
            inner -= (int)(20 * scale);
            textHeight = Measure(text, inner);
            height = maxHeight;
        }

        bar.Height = barHeight;
        int closeWidth = (int)(30 * scale), copyWidth = (int)(90 * scale);
        close.SetBounds(width - pad - closeWidth, 0, closeWidth, barHeight);
        copy.SetBounds(close.Left - copyWidth, 0, copyWidth, barHeight);

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
        {
            reset.Dispose();
            font.Dispose();
        }
        base.Dispose(disposing);
    }
}
