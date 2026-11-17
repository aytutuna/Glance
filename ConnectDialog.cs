using System.Diagnostics;

namespace Glance;

class ConnectDialog : Form
{
    static readonly string[] Ids = { Settings.Claude, Settings.Codex, Settings.GeminiCli, "anthropic", "openai", "gemini", "custom" };

    // the command, how to install it and how to sign in, for the accounts that work through a CLI
    static readonly Dictionary<string, (string Exe, string Install, string Login)> Accounts = new()
    {
        [Settings.Claude] = ("claude", "npm install -g @anthropic-ai/claude-code", "claude"),
        [Settings.Codex] = ("codex", "npm install -g @openai/codex", "codex login"),
        [Settings.GeminiCli] = ("gemini", "npm install -g @google/gemini-cli", "gemini"),
    };
    const int Wide = 388, Narrow = 276;

    readonly Settings settings;
    readonly double scale;
    readonly Font font;
    readonly Label providerLabel, savedLabel, keyLabel, urlLabel, modelLabel, effortLabel, note, status, info;
    readonly ComboBox provider = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox saved = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly ComboBox model = new();
    readonly ComboBox effort = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly TextBox key = new() { UseSystemPasswordChar = true };
    readonly TextBox url = new();
    readonly Button remove, load, signIn, save, cancel;
    CancellationTokenSource? loading;
    List<string> levels = new();

    public bool Changed { get; private set; }

    string Id => Ids[provider.SelectedIndex];

    public ConnectDialog(Screen screen, Settings settings)
    {
        this.settings = settings;
        scale = Native.ScaleFor(screen);

        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        Text = Strings.ConnectTitle;
        StartPosition = FormStartPosition.Manual;
        font = new Font("Segoe UI", Px(13), GraphicsUnit.Pixel);
        Font = font;

        providerLabel = Caption(Strings.AI);
        savedLabel = Caption(Strings.SavedKeysCaption);
        keyLabel = Caption(Strings.ApiKey);
        urlLabel = Caption(Strings.BaseUrl);
        modelLabel = Caption(Strings.Model);
        effortLabel = Caption(Strings.Effort);
        info = Caption("");
        info.ForeColor = SystemColors.GrayText;
        note = Caption(Strings.ConnectNote);
        note.ForeColor = SystemColors.GrayText;
        status = Caption("");
        status.ForeColor = Color.Firebrick;
        url.PlaceholderText = "http://localhost:11434/v1";
        model.DropDownStyle = ComboBoxStyle.DropDown;

        remove = MakeButton(Strings.RemoveKey, Remove);
        load = MakeButton(Strings.LoadModels, () => _ = LoadModels());
        signIn = MakeButton(Strings.SignIn, SignIn);
        save = MakeButton(Strings.Save, Save);
        cancel = MakeButton(Strings.Cancel, Close);
        AcceptButton = save;
        CancelButton = cancel;

        foreach (var id in Ids)
            provider.Items.Add(Settings.Providers.First(p => p.Id == id).Name);
        // the CLI account is the default provider, so a first visit starts on the Claude API instead
        int start = Array.IndexOf(Ids, settings.Provider == Settings.Claude ? "anthropic" : settings.Provider);
        provider.SelectedIndex = Math.Max(start, 0);
        provider.SelectedIndexChanged += (_, _) => Pick();

        Controls.AddRange(new Control[] { providerLabel, provider, savedLabel, saved, remove, keyLabel, key, urlLabel, url, info, signIn, modelLabel, model, load, effortLabel, effort, note, status, save, cancel });
        Pick();
        var area = screen.WorkingArea;
        Location = new Point(area.X + (area.Width - Width) / 2, area.Y + (area.Height - Height) / 2);
    }

    int Px(int v) => (int)Math.Round(v * scale);

    Label Caption(string text)
    {
        return new Label { Text = text, AutoSize = false };
    }

    Button MakeButton(string text, Action click)
    {
        var button = new Button { Text = text };
        button.Click += (_, _) => click();
        return button;
    }

