using System.Diagnostics;

namespace Glance;

class SettingsDialog : Form
{
    const int Wide = 270, Field = 146;

    static readonly int[] Opacities = { 100, 80, 60, 40, 20 };

    readonly Settings settings;
    readonly TrayApp app;
    readonly double scale;
    readonly Font font;
    readonly ComboBox readFrom, mode, translate, showOn, position, opacity, model;
    readonly CheckBox plain, history, autostart, updates;
    readonly Label order, modelLabel;
    readonly Dictionary<KeyJob, Label> keyNames = new();
    List<(string? Device, string Text)> readChoices = new(), showChoices = new();
    List<(string Id, string Name)> modes = new();
    List<(string Value, string Name)> languages = new();
    List<(string Name, bool Center, bool Area)> positions = new();

    public SettingsDialog(Screen screen, Settings settings, TrayApp app)
    {
        this.settings = settings;
        this.app = app;
        scale = Native.ScaleFor(screen);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        Text = Strings.SettingsTitle;
        StartPosition = FormStartPosition.Manual;
        font = new Font("Segoe UI", Px(13), GraphicsUnit.Pixel);
        Font = font;

        var reading = new TabPage(Strings.TabReading);
        var answer = new TabPage(Strings.TabAnswer);
        var keys = new TabPage(Strings.TabKeys);
        var ai = new TabPage(Strings.AI);
        var general = new TabPage(Strings.TabGeneral);

        readFrom = Combo(() =>
        {
            settings.ReadDevice = readChoices[readFrom!.SelectedIndex].Device;
            app.SaveLook();
        });
        mode = Combo(() =>
        {
            settings.Mode = modes[mode!.SelectedIndex].Id;
            app.SaveLook();
        });
        translate = Combo(() =>
        {
            settings.TranslateTo = languages[translate!.SelectedIndex].Value;
            app.SaveLook();
        });
        Row(reading, 0, Strings.ReadFrom, readFrom);
        Row(reading, 1, Strings.Mode, mode);
        Row(reading, 2, Strings.TranslateTo, translate);
        Add(reading, Button(Strings.EditModes, app.EditModes), Field, 12 + 3 * 34, 150);

        showOn = Combo(() =>
        {
            settings.ShowDevice = showChoices[showOn!.SelectedIndex].Device;
            app.SaveLook();
        });
        position = Combo(() =>
        {
            var (_, center, area) = positions[position!.SelectedIndex];
            if (area)
                settings.UseArea = true;
            else
            {
                settings.CenterAnswer = center;
                settings.UseArea = false;
            }
            app.SaveLook();
        });
        opacity = Combo(() => app.SetOpacity(Opacities[opacity!.SelectedIndex]));
        opacity.Items.AddRange(Opacities.Select(p => (object)$"{p}%").ToArray());
        plain = new CheckBox { Text = Strings.PlainAnswer };
        plain.Click += (_, _) =>
        {
            settings.PlainAnswer = plain.Checked;
            app.SaveLook();
        };
        Row(answer, 0, Strings.ShowAnswerOn, showOn);
        Row(answer, 1, Strings.AnswerPosition, position);
        Add(answer, Button(Strings.DrawAnswerArea, app.DrawArea), Field, 12 + 2 * 34, 150);
        Row(answer, 3, Strings.TextOpacity, opacity);
        Add(answer, plain, 12, 12 + 4 * 34, Wide);

        var jobs = new[] { KeyJob.Read, KeyJob.Region, KeyJob.Close, KeyJob.Hide };
        for (int i = 0; i < jobs.Length; i++)
        {
            var job = jobs[i];
            var name = keyNames[job] = new Label { AutoSize = false };
            Row(keys, i, string.Format(Strings.KeyTitle, KeyDialog.JobName(job)), name, 150);
            Add(keys, Button(Strings.Change, () => app.ChangeKey(job)), Field + 158, 12 + i * 34 - 3, 112);
        }

        order = new Label { AutoSize = false };
        Add(ai, new Label { Text = Strings.Order, AutoSize = false }, 12, 12 + 5, 130);
        Add(ai, order, Field, 12 + 5, Wide, 88);
        Add(ai, Button(Strings.ChangeOrder, app.ChangeOrder), Field, 12 + 100, 136);
        Add(ai, Button(Strings.ConnectAi, app.Connect), Field + 146, 12 + 100, 136);
        modelLabel = new Label { Text = Strings.ClaudeCliModel, AutoSize = false };
        model = Combo(() =>
        {
            settings.Model = Settings.Models[model!.SelectedIndex].Id;
            app.SaveLook();
        });
        model.Items.AddRange(Settings.Models.Select(m => (object)m.Name).ToArray());
        Add(ai, modelLabel, 12, 12 + 144 + 5, 130);
        Add(ai, model, Field, 12 + 144, Wide);

        autostart = new CheckBox { Text = Strings.StartWithWindows };
        autostart.Click += (_, _) =>
        {
            app.SetStartWithWindows(autostart.Checked);
            Reload();
        };
        history = new CheckBox { Text = Strings.KeepAnswers };
        history.Click += (_, _) => app.ToggleHistory();
        updates = new CheckBox { Text = Strings.CheckUpdates };
        updates.Click += (_, _) => app.ToggleUpdates();
        Add(general, autostart, 12, 12, Wide + Field);
        Add(general, history, 12, 12 + 34, Wide + Field);
        Add(general, updates, 12, 12 + 2 * 34, Wide + Field);
        Add(general, Button(Strings.OpenLogFolder, OpenLogFolder), 12, 12 + 3 * 34, 160);

        var tabs = new TabControl { Bounds = new Rectangle(Px(12), Px(12), Px(436), Px(290)) };
        tabs.TabPages.AddRange(new[] { reading, answer, keys, ai, general });
        var close = Button(Strings.Close, Close);
        close.Bounds = new Rectangle(Px(348), Px(314), Px(100), Px(30));
        CancelButton = close;
        Controls.AddRange(new Control[] { tabs, close });
        ClientSize = new Size(Px(460), Px(356));
        var area = screen.WorkingArea;
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);

