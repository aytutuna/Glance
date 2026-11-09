using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace Glance;

class TrayApp : ApplicationContext
{
    const int ReadId = 1, CloseId = 2, RegionId = 3;
    const string MouseScreen = "Screen with the mouse";

    readonly Settings settings = Settings.Load();
    readonly NotifyIcon tray = new();
    readonly ContextMenuStrip menu = new();
    readonly HotkeyWindow hotkeyWindow = new();
    readonly Icon idleIcon = MakeIcon(Color.FromArgb(0, 120, 212));
    readonly Icon busyIcon = MakeIcon(Color.FromArgb(232, 140, 0));

    CancellationTokenSource? current;
    readonly List<ScreenLabel> labels = new();
    AnswerPopup? popup;
    AreaPicker? picker;
    KeyDialog? keyDialog;
    KeyHook? keyHook;
    string? lastAnswer;

    public TrayApp()
    {
        menu.Opening += (_, e) =>
        {
            BuildMenu();
            e.Cancel = false;
        };
        tray.ContextMenuStrip = menu;
        tray.Icon = idleIcon;
        UpdateTooltip();
        tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                Trigger(300);
        };
        tray.Visible = true;

        hotkeyWindow.Pressed += id =>
        {
            if (id == ReadId)
                Trigger(0);
            else if (id == RegionId)
                Trigger(0, true);
            else
                CloseAnswer();
        };
        Bind();
        var stored = StartValue();
        if (stored != null && stored != StartCommand)
            SetStartWithWindows(true);
    }

    static string StartCommand => $"\"{Environment.ProcessPath}\"";

    static RegistryKey? RunKey(bool write)
    {
        return Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", write);
    }

    static string? StartValue()
    {
        using var key = RunKey(false);
        return key?.GetValue("Glance") as string;
    }

    bool SetStartWithWindows(bool on)
    {
        try
        {
            using var key = RunKey(true);
            if (key == null)
                return false;
            if (on)
                key.SetValue("Glance", StartCommand);
            else
                key.DeleteValue("Glance", false);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            tray.ShowBalloonTip(5000, "Glance", "Couldn't change the Windows startup setting.", ToolTipIcon.Warning);
            return false;
        }
    }

    void UpdateTooltip()
    {
        tray.Text = $"Glance (Read: {KeyDialog.KeyName(settings.Hotkey)}, Area: {KeyDialog.KeyName(settings.RegionKey)}, Close: {KeyDialog.KeyName(settings.CloseKey)})";
    }

    void Fire(Keys key)
    {
        if (key == settings.Hotkey)
            Trigger(0);
        else if (key == settings.RegionKey)
            Trigger(0, true);
        else if (key == settings.CloseKey)
            CloseAnswer();
    }

    bool Register(int id, Keys keys)
    {
        if (KeyDialog.UsesHook(keys))
            return true;
        uint modifiers = Native.MOD_NOREPEAT;
        if ((keys & Keys.Control) != 0)
            modifiers |= Native.MOD_CONTROL;
        if ((keys & Keys.Shift) != 0)
            modifiers |= Native.MOD_SHIFT;
        if ((keys & Keys.Alt) != 0)
            modifiers |= Native.MOD_ALT;
        if ((keys & KeyDialog.Win) != 0)
            modifiers |= Native.MOD_WIN;
        return hotkeyWindow.Register(id, modifiers, (uint)(keys & Keys.KeyCode));
    }

    void Bind()
    {
        Unbind();
        var failed = new List<string>();
        if (!Register(ReadId, settings.Hotkey))
            failed.Add($"{KeyDialog.KeyName(settings.Hotkey)} (reads the screen)");
        if (!Register(RegionId, settings.RegionKey))
            failed.Add($"{KeyDialog.KeyName(settings.RegionKey)} (reads an area)");
        if (!Register(CloseId, settings.CloseKey))
            failed.Add($"{KeyDialog.KeyName(settings.CloseKey)} (closes the answer)");

        if (KeyDialog.UsesHook(settings.Hotkey) || KeyDialog.UsesHook(settings.RegionKey) || KeyDialog.UsesHook(settings.CloseKey))
        {
            keyHook = new KeyHook { Bound = keys => keys == settings.Hotkey || keys == settings.RegionKey || keys == settings.CloseKey };
            keyHook.Lone += (key, ms) =>
            {
                if (ms < 500)
                    Fire(key);
            };
            keyHook.Combo += keys =>
            {
                if (KeyDialog.IsPrintScreen(keys))
                    Fire(keys);
            };
        }

        if (failed.Count > 0)
            tray.ShowBalloonTip(5000, "Glance", $"{string.Join(" and ", failed)} can't be used: Windows or another program holds it. Pick another key from the tray menu.", ToolTipIcon.Warning);
    }

    void Unbind()
    {
        hotkeyWindow.UnregisterAll();
        keyHook?.Dispose();
        keyHook = null;
    }

    void ChangeKey(KeyJob job)
    {
        if (keyDialog != null)
        {
            keyDialog.Activate();
            return;
        }
        Unbind();
        int id = job switch { KeyJob.Read => ReadId, KeyJob.Close => CloseId, _ => RegionId };
        var taken = new Dictionary<Keys, KeyJob>();
        foreach (var other in Enum.GetValues<KeyJob>())
        {
            if (other != job)
                taken[KeyOf(other)] = other;
        }
        var dialog = keyDialog = new KeyDialog(Resolve(null), job, taken, keys => Register(id, keys));
        dialog.FormClosed += (_, _) =>
        {
            keyDialog = null;
            if (dialog.Chosen is Keys chosen)
            {
                if (job == KeyJob.Read)
                    settings.Hotkey = chosen;
                else if (job == KeyJob.Region)
                    settings.RegionKey = chosen;
                else
                    settings.CloseKey = chosen;
                bool saved = settings.Save();
                UpdateTooltip();
                if (!saved)
                    WarnUnsaved();
                else if (!KeyDialog.UsesHook(chosen) && (chosen & KeyDialog.Win) == 0)
                    tray.ShowBalloonTip(5000, "Glance", $"{KeyDialog.KeyName(chosen)} now only works as the {KeyDialog.JobName(job)} key in every program. Change it from the tray menu to get it back.", ToolTipIcon.Info);
            }
            Bind();
            dialog.Dispose();
        };
        dialog.Show();
        dialog.Activate();
    }

    Keys KeyOf(KeyJob job)
    {
        return job switch { KeyJob.Read => settings.Hotkey, KeyJob.Close => settings.CloseKey, _ => settings.RegionKey };
    }

    void CloseAnswer()
    {
        current?.Cancel();
        current = null;
        ClosePopup();
        SetBusy(false);
    }

    void BuildMenu()
    {
        var old = menu.Items.Cast<ToolStripItem>().ToList();
        menu.Items.Clear();
        foreach (var item in old)
            item.Dispose();
        menu.Items.Add("Read screen now", null, (_, _) => Trigger(300));
        menu.Items.Add("Read an area", null, (_, _) => Trigger(300, true));
        menu.Items.Add(ScreenMenu("Read from", settings.ReadDevice, d => settings.ReadDevice = d));
        menu.Items.Add(ScreenMenu("Show answer on", settings.ShowDevice, d => settings.ShowDevice = d));
        menu.Items.Add("Identify screens", null, (_, _) => Identify());
        menu.Items.Add($"Read key ({KeyDialog.KeyName(settings.Hotkey)})...", null, (_, _) => ChangeKey(KeyJob.Read));
        menu.Items.Add($"Area key ({KeyDialog.KeyName(settings.RegionKey)})...", null, (_, _) => ChangeKey(KeyJob.Region));
        menu.Items.Add($"Close key ({KeyDialog.KeyName(settings.CloseKey)})...", null, (_, _) => ChangeKey(KeyJob.Close));
        bool autostart = StartValue() != null;
        menu.Items.Add(new ToolStripMenuItem("Start with Windows", null, (_, _) => SetStartWithWindows(!autostart))
        {
            Checked = autostart,
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Copy last answer", null, (_, _) => CopyLast())
        {
            Enabled = lastAnswer != null,
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Quit());
    }

    void CopyLast()
    {
        Copy(lastAnswer!);
    }

    public static bool Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    ToolStripMenuItem ScreenMenu(string title, string? selected, Action<string?> choose)
    {
        var item = new ToolStripMenuItem(title);
        var screens = Screen.AllScreens;
        bool known = selected != null && screens.Any(s => s.DeviceName == selected);
        item.DropDownItems.Add(ScreenItem(MouseScreen, !known, () => choose(null)));
        for (int i = 0; i < screens.Length; i++)
        {
            string name = screens[i].DeviceName;
            item.DropDownItems.Add(ScreenItem(Describe(screens[i], i), selected == name, () => choose(name)));
        }
        return item;
    }

    ToolStripMenuItem ScreenItem(string text, bool on, Action pick)
    {
        return new ToolStripMenuItem(text, null, (_, _) =>
        {
            pick();
            if (!settings.Save())
                WarnUnsaved();
        })
        { Checked = on };
    }

    void WarnUnsaved()
    {
        tray.ShowBalloonTip(5000, "Glance", "Couldn't save the settings file, so this choice won't survive a restart.", ToolTipIcon.Warning);
    }

    static string Describe(Screen s, int index)
    {
        string text = $"Screen {index + 1} ({s.Bounds.Width}x{s.Bounds.Height}";
        return s.Primary ? text + ", primary)" : text + ")";
    }

    static Screen Resolve(string? device)
    {
        if (device != null)
        {
            var found = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == device);
            if (found != null)
                return found;
        }
        return Screen.FromPoint(Cursor.Position);
    }

    void Identify()
    {
        CloseLabels();
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var label = new ScreenLabel(screens[i], i + 1);
            labels.Add(label);
            label.FormClosed += (_, _) => labels.Remove(label);
            label.Show();
        }
    }

    void CloseLabels()
    {
        foreach (var label in labels.ToList())
            label.Close();
    }

    void Trigger(int delayMs, bool area = false)
    {
        if (keyDialog != null)
        {
            keyDialog.Activate();
            return;
        }
        if (picker != null)
        {
            picker.Activate();
            return;
        }
        current?.Cancel();
        var cts = current = new CancellationTokenSource();
        _ = Read(delayMs, area, cts);
    }

    async Task Read(int delayMs, bool area, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        SetBusy(true);
        ClosePopup();
        CloseLabels();
        try
        {
            await Task.Delay(delayMs + 150, ct);
            var screen = Resolve(settings.ReadDevice);
            using var shot = await Task.Run(() => Grabber.Grab(screen), ct);
            ct.ThrowIfCancellationRequested();
            Rectangle? crop = null;
            if (area)
            {
                crop = await Pick(screen, shot, ct);
                if (crop == null)
                    return;
            }
            var jpeg = await Task.Run(() => Grabber.Encode(shot, crop), ct);
            ct.ThrowIfCancellationRequested();

            popup = NewPopup("Reading...", true);

            string answer = (await ClaudeCli.AskAsync(jpeg, ct)).Replace("**", "").Trim();
            ct.ThrowIfCancellationRequested();
            if (answer.Length == 0)
                throw new ClaudeException("Claude returned no answer.");
            lastAnswer = answer;
            ShowText(answer);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
                ShowText(ex.Message);
        }
        finally
        {
            if (current == cts)
            {
                SetBusy(false);
                current = null;
            }
            cts.Dispose();
        }
    }

    async Task<Rectangle?> Pick(Screen screen, Bitmap shot, CancellationToken ct)
    {
        var done = new TaskCompletionSource<Rectangle?>();
        var form = picker = new AreaPicker(screen, shot);
        form.FormClosed += (_, _) =>
        {
            picker = null;
            done.SetResult(form.Selection);
        };
        form.Show();
        using var reg = ct.Register(form.Close);
        var picked = await done.Task;
        form.Dispose();
        return picked;
    }

    void ShowText(string text)
    {
        if (popup == null || popup.IsDisposed)
        {
            popup = NewPopup(text, false);
        }
        else
        {
            popup.SetText(text);
        }
    }

    AnswerPopup NewPopup(string text, bool reading)
    {
        var shown = new AnswerPopup(Resolve(settings.ShowDevice), text, reading);
        shown.CloseClicked += CloseAnswer;
        shown.Show();
        return shown;
    }

    void ClosePopup()
    {
        popup?.Close();
        popup = null;
    }

    void SetBusy(bool busy)
    {
        tray.Icon = busy ? busyIcon : idleIcon;
    }

    void Quit()
    {
        current?.Cancel();
        picker?.Close();
        ClosePopup();
        keyDialog?.Close();
        keyHook?.Dispose();
        hotkeyWindow.Dispose();
        tray.Visible = false;
        tray.Dispose();
        idleIcon.Dispose();
        busyIcon.Dispose();
        ExitThread();
    }

    static Icon MakeIcon(Color fill)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var path = new GraphicsPath();
            const int d = 12;
            var r = new Rectangle(1, 1, 30, 30);
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);

            using var font = new Font("Segoe UI", 22, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("Q", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            Native.DestroyIcon(handle);
        }
    }

    class HotkeyWindow : NativeWindow, IDisposable
    {
        public event Action<int>? Pressed;
        readonly HashSet<int> registered = new();

        public HotkeyWindow()
        {
            CreateHandle(new CreateParams());
        }

        public bool Register(int id, uint modifiers, uint key)
        {
            Unregister(id);
            if (!Native.RegisterHotKey(Handle, id, modifiers, key))
                return false;
            registered.Add(id);
            return true;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
                Pressed?.Invoke((int)m.WParam);
            else
                base.WndProc(ref m);
        }

        public void Unregister(int id)
        {
            if (registered.Remove(id))
                Native.UnregisterHotKey(Handle, id);
        }

        public void UnregisterAll()
        {
            foreach (int id in registered.ToList())
                Unregister(id);
        }

        public void Dispose()
        {
            UnregisterAll();
            DestroyHandle();
        }
    }
}