    void Pick()
    {
        string id = Id;
        loading?.Cancel();
        loading = null;
        load.Enabled = true;
        key.Text = "";
        ShowKeys();
        url.Text = settings.CustomUrl ?? "";
        model.Items.Clear();
        model.DropDownStyle = id == Settings.Claude ? ComboBoxStyle.DropDownList : ComboBoxStyle.DropDown;
        if (id is Settings.Claude or "anthropic")
            model.Items.AddRange(Settings.Models.Select(m => (object)m.Id).ToArray());
        else if (id == Settings.GeminiCli)
            model.Items.AddRange(GeminiCli.Aliases);
        model.Text = id == Settings.Claude ? settings.Model : settings.ProviderModels.GetValueOrDefault(id) ?? "";
        if (id == "anthropic" && model.Text.Length == 0)
            model.Text = Settings.Models[0].Id;
        levels = Settings.EffortLevels(id).Prepend("").ToList();
        effort.Items.Clear();
        effort.Items.AddRange(levels.Select(l => (object)Settings.EffortName(l)).ToArray());
        effort.SelectedIndex = Math.Max(0, levels.IndexOf(settings.EffortOf(id)));
        info.Text = "";
        Say("", false);
        Arrange();
        if (Accounts.ContainsKey(id))
            _ = ShowAccount(id, loading = new CancellationTokenSource(TimeSpan.FromSeconds(20)));
    }

    void ShowKeys()
    {
        string id = Id;
        bool stored = settings.HasKey(id);
        saved.Items.Clear();
        saved.Items.AddRange(settings.KeyNames(id).Cast<object>().ToArray());
        if (stored)
            saved.SelectedIndex = 0;
        key.PlaceholderText = stored ? Strings.PasteAnotherKey : id == "custom" ? Strings.Optional : Strings.PasteKey;
        keyLabel.Text = stored ? Strings.AddKey : id == "custom" ? Strings.ApiKeyOptional : Strings.ApiKey;
    }

    void Arrange()
    {
        bool account = Accounts.ContainsKey(Id);
        int y = Px(14);
        y = Row(providerLabel, provider, y, Wide, true);
        bool stored = !account && settings.HasKey(Id);
        y = Row(savedLabel, saved, y, Narrow, stored);
        remove.Visible = stored;
        remove.Bounds = new Rectangle(Px(16 + Narrow + 12), saved.Top - Px(1), Px(Wide - Narrow - 12), Px(26));
        y = Row(keyLabel, key, y, Wide, !account);
        y = Row(urlLabel, url, y, Wide, Id == "custom");
        info.Visible = signIn.Visible = account;
        if (account)
        {
            info.Bounds = new Rectangle(Px(16), y, Px(Wide - 112), Px(34));
            signIn.Bounds = new Rectangle(Px(16 + Wide - 100), y, Px(100), Px(26));
            y += Px(46);
        }
        y = Row(modelLabel, model, y, Narrow, true);
        load.Visible = !account;
        load.Bounds = new Rectangle(Px(16 + Narrow + 12), model.Top - Px(1), Px(Wide - Narrow - 12), Px(26));
        y = Row(effortLabel, effort, y, Narrow, levels.Count > 1);
        note.Bounds = new Rectangle(Px(16), y, Px(Wide), Px(34));
        status.Bounds = new Rectangle(Px(16), y + Px(36), Px(Wide), Px(48));
        y += Px(90);
        save.Bounds = new Rectangle(Px(16 + Wide - 208), y, Px(100), Px(30));
        cancel.Bounds = new Rectangle(Px(16 + Wide - 100), y, Px(100), Px(30));
        ClientSize = new Size(Px(420), y + Px(44));
    }

    int Row(Label label, Control field, int y, int width, bool show)
    {
        label.Visible = field.Visible = show;
        if (!show)
            return y;
        label.Bounds = new Rectangle(Px(16), y, Px(Wide), Px(18));
        field.Location = new Point(Px(16), y + Px(20));
        field.Width = Px(width);
        return y + Px(20) + field.Height + Px(12);
    }

