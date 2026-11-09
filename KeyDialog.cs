namespace ScreenCopy;

enum KeyJob { Read, Close }

class KeyDialog : Form
{
    public const Keys Win = (Keys)0x80000;

    readonly KeyJob job;
    readonly Keys other;
    readonly Func<Keys, bool> register;
    readonly Label status;
    readonly System.Windows.Forms.Timer tick = new() { Interval = 100 };
    KeyHook? hook;
    long closeAt;

    public Keys? Chosen { get; private set; }

    public KeyDialog(Screen screen, KeyJob job, Keys other, Func<Keys, bool> register)
    {
        this.job = job;
        this.other = other;
        this.register = register;
        double scale = Native.ScaleFor(screen);
        int Px(int v) => (int)Math.Round(v * scale);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        Text = job == KeyJob.Read ? "Read key" : "Close key";
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(Px(380), Px(170));
        var area = screen.WorkingArea;
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);

        Controls.Add(new Label
        {
            Text = "Press the key for: " + (job == KeyJob.Read ? "Read the screen" : "Close the answer"),
            Font = new Font("Segoe UI", Px(16), GraphicsUnit.Pixel),
            AutoSize = false,
            Bounds = new Rectangle(Px(16), Px(14), Px(348), Px(26)),
        });
        Controls.Add(new Label
        {
            Text = "Fn by itself doesn't reach Windows on most laptops.",
            Font = new Font("Segoe UI", Px(12), GraphicsUnit.Pixel),
            ForeColor = SystemColors.GrayText,
            AutoSize = false,
            Bounds = new Rectangle(Px(16), Px(44), Px(348), Px(20)),
        });
        status = new Label
        {
            Font = new Font("Segoe UI", Px(12), GraphicsUnit.Pixel),
            ForeColor = Color.Firebrick,
            AutoSize = false,
            Bounds = new Rectangle(Px(16), Px(70), Px(348), Px(52)),
        };
        Controls.Add(status);
        var cancel = new Button
        {
            Text = "Cancel",
            Font = new Font("Segoe UI", Px(12), GraphicsUnit.Pixel),
            TabStop = false,
            Bounds = new Rectangle(Px(264), Px(128), Px(100), Px(30)),
        };
        cancel.Click += (_, _) =>
        {
            Chosen = null;
            Close();
        };
        Controls.Add(cancel);
    }

    public static string KeyName(Keys keys)
    {
        switch (keys)
        {
            case Keys.LControlKey: return "Left Ctrl";
            case Keys.RControlKey: return "Right Ctrl";
            case Keys.LShiftKey: return "Left Shift";
            case Keys.RShiftKey: return "Right Shift";
            case Keys.LMenu: return "Left Alt";
            case Keys.RMenu: return "Right Alt";
        }
        string prefix = (keys & Win) != 0 ? "Win+" : "";
        var rest = keys & ~Win;
        var code = rest & Keys.KeyCode;
        if (!Enum.IsDefined(code))
        {
            if ((rest & Keys.Control) != 0) prefix += "Ctrl+";
            if ((rest & Keys.Shift) != 0) prefix += "Shift+";
            if ((rest & Keys.Alt) != 0) prefix += "Alt+";
            return prefix + $"Key 0x{(int)code:X2}";
        }
        return prefix + (new KeysConverter().ConvertToInvariantString(rest) ?? rest.ToString());
    }

    public static bool IsValid(Keys keys)
    {
        return (keys & Keys.KeyCode) != Keys.None;
    }

    public static bool IsLone(Keys keys)
    {
        return keys is Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey
            or Keys.LMenu or Keys.RMenu;
    }

    public static bool IsPrintScreen(Keys keys)
    {
        return (keys & Keys.KeyCode) == Keys.PrintScreen;
    }

    // Windows 11 hands Print Screen to the Snipping Tool, so a registered hotkey never fires
    public static bool UsesHook(Keys keys)
    {
        return IsLone(keys) || IsPrintScreen(keys);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        hook = new KeyHook { Swallow = () => Native.GetForegroundWindow() == Handle };
        hook.Combo += Choose;
        hook.Lone += (key, _) => Choose(key);
        tick.Tick += (_, _) =>
        {
            hook?.Poll();
            if (closeAt != 0 && Environment.TickCount64 >= closeAt)
                Close();
        };
        tick.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        tick.Dispose();
        hook?.Dispose();
        hook = null;
        base.OnFormClosed(e);
    }

    void Choose(Keys keys)
    {
        if (Chosen != null)
            return;
        if (keys is Keys.LWin or Keys.RWin)
        {
            status.Text = "The Windows key alone also opens Start. Add another key to it.";
        }
        else if (keys == other)
        {
            status.Text = $"{KeyName(keys)} is already the {(job == KeyJob.Read ? "Close" : "Read")} key.";
        }
        else if (!register(keys))
        {
            status.Text = $"{KeyName(keys)} can't be used. Windows or another program holds it.";
        }
        else
        {
            Chosen = keys;
            // keep swallowing until the keys are up, or their repeats leak out
            if (hook!.AnyHeld)
            {
                hook.Released += Close;
                closeAt = Environment.TickCount64 + 1000;
            }
            else
            {
                Close();
            }
        }
    }
}
