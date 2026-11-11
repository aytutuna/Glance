namespace Glance;

class ModesDialog : Form
{
    const int Wide = 528;

    class Entry
    {
        public string Id = "";
        public string Name = "";
        public string Prompt = "";
        public bool Builtin;
    }

    readonly Settings settings;
    readonly double scale;
    readonly Font font;
    readonly Label status;
    readonly ListBox list = new() { IntegralHeight = false };
    readonly TextBox name = new();
    readonly TextBox prompt = new() { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical };
    readonly Button add, delete, reset, save, cancel;
    readonly List<Entry> entries;
    int shown = -1;
    bool renaming;

    public bool Changed { get; private set; }

    public ModesDialog(Screen screen, Settings settings)
    {
        this.settings = settings;
        scale = Native.ScaleFor(screen);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        Text = Strings.EditModesTitle;
        StartPosition = FormStartPosition.Manual;
        font = new Font("Segoe UI", Px(13), GraphicsUnit.Pixel);
        Font = font;

        status = new Label { AutoSize = false, ForeColor = Color.Firebrick };
        add = MakeButton(Strings.Add, Add);
        delete = MakeButton(Strings.Delete, Delete);
        reset = MakeButton(Strings.Reset, Reset);
        save = MakeButton(Strings.Ok, Save);
        cancel = MakeButton(Strings.Cancel, Close);
        CancelButton = cancel;

        entries = Settings.Modes.Select(m => new Entry
        {
            Id = m.Id,
            Name = m.Name,
            Prompt = Prompts.Instruction(settings, m.Id),
            Builtin = true,
        }).Concat(settings.CustomModes.Select(m => new Entry { Id = m.Id, Name = m.Name, Prompt = m.Prompt })).ToList();
        foreach (var entry in entries)
            list.Items.Add(entry.Name);
        list.SelectedIndexChanged += (_, _) =>
        {
            if (!renaming)
                Show(list.SelectedIndex);
        };
        name.TextChanged += (_, _) =>
        {
            if (shown >= 0 && !entries[shown].Builtin && (string)list.Items[shown] != name.Text)
            {
                renaming = true;
                list.Items[shown] = name.Text;
                renaming = false;
            }
        };

        Controls.AddRange(new Control[] { list, add, delete, reset, name, prompt, status, save, cancel });
        int y = Px(16);
        list.Bounds = new Rectangle(Px(16), y, Px(150), Px(250));
        add.Bounds = new Rectangle(Px(16), y + Px(258), Px(70), Px(28));
        delete.Bounds = new Rectangle(Px(96), y + Px(258), Px(70), Px(28));
        name.Bounds = new Rectangle(Px(178), y, Px(Wide - 162), Px(24));
        prompt.Bounds = new Rectangle(Px(178), y + Px(32), Px(Wide - 162), Px(218));
        reset.Bounds = new Rectangle(Px(178), y + Px(258), Px(70), Px(28));
        y += Px(296);
        status.Bounds = new Rectangle(Px(16), y, Px(Wide), Px(22));
        y += Px(28);
        save.Bounds = new Rectangle(Px(16 + Wide - 208), y, Px(100), Px(30));
        cancel.Bounds = new Rectangle(Px(16 + Wide - 100), y, Px(100), Px(30));
        ClientSize = new Size(Px(Wide + 32), y + Px(46));
        var area = screen.WorkingArea;
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
        list.SelectedIndex = 0;
    }

    int Px(int v) => (int)Math.Round(v * scale);

    Button MakeButton(string text, Action click)
    {
        var button = new Button { Text = text };
        button.Click += (_, _) => click();
        return button;
    }

    void Keep()
    {
        if (shown < 0)
            return;
        entries[shown].Prompt = prompt.Text;
        if (!entries[shown].Builtin)
            entries[shown].Name = name.Text;
    }

    void Show(int index)
    {
        Keep();
        shown = index;
        if (index < 0)
            return;
        var entry = entries[index];
        name.Text = entry.Name;
        name.ReadOnly = entry.Builtin;
        prompt.Text = entry.Prompt;
        delete.Enabled = !entry.Builtin;
        reset.Enabled = entry.Builtin;
        status.Text = "";
    }

    void Add()
    {
        Keep();
        entries.Add(new Entry { Id = new CustomMode().Id, Name = Strings.NewMode });
        list.Items.Add(Strings.NewMode);
        list.SelectedIndex = entries.Count - 1;
        name.Focus();
        name.SelectAll();
    }

    void Delete()
    {
        int at = shown;
        shown = -1;
        entries.RemoveAt(at);
        list.Items.RemoveAt(at);
        list.SelectedIndex = Math.Min(at, entries.Count - 1);
    }

    void Reset()
    {
        prompt.Text = Prompts.Default(settings, entries[shown].Id);
    }

    void Save()
    {
        Keep();
        foreach (var entry in entries.Where(e => !e.Builtin))
            entry.Name = entry.Name.Trim();
        var blank = entries.FirstOrDefault(e => e.Prompt.Trim().Length == 0 || e.Name.Length == 0);
        if (blank != null)
        {
            list.SelectedIndex = entries.IndexOf(blank);
            status.Text = blank.Builtin ? Strings.PromptEmpty : Strings.ModeNeedsBoth;
            return;
        }
        settings.CustomModes = entries.Where(e => !e.Builtin)
            .Select(e => new CustomMode { Id = e.Id, Name = e.Name, Prompt = e.Prompt.Trim() }).ToList();
        settings.PromptOverrides = entries.Where(e => e.Builtin && e.Prompt.Trim() != Prompts.Default(settings, e.Id))
            .ToDictionary(e => e.Id, e => e.Prompt.Trim().Replace(Prompts.Language(settings), "{language}"));
        if (!settings.AllModes().Any(m => m.Id == settings.Mode))
            settings.Mode = Settings.Modes[0].Id;
        Changed = true;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        font.Dispose();
    }
}
