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
    IntPtr previous;

    public event Action? CloseClicked;
    public event Action<string>? Asked;

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
        this.ask = ask;
        SetReading(false);
        Render(text);
    }

    public void Busy(string text)
    {
        Release();
        ask = false;
        SetReading(true);
        Render(text);
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
            label.Font = font;
            copy.Font = font;
            close.Font = font;
            box.Font = font;
            old.Dispose();
        }
        Fit(text);
        EnsureBounds();
        if (follow)
            panel.AutoScrollPosition = new Point(0, panel.DisplayRectangle.Height);
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
        int inputHeight = ask ? (int)(38 * scale) : 0;
        int height = textHeight + 2 * pad + barHeight + inputHeight;
        if (height > maxHeight)
        {
            inner -= (int)(20 * scale);
            textHeight = Measure(text, inner);
            height = maxHeight;
        }

        bar.Height = barHeight;
        inputBar.Visible = ask;
        inputBar.Height = inputHeight;
        box.SetBounds(pad, (inputHeight - box.Height) / 2, width - 2 * pad, box.Height);
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
            flush.Dispose();
            font.Dispose();
        }
        base.Dispose(disposing);
    }
}