    async Task ShowAccount(string id, CancellationTokenSource cts)
    {
        try
        {
            if (Cli.Find(Accounts[id].Exe) is not { } at)
            {
                info.Text = string.Format(Strings.AccountMissing, Accounts[id].Install);
                return;
            }
            string found = string.Format(Strings.AccountFound, at.Script ?? at.Program);
            info.Text = found;
            bool? signed = id switch
            {
                Settings.Codex => await Cli.Probe("codex", new[] { "login", "status" }, cts.Token) is { Exit: 0 },
                Settings.GeminiCli => File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "oauth_creds.json")),
                _ => null,
            };
            if (signed != null && Id == id)
                info.Text = found + "\n" + (signed == true ? Strings.SignedIn : Strings.NotSignedIn);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (loading == cts)
                loading = null;
            cts.Dispose();
        }
    }

    void SignIn()
    {
        Process.Start(new ProcessStartInfo("cmd.exe", "/k " + Accounts[Id].Login) { UseShellExecute = true });
    }

    void Say(string text, bool error)
    {
        status.ForeColor = error ? Color.Firebrick : SystemColors.GrayText;
        status.Text = text;
    }

    void Remove()
    {
        if (saved.SelectedIndex < 0)
            return;
        settings.RemoveKey(Id, saved.SelectedIndex);
        Changed = true;
        ShowKeys();
        Arrange();
    }

    async Task LoadModels()
    {
        string typed = key.Text.Trim();
        if (typed.Length > 0 && Api.CheckKey(typed) is string bad)
        {
            Say(bad, true);
            return;
        }
        loading?.Cancel();
        var cts = loading = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        load.Enabled = false;
        Say(Strings.LoadingModels, false);
        try
        {
            var ids = await Ai.Models(settings, Id, url.Text, typed.Length > 0 ? typed : null, cts.Token);
            if (loading != cts)
                return;
            model.Items.Clear();
            model.Items.AddRange(ids);
            Say(string.Format(Strings.ModelsLoaded, ids.Length), false);
            model.Focus();
            model.DroppedDown = true;
        }
        catch (AiException ex)
        {
            if (loading == cts)
                Say(ex.Message, true);
        }
        catch (OperationCanceledException)
        {
            if (loading == cts)
                Say(Strings.ProviderTimeout, true);
        }
        finally
        {
            if (loading == cts)
            {
                load.Enabled = true;
                loading = null;
            }
            cts.Dispose();
        }
    }

    void SaveEffort(string id)
    {
        if (levels.Count > 1)
            settings.Effort[id] = levels[effort.SelectedIndex];
    }

    void SaveAccount(string id)
    {
        string picked = model.Text.Trim();
        if (id == Settings.Claude)
        {
            if (!Settings.Models.Any(m => m.Id == picked))
            {
                Say(Strings.PickModel, true);
                return;
            }
            settings.Model = picked;
        }
        else
        {
            settings.ProviderModels[id] = picked;
        }
        SaveEffort(id);
        settings.Provider = id;
        settings.Include(id);
        Changed = true;
        Close();
    }

    void Save()
    {
        string id = Id, typed = key.Text.Trim();
        if (Accounts.ContainsKey(id))
        {
            SaveAccount(id);
            return;
        }
        if (typed.Length > 0 && Api.CheckKey(typed) is string bad)
        {
            Say(bad, true);
            return;
        }
        if (typed.Length == 0 && settings.HasKey(id) && settings.GetKeys(id).Length == 0)
        {
            Say(Strings.SavedKeysUnreadable, true);
            return;
        }
        bool hasKey = typed.Length > 0 || settings.HasKey(id);
        if (id != "custom" && !hasKey)
        {
            Say(Strings.EnterKey, true);
            return;
        }
        string picked = model.Text.Trim();
        if (picked.Length == 0)
        {
            Say(Strings.PickModel, true);
            return;
        }
        string? address = null;
        if (id == "custom")
        {
            bool sameHost = settings.SameHost(url.Text);
            var error = Api.CheckUrl(url.Text, typed.Length > 0 || hasKey && sameHost, out var normal);
            if (error != null)
            {
                Say(error, true);
                return;
            }
            address = normal;
            // saved keys belong to the old address
            if (!sameHost)
                settings.ApiKeys.Remove(id);
        }
        if (typed.Length > 0)
            settings.AddKey(id, typed);
        if (address != null)
            settings.CustomUrl = address;
        settings.ProviderModels[id] = picked;
        SaveEffort(id);
        settings.Provider = id;
        if (!settings.HasKey(id))
            settings.Include(id);
        Changed = true;
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        loading?.Cancel();
        loading = null;
        key.Clear();
        base.OnFormClosed(e);
        font.Dispose();
    }
}
