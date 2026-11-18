namespace Glance;

class OrderDialog : Form
{
    const int Wide = 388;

    readonly Settings settings;
    readonly double scale;
    readonly Font font;
    readonly Label note, status;
    readonly CheckedListBox list = new() { CheckOnClick = true, IntegralHeight = false };
    readonly Button up, down, save, cancel;
    readonly List<(string Entry, string Label)> items;

    public bool Changed { get; private set; }

    public OrderDialog(Screen screen, Settings settings)
    {
        this.settings = settings;
        scale = Native.ScaleFor(screen);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        Native.Mirror(this);
        Text = Strings.OrderTitle;
        StartPosition = FormStartPosition.Manual;
        font = new Font("Segoe UI", Px(13), GraphicsUnit.Pixel);
        Font = font;

        note = new Label
        {
            Text = Strings.OrderNote,
            AutoSize = false,
        };
        status = new Label { AutoSize = false, ForeColor = Color.Firebrick };
        up = MakeButton(Strings.Up, () => Shift(-1));
        down = MakeButton(Strings.Down, () => Shift(1));
        save = MakeButton(Strings.Save, Save);
        cancel = MakeButton(Strings.Cancel, Close);
        AcceptButton = save;
        CancelButton = cancel;

        var options = settings.Options();
        var chain = settings.Chain();
        items = chain.Select(e => options.First(o => o.Entry == e)).Concat(options.Where(o => !chain.Contains(o.Entry))).ToList();
        foreach (var (entry, label) in items)
            list.Items.Add(label, chain.Contains(entry));
        if (list.Items.Count > 0)
            list.SelectedIndex = 0;

        Controls.AddRange(new Control[] { note, list, up, down, status, save, cancel });
        int y = Px(14);
        int noteHeight = Native.TextHeight(note, Px(Wide), Px(54));
        note.Bounds = new Rectangle(Px(16), y, Px(Wide), noteHeight);
        y += noteHeight + Px(6);
        int move = Math.Max(Native.ButtonWidth(up, Px(80)), Native.ButtonWidth(down, Px(80)));
        list.Bounds = new Rectangle(Px(16), y, Px(Wide) - move - Px(12), Px(200));
        up.Bounds = new Rectangle(Px(16 + Wide) - move, y, move, Px(28));
        down.Bounds = new Rectangle(Px(16 + Wide) - move, y + Px(36), move, Px(28));
        y += Px(208);
        status.Bounds = new Rectangle(Px(16), y, Px(Wide), Px(34));
        y += Px(40);
        int ok = Math.Max(Native.ButtonWidth(save, Px(100)), Native.ButtonWidth(cancel, Px(100)));
        save.Bounds = new Rectangle(Px(16 + Wide) - 2 * ok - Px(8), y, ok, Px(30));
        cancel.Bounds = new Rectangle(Px(16 + Wide) - ok, y, ok, Px(30));
        ClientSize = new Size(Px(420), y + Px(44));
        var area = screen.WorkingArea;
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
    }

    int Px(int v) => (int)Math.Round(v * scale);

    Button MakeButton(string text, Action click)
    {
        var button = new Button { Text = text };
        button.Click += (_, _) => click();
        return button;
    }

    void Shift(int by)
    {
        int at = list.SelectedIndex, to = at + by;
        if (at < 0 || to < 0 || to >= list.Items.Count)
            return;
        bool on = list.GetItemChecked(at);
        var item = items[at];
        items.RemoveAt(at);
        items.Insert(to, item);
        list.Items.RemoveAt(at);
        list.Items.Insert(to, item.Label);
        list.SetItemChecked(to, on);
        list.SelectedIndex = to;
    }

    void Save()
    {
        var order = items.Where((_, i) => list.GetItemChecked(i)).Select(o => o.Entry).ToList();
        if (order.Count == 0)
        {
            status.Text = Strings.TickOne;
            return;
        }
        settings.Order = order;
        Changed = true;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        font.Dispose();
    }
}
