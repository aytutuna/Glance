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
    readonly Panel inputBar;
    readonly TextBox box;
    readonly System.Windows.Forms.Timer reset = new() { Interval = 1500 };
    readonly System.Windows.Forms.Timer flush = new() { Interval = 50 };
    string text;
    string? pending;
    long lastLayout;
    Rectangle target;
    bool ask, activatable;
    readonly bool centered, plain;
    readonly double[]? area;
    Font? fit;
    IntPtr previous;

    public event Action? CloseClicked;
    public event Action<string>? Asked;

    public AnswerPopup(Screen screen, string text, bool reading = false, Settings? look = null)
    {
        this.text = text;
        this.screen = screen;
        area = look is { UseArea: true } ? look.AnswerArea : null;
        centered = area == null && look is { CenterAnswer: true };
        plain = look is { PlainAnswer: true };
        scale = Native.ScaleFor(screen);
        font = NewFont();
        if (area != null)
            fit = LargestFit();

        BackColor = plain ? Color.Black : Color.FromArgb(32, 32, 32);

        label = new Label
        {
            Font = TextFont,
            ForeColor = plain ? Color.White : Color.FromArgb(240, 240, 240),
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
        inputBar = new Panel { Dock = DockStyle.Bottom, BackColor = BackColor, Visible = false };
        box = new TextBox
        {
            Font = font,
            ForeColor = Color.FromArgb(240, 240, 240),
            BackColor = Color.FromArgb(52, 52, 52),
            BorderStyle = BorderStyle.FixedSingle,
            PlaceholderText = "Ask a follow-up",
        };
        box.MouseDown += (_, _) => TakeFocus();
        box.KeyDown += BoxKeyDown;
        inputBar.Controls.Add(box);
        Controls.Add(panel);
        Controls.Add(inputBar);
        Controls.Add(bar);
        if (plain)
        {
            // no buttons to click, so a click anywhere closes it
            bar.Visible = false;
            Click += (_, _) => CloseClicked?.Invoke();
            panel.Click += (_, _) => CloseClicked?.Invoke();
            label.Click += (_, _) => CloseClicked?.Invoke();
        }
        SetReading(reading);
        reset.Tick += (_, _) =>
        {
            reset.Stop();
            copy.Text = "Copy";
        };

        flush.Tick += (_, _) =>
        {
            flush.Stop();
            if (pending != null)
                Render(pending);
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

    void TakeFocus()
    {
        if (activatable)
            return;
        activatable = true;
        previous = Native.GetForegroundWindow();
        SetActivatable(true);
        Activate();
        box.Focus();
    }

    // hands the foreground back to the window the user was in before the click
    void Release()
    {
        if (!activatable)
            return;
        var back = previous;
        activatable = false;
        previous = IntPtr.Zero;
        SetActivatable(false);
        if (Native.IsWindow(back))
            Native.SetForegroundWindow(back);
    }

    void BoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            e.SuppressKeyPress = true;
            var question = box.Text.Trim();
            if (question.Length == 0)
                return;
            box.Clear();
            Asked?.Invoke(question);
        }
        else if (e.KeyCode == Keys.Escape)
        {
            e.SuppressKeyPress = true;
            if (box.Text.Length > 0)
                box.Clear();
            else
                CloseClicked?.Invoke();
        }
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        if (activatable)
        {
            activatable = false;
            previous = IntPtr.Zero;
            SetActivatable(false);
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        Release();
        base.OnFormClosing(e);
    }

    void CopyText()
    {
        copy.Text = TrayApp.Copy(text) ? "Copied" : "Failed";
        reset.Stop();
        reset.Start();
    }

    // copy is what the Copy button takes when the shown text is more than the answer
    public void SetText(string text, string? copy = null, bool ask = false)
    {
        this.text = copy ?? text;
        this.ask = ask && !plain;
        SetReading(false);
        ResetFit();
        Render(text);
    }

    public void Busy(string text)
    {
        Release();
        ask = false;
        SetReading(true);
        ResetFit();
        Render(text);
    }

    Font TextFont => fit ?? font;

    Font LargestFit()
    {
        return new Font("Segoe UI", (float)(72 * scale), GraphicsUnit.Pixel);
    }

    void ResetFit()
    {
        if (fit == null)
            return;
        var next = LargestFit();
        // the Font setter ignores an equal font, so swapping would leave the label on a disposed one
        if (next.Equals(fit))
        {
            next.Dispose();
            return;
        }
        var old = fit;
        fit = next;
        label.Font = fit;
        old.Dispose();
    }

    // shrinks only, so text streaming in doesn't make the size jump back up
    void Shrink(string text, int width, int height)
    {
        float floor = (float)(12 * scale);
        while (fit!.Size > floor && Measure(text, width) > height)
        {
            var old = fit;
            fit = new Font("Segoe UI", Math.Max(floor, old.Size * 0.9f), GraphicsUnit.Pixel);
            label.Font = fit;
            old.Dispose();
        }
    }

    Rectangle? Drawn()
    {
        if (area == null)
            return null;
        var b = screen.Bounds;
        int w = Math.Min(b.Width, Math.Max((int)(area[2] * b.Width), (int)(160 * scale)));
        int h = Math.Min(b.Height, Math.Max((int)(area[3] * b.Height), (int)(60 * scale)));
        return new Rectangle(Math.Min(b.X + (int)(area[0] * b.Width), b.Right - w), Math.Min(b.Y + (int)(area[1] * b.Height), b.Bottom - h), w, h);
    }

    // partial text: relayout at most every 50 ms, and Copy stays off until SetText
    public void Stream(string partial)
    {
        if (Environment.TickCount64 - lastLayout >= 50)
        {
            Render(partial);
            return;
        }
        pending = partial;
        flush.Start();
    }

    void Render(string text)
    {
        flush.Stop();
        pending = null;
        lastLayout = Environment.TickCount64;
        var scroll = panel.VerticalScroll;
        bool follow = scroll.Value + panel.ClientSize.Height >= scroll.Maximum - 2;
        // the monitor may have been unplugged or rescaled since the popup was created
        screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == screen.DeviceName) ?? Screen.FromHandle(Handle);
        double now = Native.ScaleFor(screen);
        if (now != scale)
        {
            scale = now;
            var old = font;
            font = NewFont();
            copy.Font = font;
            close.Font = font;
            box.Font = font;
            old.Dispose();
            ResetFit();
            label.Font = TextFont;
        }
        Fit(text);
        EnsureBounds();
        if (follow)
            panel.AutoScrollPosition = new Point(0, panel.DisplayRectangle.Height);
    }

    Font NewFont()
    {
        return new Font("Segoe UI", (float)(15 * Unit), GraphicsUnit.Pixel);
    }

    // the centered popup is meant to be read at a glance, so everything in it is larger
    double Unit => scale * (centered ? 1.8 : 1);

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
        var drawn = Drawn();
        int margin = (int)(16 * scale), pad = (int)(14 * Unit);
        int width = drawn?.Width ?? Math.Min((int)(420 * Unit), wa.Width - 2 * margin);
        int maxHeight = drawn?.Height ?? (int)(wa.Height * (centered ? 0.85 : 0.6));
        int inner = width - 2 * pad;

        int barHeight = plain ? 0 : (int)(28 * Unit);
        int inputHeight = ask ? (int)(38 * Unit) : 0;
        int chrome = 2 * pad + barHeight + inputHeight;
        if (drawn != null)
            Shrink(text, inner, maxHeight - chrome);
        int textHeight = Measure(text, inner);
        int height = drawn?.Height ?? textHeight + chrome;
        if (textHeight + chrome > maxHeight)
        {
            inner -= (int)(20 * scale);
            textHeight = Measure(text, inner);
            height = maxHeight;
        }

        bar.Height = barHeight;
        inputBar.Visible = ask;
        inputBar.Height = inputHeight;
        box.SetBounds(pad, (inputHeight - box.Height) / 2, width - 2 * pad, box.Height);
        int closeWidth = (int)(30 * Unit), copyWidth = (int)(90 * Unit);
        close.SetBounds(width - pad - closeWidth, 0, closeWidth, barHeight);
        copy.SetBounds(close.Left - copyWidth, 0, copyWidth, barHeight);

        label.Text = text;
        label.SetBounds(pad, pad, inner, textHeight);
        panel.AutoScrollMinSize = new Size(0, textHeight + 2 * pad);

        var b = screen.Bounds;
        target = drawn
            ?? (centered
                ? new Rectangle(b.X + (b.Width - width) / 2, b.Y + (b.Height - height) / 2, width, height)
                : new Rectangle(wa.Right - width - margin, wa.Bottom - height - margin, width, height));
    }

    int Measure(string text, int width)
    {
        var flags = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl;
        return TextRenderer.MeasureText(text, TextFont, new Size(width, int.MaxValue), flags).Height + 2;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            reset.Dispose();
            flush.Dispose();
            font.Dispose();
            fit?.Dispose();
        }
        base.Dispose(disposing);
    }
}