        // the tray menu or another dialog may have changed things since the window was last in front
        Activated += (_, _) => Reload();
        Reload();
    }

    int Px(int v) => (int)Math.Round(v * scale);

    ComboBox Combo(Action commit)
    {
        var box = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        box.SelectionChangeCommitted += (_, _) => commit();
        return box;
    }

    Button Button(string text, Action click)
    {
        var button = new Button { Text = text };
        button.Click += (_, _) => click();
        return button;
    }

    void Row(TabPage page, int row, string text, Control control, int width = Wide)
    {
        Add(page, new Label { Text = text, AutoSize = false }, 12, 12 + row * 34 + 5, 130);
        Add(page, control, Field, 12 + row * 34, width);
    }

    void Add(TabPage page, Control control, int x, int y, int width, int height = 24)
    {
        control.Bounds = new Rectangle(Px(x), Px(y), Px(width), Px(height));
        page.Controls.Add(control);
    }

    static void Fill(ComboBox box, IEnumerable<string> names, int selected)
    {
        box.Items.Clear();
        box.Items.AddRange(names.Cast<object>().ToArray());
        box.SelectedIndex = selected;
    }

    void OpenLogFolder()
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Glance");
        Directory.CreateDirectory(folder);
        Process.Start("explorer.exe", folder);
    }

    void Reload()
    {
        readChoices = TrayApp.ScreenChoices(true);
        Fill(readFrom, readChoices.Select(c => c.Text), Math.Max(0, readChoices.FindIndex(c => c.Device == settings.ReadDevice)));
        modes = settings.AllModes();
        Fill(mode, modes.Select(m => m.Name), Math.Max(0, modes.FindIndex(m => m.Id == settings.Mode)));
        languages = TrayApp.TranslateChoices();
        Fill(translate, languages.Select(l => l.Name), Math.Max(0, languages.FindIndex(l => l.Value == settings.TranslateTo)));

        showChoices = TrayApp.ScreenChoices(false);
        Fill(showOn, showChoices.Select(c => c.Text), Math.Max(0, showChoices.FindIndex(c => c.Device == settings.ShowDevice)));
        positions = new() { (Strings.BottomRight, false, false), (Strings.CenterOfScreen, true, false) };
        if (settings.AnswerArea != null)
            positions.Add((Strings.DrawnArea, false, true));
        int at = settings.UseArea && settings.AnswerArea != null ? positions.Count - 1 : settings.CenterAnswer ? 1 : 0;
        Fill(position, positions.Select(p => p.Name), at);
        opacity.SelectedIndex = Array.IndexOf(Opacities, settings.TextOpacity);
        plain.Checked = settings.PlainAnswer;

        foreach (var (job, label) in keyNames)
            label.Text = KeyDialog.KeyName(app.KeyOf(job));

        var chain = settings.Chain();
        var names = settings.Options().ToDictionary(o => o.Entry, o => o.Label);
        order.Text = string.Join("\n", chain.Select((e, i) => $"{i + 1}. {names[e]}"));
        modelLabel.Visible = model.Visible = chain.Contains(Settings.Claude);
        model.SelectedIndex = Math.Max(0, Array.FindIndex(Settings.Models, m => m.Id == settings.Model));

        autostart.Checked = TrayApp.StartValue() != null;
        history.Checked = settings.KeepHistory;
        updates.Checked = settings.CheckUpdates;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        font.Dispose();
    }
}
