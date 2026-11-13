using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Glance;

class TrayApp : ApplicationContext
{
    const int ReadId = 1, CloseId = 2, RegionId = 3, HideId = 4;
    const string AllScreens = "all";
    const string ActiveWindow = "window";

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
    ConnectDialog? connectDialog;
    OrderDialog? orderDialog;
    ModesDialog? modesDialog;
    SettingsDialog? settingsDialog;
    KeyHook? keyHook;
    readonly Native.WinEventProc focusProc;
    readonly IntPtr focusHook;
    IntPtr lastWindow = Native.GetForegroundWindow();
    readonly List<string> history;
    Conversation? chat;
    string thread = "", shown = "";
    string? updateVersion, updateUrl;

    public TrayApp()
    {
        history = settings.KeepHistory ? Settings.LoadHistory() : new();
        // the desktop is kept too, so a click on it still means "read the screen";
        // whether a window can be read is only checked at read time, since a new window
        // can be cloaked or still minimized when it gets the focus
        focusProc = (_, _, hwnd, _, _, _, _) =>
        {
            if (hwnd != IntPtr.Zero && !Native.PassingFocus(hwnd))
                lastWindow = hwnd;
        };
        focusHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, focusProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
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
            else if (id == HideId)
                HideAnswer();
            else
                CloseAnswer();
        };
        Bind();
        var stored = StartValue();
        if (stored != null && stored != StartCommand)
            SetStartWithWindows(true);
        _ = CheckForUpdate();
    }

    public void Notify(string text, ToolTipIcon icon)
    {
        if (tray.Visible)
            tray.ShowBalloonTip(5000, "Glance", text, icon);
    }

    public void ShowRunning()
    {
        Notify(string.Format(Strings.AlreadyRunning, KeyDialog.KeyName(settings.Hotkey)), ToolTipIcon.Info);
    }

    async Task CheckForUpdate()
    {
        if (!settings.CheckUpdates || DateTime.UtcNow - settings.LastUpdateCheck < TimeSpan.FromDays(1))
            return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/aytutuna/Glance/releases/latest");
            req.Headers.UserAgent.ParseAdd("Glance");
            using var resp = await Api.Http.SendAsync(req, cts.Token);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(cts.Token));
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            string url = doc.RootElement.GetProperty("html_url").GetString() ?? "";
            settings.LastUpdateCheck = DateTime.UtcNow;
            settings.Save();
            if (Version.TryParse(tag.TrimStart('v'), out var latest)
                && latest > Assembly.GetExecutingAssembly().GetName().Version
                && url.StartsWith("https://github.com/aytutuna/Glance/"))
            {
                updateVersion = latest.ToString();
                updateUrl = url;
                Notify(string.Format(Strings.UpdateReady, updateVersion), ToolTipIcon.Info);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or KeyNotFoundException)
        {
            Log.Write("update check", ex);
        }
    }

    static string StartCommand => $"\"{Environment.ProcessPath}\"";

    static RegistryKey? RunKey(bool write)
    {
        return Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", write);
    }

    internal static string? StartValue()
    {
        using var key = RunKey(false);
        return key?.GetValue("Glance") as string;
    }

    internal bool SetStartWithWindows(bool on)
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
            tray.ShowBalloonTip(5000, "Glance", Strings.StartupFailed, ToolTipIcon.Warning);
            return false;
        }
    }

    void UpdateTooltip()
    {
        tray.Text = string.Format(Strings.Tooltip, KeyDialog.KeyName(settings.Hotkey), KeyDialog.KeyName(settings.RegionKey), KeyDialog.KeyName(settings.CloseKey));
    }

    void Fire(Keys key)
    {
        if (key == settings.Hotkey)
            Trigger(0);
        else if (key == settings.RegionKey)
            Trigger(0, true);
        else if (key == settings.CloseKey)
            CloseAnswer();
        else if (key == settings.HideKey)
            HideAnswer();
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
            failed.Add(string.Format(Strings.KeyReadsScreen, KeyDialog.KeyName(settings.Hotkey)));
        if (!Register(RegionId, settings.RegionKey))
            failed.Add(string.Format(Strings.KeyReadsArea, KeyDialog.KeyName(settings.RegionKey)));
        if (!Register(CloseId, settings.CloseKey))
            failed.Add(string.Format(Strings.KeyClosesAnswer, KeyDialog.KeyName(settings.CloseKey)));
        if (!Register(HideId, settings.HideKey))
            failed.Add(string.Format(Strings.KeyHidesAnswer, KeyDialog.KeyName(settings.HideKey)));

        var all = new[] { settings.Hotkey, settings.RegionKey, settings.CloseKey, settings.HideKey };
        if (all.Any(KeyDialog.UsesHook))
        {
            keyHook = new KeyHook { Bound = keys => all.Contains(keys) };
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
            tray.ShowBalloonTip(5000, "Glance", string.Format(Strings.KeysFailed, string.Join(Strings.ListJoin, failed)), ToolTipIcon.Warning);
    }

    void Unbind()
    {
        hotkeyWindow.UnregisterAll();
        keyHook?.Dispose();
        keyHook = null;
    }

    internal void ChangeKey(KeyJob job)
    {
        if (keyDialog != null)
        {
            keyDialog.Activate();
            return;
        }
        Unbind();
        int id = job switch { KeyJob.Read => ReadId, KeyJob.Close => CloseId, KeyJob.Hide => HideId, _ => RegionId };
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
                else if (job == KeyJob.Hide)
                    settings.HideKey = chosen;
                else
                    settings.CloseKey = chosen;
                bool saved = settings.Save();
                UpdateTooltip();
                if (!saved)
                    WarnUnsaved();
                else if (!KeyDialog.UsesHook(chosen) && (chosen & KeyDialog.Win) == 0)
                    tray.ShowBalloonTip(5000, "Glance", string.Format(Strings.KeyOnlyHere, KeyDialog.KeyName(chosen), KeyDialog.JobName(job)), ToolTipIcon.Info);
            }
            Bind();
            dialog.Dispose();
        };
        dialog.Show();
        dialog.Activate();
    }

    internal Keys KeyOf(KeyJob job)
    {
        return job switch { KeyJob.Read => settings.Hotkey, KeyJob.Close => settings.CloseKey, KeyJob.Hide => settings.HideKey, _ => settings.RegionKey };
    }

    // stops any answer and leaves the answer screen up, black and empty
    void HideAnswer()
    {
        current?.Cancel();
        current = null;
        SetBusy(false);
        chat = null;
        thread = shown = "";
        if (popup != null && !popup.IsDisposed)
            popup.SetText("");
        else if (settings.PlainAnswer)
            popup = NewPopup("", false);
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
        if (updateUrl != null)
        {
            var url = updateUrl;
            menu.Items.Add(string.Format(Strings.UpdateTo, updateVersion), null, (_, _) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }));
            menu.Items.Add(new ToolStripSeparator());
        }
        menu.Items.Add(Strings.MenuReadNow, null, (_, _) => Trigger(300));
        menu.Items.Add(Strings.ReadArea, null, (_, _) => Trigger(300, true));
        menu.Items.Add(Strings.MenuHideText, null, (_, _) => HideAnswer());
        menu.Items.Add(ScreenMenu(Strings.ReadFrom, settings.ReadDevice, d => settings.ReadDevice = d, true));
        menu.Items.Add(ScreenMenu(Strings.ShowAnswerOn, settings.ShowDevice, d => settings.ShowDevice = d));
        menu.Items.Add(PositionMenu());
        menu.Items.Add(new ToolStripMenuItem(Strings.PlainAnswer, null, (_, _) =>
        {
            settings.PlainAnswer = !settings.PlainAnswer;
            SaveLook();
        })
        { Checked = settings.PlainAnswer });
        menu.Items.Add(OpacityMenu());
        menu.Items.Add(AiMenu());
        if (settings.Chain().Contains(Settings.Claude))
            menu.Items.Add(ModelMenu());
        menu.Items.Add(ModeMenu());
        menu.Items.Add(TranslateMenu());
        menu.Items.Add(Strings.IdentifyScreens, null, (_, _) => Identify());
        foreach (var job in new[] { KeyJob.Read, KeyJob.Region, KeyJob.Close, KeyJob.Hide })
            menu.Items.Add(string.Format(Strings.KeyMenu, KeyDialog.JobName(job), KeyDialog.KeyName(KeyOf(job))), null, (_, _) => ChangeKey(job));
        bool autostart = StartValue() != null;
        menu.Items.Add(new ToolStripMenuItem(Strings.StartWithWindows, null, (_, _) => SetStartWithWindows(!autostart))
        {
            Checked = autostart,
        });
        menu.Items.Add(new ToolStripMenuItem(Strings.CheckUpdates, null, (_, _) => ToggleUpdates())
        { Checked = settings.CheckUpdates });
        menu.Items.Add(new ToolStripSeparator());
        var recent = new ToolStripMenuItem(Strings.RecentAnswers);
        foreach (var answer in history)
            recent.DropDownItems.Add(Preview(answer), null, (_, _) => Copy(answer));
        if (history.Count > 0)
            recent.DropDownItems.Add(new ToolStripSeparator());
        recent.DropDownItems.Add(new ToolStripMenuItem(Strings.KeepAnswers, null, (_, _) => ToggleHistory())
        { Checked = settings.KeepHistory });
        recent.DropDownItems.Add(new ToolStripMenuItem(Strings.ClearRecent, null, (_, _) =>
        {
            history.Clear();
            if (settings.KeepHistory)
                Settings.SaveHistory(history);
        })
        { Enabled = history.Count > 0 });
        menu.Items.Add(recent);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(Strings.MenuSettings, null, (_, _) => OpenSettings());
        menu.Items.Add(Strings.MenuExit, null, (_, _) => Quit());
    }

    ToolStripMenuItem PositionMenu()
    {
        var item = new ToolStripMenuItem(Strings.AnswerPosition);
        foreach (var (name, center) in new[] { (Strings.BottomRight, false), (Strings.CenterOfScreen, true) })
        {
            item.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
            {
                settings.CenterAnswer = center;
                settings.UseArea = false;
                SaveLook();
            })
            { Checked = !settings.UseArea && settings.CenterAnswer == center });
        }
        if (settings.AnswerArea != null)
        {
            item.DropDownItems.Add(new ToolStripMenuItem(Strings.DrawnArea, null, (_, _) =>
            {
                settings.UseArea = true;
                SaveLook();
            })
            { Checked = settings.UseArea });
        }
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(Strings.DrawAnswerArea, null, (_, _) => DrawArea());
        return item;
    }

    ToolStripMenuItem OpacityMenu()
    {
        var item = new ToolStripMenuItem(Strings.TextOpacity);
        foreach (int percent in new[] { 100, 80, 60, 40, 20 })
        {
            item.DropDownItems.Add(new ToolStripMenuItem($"{percent}%", null, (_, _) => SetOpacity(percent))
            { Checked = settings.TextOpacity == percent });
        }
        return item;
    }

    internal void SetOpacity(int percent)
    {
        settings.TextOpacity = percent;
        if (popup != null && !popup.IsDisposed)
            popup.SetTextOpacity(percent);
        SaveLook();
    }

    internal void SaveLook()
    {
        if (!settings.Save())
            WarnUnsaved();
    }

    internal void DrawArea()
    {
        if (keyDialog != null || picker != null)
        {
            ((Form?)keyDialog ?? picker)!.Activate();
            return;
        }
        Start(async ct =>
        {
            ClosePopup();
            CloseLabels();
            settingsDialog?.Hide();
            Bitmap shot;
            var screen = Resolve(settings.ShowDevice);
            try
            {
                // the tray menu and the settings window have to be gone before the screen is captured
                await Task.Delay(300, ct);
                shot = await Task.Run(() => Grabber.Grab(screen.Bounds), ct);
            }
            finally
            {
                settingsDialog?.Show();
            }
            using var kept = shot;
            ct.ThrowIfCancellationRequested();
            var drawn = await Pick(screen, shot, Strings.DragAnswerArea, ct);
            if (drawn is not Rectangle r)
                return;
            double w = shot.Width, h = shot.Height;
            settings.AnswerArea = new[] { r.X / w, r.Y / h, r.Width / w, r.Height / h };
            settings.UseArea = true;
            SaveLook();
            popup = NewPopup(Strings.AnswersHere, false);
        });
    }

    ToolStripMenuItem AiMenu()
    {
        var item = new ToolStripMenuItem(Strings.AI);
        var chain = settings.Chain();
        var names = settings.Options().ToDictionary(o => o.Entry, o => o.Label);
        for (int i = 0; i < chain.Count; i++)
        {
            string entry = chain[i];
            // picking one moves it to the top; the rest keep their order behind it
            item.DropDownItems.Add(new ToolStripMenuItem($"{i + 1}. {names[entry]}", null, (_, _) =>
            {
                settings.Order = chain.Where(e => e != entry).Prepend(entry).ToList();
                SaveLook();
            })
            { Checked = i == 0 });
        }
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(Strings.ChangeOrder, null, (_, _) => ChangeOrder());
        item.DropDownItems.Add(Strings.ConnectAi, null, (_, _) => Connect());
        return item;
    }

    internal void ChangeOrder()
    {
        if (orderDialog != null)
        {
            orderDialog.Activate();
            return;
        }
        var dialog = orderDialog = new OrderDialog(Resolve(null), settings);
        dialog.FormClosed += (_, _) =>
        {
            orderDialog = null;
            if (dialog.Changed)
                SaveLook();
            dialog.Dispose();
        };
        dialog.Show();
        dialog.Activate();
    }

    internal void EditModes()
    {
        if (modesDialog != null)
        {
            modesDialog.Activate();
            return;
        }
        var dialog = modesDialog = new ModesDialog(Resolve(null), settings);
        dialog.FormClosed += (_, _) =>
        {
            modesDialog = null;
            if (dialog.Changed)
                SaveLook();
            dialog.Dispose();
        };
        dialog.Show();
        dialog.Activate();
    }

    internal void Connect()
    {
        if (connectDialog != null)
        {
            connectDialog.Activate();
            return;
        }
        var dialog = connectDialog = new ConnectDialog(Resolve(null), settings);
        dialog.FormClosed += (_, _) =>
        {
            connectDialog = null;
            if (dialog.Changed)
            {
                // an open thread holds the old keys, and one may have just been removed
                CloseAnswer();
                if (!settings.Save())
                    WarnUnsaved();
            }
            dialog.Dispose();
        };
        dialog.Show();
        dialog.Activate();
    }

    ToolStripMenuItem ModelMenu()
    {
        var item = new ToolStripMenuItem(Strings.ClaudeCliModel);
        foreach (var (id, name) in Settings.Models)
        {
            item.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
            {
                settings.Model = id;
                if (!settings.Save())
                    WarnUnsaved();
            })
            { Checked = settings.Model == id });
        }
        return item;
    }

    ToolStripMenuItem ModeMenu()
    {
        var item = new ToolStripMenuItem(Strings.Mode);
        foreach (var (id, name) in settings.AllModes())
        {
            item.DropDownItems.Add(new ToolStripMenuItem(name.Replace("&", "&&"), null, (_, _) =>
            {
                settings.Mode = id;
                if (!settings.Save())
                    WarnUnsaved();
            })
            { Checked = settings.Mode == id });
        }
        item.DropDownItems.Add(new ToolStripSeparator());
        item.DropDownItems.Add(Strings.EditModes, null, (_, _) => EditModes());
        return item;
    }

    internal static List<(string Value, string Name)> TranslateChoices()
    {
        return Settings.Languages.Select(l => (l, l)).Prepend(("", string.Format(Strings.WindowsLanguage, System.Globalization.CultureInfo.CurrentUICulture.NativeName))).ToList();
    }

    ToolStripMenuItem TranslateMenu()
    {
        var item = new ToolStripMenuItem(Strings.TranslateTo);
        foreach (var (value, name) in TranslateChoices())
        {
            item.DropDownItems.Add(new ToolStripMenuItem(name, null, (_, _) =>
            {
                settings.TranslateTo = value;
                if (!settings.Save())
                    WarnUnsaved();
            })
            { Checked = settings.TranslateTo == value });
        }
        return item;
    }

    internal void ToggleHistory()
    {
        settings.KeepHistory = !settings.KeepHistory;
        if (settings.KeepHistory)
            Settings.SaveHistory(history);
        else
            Settings.DeleteHistory();
        SaveLook();
    }

    internal void ToggleUpdates()
    {
        settings.CheckUpdates = !settings.CheckUpdates;
        SaveLook();
    }

    static string Preview(string answer)
    {
        string line = Regex.Replace(answer, @"\s+", " ");
        if (line.Length > 60)
            line = line[..57] + "...";
        return line.Replace("&", "&&");
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

    ToolStripMenuItem ScreenMenu(string title, string? selected, Action<string?> choose, bool withAll = false)
    {
        var item = new ToolStripMenuItem(title);
        var choices = ScreenChoices(withAll);
        // a screen that is gone counts as the screen with the mouse
        if (!choices.Any(c => c.Device == selected))
            selected = null;
        foreach (var (device, text) in choices)
            item.DropDownItems.Add(ScreenItem(text, device == selected, () => choose(device)));
        return item;
    }

    internal static List<(string? Device, string Text)> ScreenChoices(bool withAll)
    {
        var screens = Screen.AllScreens;
        var choices = new List<(string?, string)> { (null, Strings.MouseScreen) };
        if (withAll)
        {
            choices.Add((AllScreens, Strings.AllScreens));
            choices.Add((ActiveWindow, Strings.ActiveWindow));
        }
        for (int i = 0; i < screens.Length; i++)
            choices.Add((screens[i].DeviceName, Describe(screens[i], i)));
        return choices;
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
        tray.ShowBalloonTip(5000, "Glance", Strings.SettingsNotSaved, ToolTipIcon.Warning);
    }

    static string Describe(Screen s, int index)
    {
        return string.Format(s.Primary ? Strings.ScreenPrimary : Strings.ScreenPlain, index + 1, s.Bounds.Width, s.Bounds.Height);
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
        Start(ct => Read(delayMs, area, ct));
    }

    void Start(Func<CancellationToken, Task> job)
    {
        current?.Cancel();
        var cts = current = new CancellationTokenSource();
        _ = Run(cts, job);
    }

    async Task Run(CancellationTokenSource cts, Func<CancellationToken, Task> job)
    {
        var ct = cts.Token;
        SetBusy(true);
        try
        {
            await job(ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log.Write("job failed", ex);
            if (!ct.IsCancellationRequested)
                ShowText(thread + ex.Message, ex.Message, thread.Length > 0);
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

    async Task Read(int delayMs, bool area, CancellationToken ct)
    {
        // taken first: closing the popup or waiting can change the focus. A tray click or
        // the tray menu moves the focus to the taskbar or to Glance, so the window used last is read then
        var window = settings.ReadDevice == ActiveWindow && !area
            ? Native.ReadableWindow(Native.GetForegroundWindow()) ?? Native.ReadableWindow(lastWindow)
            : null;
        var screen = window is { } w ? Screen.FromRectangle(w) : Resolve(settings.ReadDevice);
        // an area is drawn on one screen, so it falls back to the screen with the mouse;
        // a window that can't be read does too
        bool all = settings.ReadDevice == AllScreens && !area;
        var open = popup is { IsDisposed: false } shownPopup ? shownPopup : null;
        // the pointer can rest on the answer screen (a ScreenPad is also the touchpad);
        // reading that screen would only capture the answer, so read the main one instead
        if (window == null && !all && open != null && open.Device == screen.DeviceName && settings.ReadDevice != open.Device)
        {
            var others = Screen.AllScreens.Where(s => s.DeviceName != open.Device).ToList();
            screen = others.FirstOrDefault(s => s.Primary) ?? others.FirstOrDefault() ?? screen;
        }
        // an answer on another screen stays up while the next one is read;
        // on the screen or window being read it would end up in the screenshot
        bool clear = window is { } wb ? open?.Bounds.IntersectsWith(wb) == false : open?.Device != screen.DeviceName;
        bool keep = !all && open != null && clear && open.Look == AnswerPopup.LookOf(settings);
        if (!keep)
            ClosePopup();
        CloseLabels();
        var provider = Ai.Create(settings);
        string model = settings.ActiveModel();
        await Task.Delay(Native.CanExcludeFromCapture ? delayMs : delayMs + 150, ct);
        var bounds = window ?? (all ? SystemInformation.VirtualScreen : screen.Bounds);
        using var shot = await Task.Run(() => Grabber.Grab(bounds), ct);
        ct.ThrowIfCancellationRequested();
        Rectangle? crop = null;
        if (area)
        {
            crop = await Pick(screen, shot, Strings.DragReadArea, ct);
            if (crop == null)
                return;
        }
        var jpeg = await Task.Run(() => Grabber.Encode(shot, crop, all ? Grabber.WideEdge : Grabber.MaxEdge), ct);
        ct.ThrowIfCancellationRequested();

        AnswerPopup mine;
        if (keep && popup is { IsDisposed: false } kept)
        {
            chat = null;
            thread = shown = "";
            kept.Busy(Strings.Reading);
            mine = kept;
        }
        else
        {
            ClosePopup();
            mine = popup = NewPopup(Strings.Reading, true);
        }
        await Reply(new Conversation(jpeg, model, settings.Mode, Prompts.Instruction(settings, settings.Mode), provider), mine, ct);
    }

    async Task Reply(Conversation c, AnswerPopup mine, CancellationToken ct)
    {
        string answer = Clean(await c.Provider.AskAsync(c, partial =>
        {
            partial = Clean(partial);
            if (partial.Length > 0 && !ct.IsCancellationRequested && popup == mine)
                mine.Stream(thread + partial);
        }, ct));
        ct.ThrowIfCancellationRequested();
        if (answer.Length == 0)
            throw new AiException(Strings.AnswerEmpty);
        c.Complete(answer);
        chat = c;
        history.Insert(0, answer);
        if (history.Count > 10)
            history.RemoveAt(10);
        if (settings.KeepHistory)
            Settings.SaveHistory(history);
        ShowText(thread + answer, answer, true);
    }

    void Follow(string question)
    {
        var c = chat;
        var mine = popup;
        if (c == null || mine == null)
            return;
        thread = shown + "\n\n" + Strings.QuestionPrefix + question + "\n\n";
        c.Ask(question);
        mine.Busy(thread + Strings.Reading);
        Start(ct => Reply(c, mine, ct));
    }

    // markers can be split across chunks, so this runs on the whole text
    static string Clean(string text)
    {
        return text.Replace("**", "").Trim();
    }

    async Task<Rectangle?> Pick(Screen screen, Bitmap shot, string hint, CancellationToken ct)
    {
        var done = new TaskCompletionSource<Rectangle?>();
        var form = picker = new AreaPicker(screen, shot, hint);
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

    void ShowText(string text, string? copy = null, bool ask = false)
    {
        shown = text;
        if (popup == null || popup.IsDisposed)
            popup = NewPopup(text, false);
        popup.SetText(text, copy, ask);
    }

    AnswerPopup NewPopup(string text, bool reading)
    {
        var made = new AnswerPopup(Resolve(settings.ShowDevice), text, reading, settings);
        made.CloseClicked += CloseAnswer;
        made.Asked += Follow;
        made.Show();
        return made;
    }

    void ClosePopup()
    {
        popup?.Close();
        popup = null;
        chat = null;
        thread = shown = "";
    }

    void SetBusy(bool busy)
    {
        tray.Icon = busy ? busyIcon : idleIcon;
    }

    void OpenSettings()
    {
        if (settingsDialog != null)
        {
            settingsDialog.Activate();
            return;
        }
        var dialog = settingsDialog = new SettingsDialog(Resolve(null), settings, this);
        dialog.FormClosed += (_, _) =>
        {
            settingsDialog = null;
            dialog.Dispose();
        };
        dialog.Show();
        dialog.Activate();
    }

    void Quit()
    {
        current?.Cancel();
        picker?.Close();
        ClosePopup();
        keyDialog?.Close();
        connectDialog?.Close();
        settingsDialog?.Close();
        keyHook?.Dispose();
        Native.UnhookWinEvent(focusHook);
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
            g.DrawString("G", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
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
