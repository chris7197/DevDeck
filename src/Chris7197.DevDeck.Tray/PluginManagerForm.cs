using chris7197.DevDeck.Core;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace chris7197.DevDeck.Tray;

/// <summary>
/// A non-executing plugin manager: no COM, no assembly loading, no process restarts.
/// Startup diagnostics come from the host's last observed load, not a simulated probe.
/// </summary>
public partial class PluginManagerForm : Form
{
    private readonly TrayPreferences _preferences;
    private string _hostDirectory;
    private DevDeckPluginInventory.Entry? _selected;
    private DevDeckPluginConfigurationStore.Document? _document;
    private readonly Dictionary<string, Func<JsonNode?>> _editors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ToolTip _tips = new();
    private bool _loading;
    private bool _dirty;

    public PluginManagerForm(TrayPreferences preferences)
    {
        _preferences = preferences;
        _hostDirectory = !string.IsNullOrWhiteSpace(preferences.PluginHostDirectory)
            ? preferences.PluginHostDirectory
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HotSpot", "StreamDock", "plugins", "com.chris7197.devdeck.sdPlugin");
        InitializeComponent();
        LoadInventory();
    }

    private bool CanDiscard()
    {
        return !_dirty || MessageBox.Show(this, "Discard unsaved plugin settings?", "Unsaved changes",
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
    }

    private void LoadInventory(string? preferredFolder = null)
    {
        _loading = true;
        try
        {
            _selected = null;
            _document = null;
            _dirty = false;
            _editors.Clear();
            ClearEditorControls();
            fieldTable.RowStyles.Clear();
            fieldTable.RowCount = 0;
            lblPluginName.Text = "Select a plugin";
            lblStatus.Text = "Select an installed plugin to view diagnostics and settings.";
            hostPath.Text = "Host: " + _hostDirectory;
            pluginList.Items.Clear();
            var inventory = DevDeckPluginInventory.Scan(_hostDirectory);
            var snapshot = DevDeckPluginHealthStore.Read(_hostDirectory);
            foreach (var entry in inventory)
            {
                var previous = snapshot?.Plugins.FirstOrDefault(x => x.Folder.Equals(entry.FolderName,
                    StringComparison.OrdinalIgnoreCase));
                string state = entry.Problem is not null ? "Invalid" : !entry.Enabled ? "Disabled" :
                    previous is null ? "Not checked" : previous.Loaded ? "Loaded *" : "Failed *";
                var item = new ListViewItem([entry.FolderName, state]) { Tag = entry };
                if (entry.Problem is not null || (previous is not null && !previous.Loaded && entry.Enabled))
                    item.ForeColor = System.Drawing.Color.Firebrick;
                pluginList.Items.Add(item);
                if (entry.FolderName.Equals(preferredFolder, StringComparison.OrdinalIgnoreCase))
                    item.Selected = true;
            }
            if (inventory.Count == 0)
                lblStatus.Text = "No plugins found. Select the Stream Dock installation folder above.";
            else if (pluginList.SelectedItems.Count == 0)
                pluginList.Items[0].Selected = true;
            pluginList.SelectedItems.Cast<ListViewItem>().FirstOrDefault()?.EnsureVisible();
        }
        finally { _loading = false; }
        ShowSelected();
    }

    private void ShowSelected()
    {
        if (_loading) return;
        _selected = pluginList.SelectedItems.Count == 1
            ? pluginList.SelectedItems[0].Tag as DevDeckPluginInventory.Entry : null;
        _document = null;
        _editors.Clear();
        ClearEditorControls();
        fieldTable.RowStyles.Clear();
        fieldTable.RowCount = 0;
        _dirty = false;
        MarkDirty(false);
        if (_selected is null) return;
        lblPluginName.Text = _selected.FolderName + "  (" + _selected.Id + ")";
        var snapshot = DevDeckPluginHealthStore.Read(_hostDirectory);
        var checkedPlugin = snapshot?.Plugins.FirstOrDefault(x => x.Folder.Equals(_selected.FolderName,
            StringComparison.OrdinalIgnoreCase));
        string details = _selected.Problem ??
            (!_selected.Enabled ? "Disabled in config.json." :
             checkedPlugin is null ? "No host startup result has been recorded." :
             checkedPlugin.Loaded ? $"Loaded {checkedPlugin.ActionCount} action(s)." :
             "Host load failed: " + checkedPlugin.Error);
        if (snapshot is not null && checkedPlugin is not null)
        {
            details += $"  Last checked {snapshot.CheckedAtUtc.ToLocalTime():g}.";
            if (File.Exists(Path.Combine(_selected.Folder, "config.json")) &&
                File.GetLastWriteTimeUtc(Path.Combine(_selected.Folder, "config.json")) > snapshot.CheckedAtUtc.UtcDateTime ||
                !string.IsNullOrEmpty(_selected.AssemblyName) &&
                File.Exists(Path.Combine(_selected.Folder, _selected.AssemblyName)) &&
                File.GetLastWriteTimeUtc(Path.Combine(_selected.Folder, _selected.AssemblyName)) > snapshot.CheckedAtUtc.UtcDateTime)
                details += "  Changes since last host startup — restart to confirm status.";
        }
        lblStatus.Text = details;
        try
        {
            _document = DevDeckPluginConfigurationStore.Open(_selected.Folder);
            BuildEditors(_document);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { lblStatus.Text += "  Configuration cannot be edited: " + e.Message; }
    }

    private void ClearEditorControls()
    {
        _tips.RemoveAll();
        foreach (Control control in fieldTable.Controls.Cast<Control>().ToArray())
        {
            fieldTable.Controls.Remove(control);
            control.Dispose();
        }
    }

    private void BuildEditors(DevDeckPluginConfigurationStore.Document document)
    {
        foreach (var field in document.Fields)
        {
            JsonNode? current = document.Values[field.Key];
            Control editor;
            Func<JsonNode?> getValue;
            if (field.Type == "boolean")
            {
                if (field.Nullable)
                {
                    var combo = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
                    combo.Items.AddRange(["Inherit (null)", "Yes", "No"]);
                    combo.SelectedIndex = current is null ? 0 : current.GetValue<bool>() ? 1 : 2;
                    combo.SelectedIndexChanged += (_, _) => MarkDirty(true);
                    editor = combo;
                    getValue = () => combo.SelectedIndex == 0 ? null : JsonValue.Create(combo.SelectedIndex == 1);
                }
                else
                {
                    var checkbox = new CheckBox { Text = "Enabled", Checked = current?.GetValue<bool>() ?? false,
                        AutoSize = true };
                    checkbox.CheckedChanged += (_, _) => MarkDirty(true);
                    editor = checkbox;
                    getValue = () => JsonValue.Create(checkbox.Checked);
                }
            }
            else
            {
                bool isJson = field.Type is "array" or "object" or "json";
                var text = new TextBox
                {
                    Text = current is null ? "" : isJson ? current.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) :
                        current.ToString(),
                    Multiline = isJson,
                    ScrollBars = isJson ? ScrollBars.Vertical : ScrollBars.None,
                    Height = isJson ? 146 : 27,
                    Dock = DockStyle.Fill,
                    AcceptsReturn = isJson
                };
                text.TextChanged += (_, _) => MarkDirty(true);
                editor = text;
                getValue = () => ParseEditor(field, text.Text);
            }
            editor.Margin = new Padding(3, 6, 6, 6);
            var label = new Label
            {
                Text = field.Label + (field.Nullable ? " (optional)" : ""),
                AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft,
                Margin = new Padding(4, 3, 4, 3)
            };
            if (!string.IsNullOrWhiteSpace(field.Description))
            {
                _tips.SetToolTip(label, field.Description);
                _tips.SetToolTip(editor, field.Description);
            }
            int row = fieldTable.RowCount++;
            fieldTable.RowStyles.Add(new RowStyle(SizeType.Absolute, field.Type is "array" or "object" or "json" ? 159 : 43));
            fieldTable.Controls.Add(label, 0, row);
            fieldTable.Controls.Add(editor, 1, row);
            _editors.Add(field.Key, getValue);
        }
    }

    private static JsonNode? ParseEditor(DevDeckPluginConfigurationStore.SettingField field, string text)
    {
        string trimmed = text.Trim();
        if (field.Nullable && trimmed.Length == 0) return null;
        return field.Type switch
        {
            "string" => JsonValue.Create(text),
            "integer" => JsonValue.Create(long.Parse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture)),
            "number" => JsonValue.Create(decimal.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture)),
            "array" or "object" or "json" => JsonNode.Parse(trimmed),
            _ => throw new InvalidOperationException("Unknown editor type " + field.Type)
        };
    }

    private void MarkDirty(bool dirty)
    {
        if (_loading) return;
        _dirty = dirty;
        btnSave.Enabled = dirty && _document is not null;
        btnDiscard.Enabled = dirty && _document is not null;
        lblChanged.Text = dirty ? "Unsaved changes" : "";
    }

    private void btnSave_Click(object? sender, EventArgs e)
    {
        if (_document is null) return;
        try
        {
            JsonObject proposed = (JsonObject)_document.Values.DeepClone();
            foreach (var (key, read) in _editors) proposed[key] = read();
            var errors = DevDeckPluginConfigurationStore.Validate(_document.PluginFolder, proposed);
            if (errors.Count != 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            string folder = _selected!.FolderName;
            _document = DevDeckPluginConfigurationStore.Save(_document, proposed);
            new DeckLog().Write("Plugin settings saved for " + folder + "; host restart required.");
            LoadInventory(folder);
            MessageBox.Show(this, "Settings saved. Restart the Stream Dock host to apply them.",
                "DevDeck plugins", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or OverflowException)
        {
            MessageBox.Show(this, ex.Message, "Could not save plugin", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void btnDiscard_Click(object? sender, EventArgs e) => ShowSelected();
    private void btnRefresh_Click(object? sender, EventArgs e)
    {
        if (CanDiscard()) LoadInventory(_selected?.FolderName);
    }
    private void pluginList_SelectedIndexChanged(object? sender, EventArgs e)
    {
        if (_loading) return;
        if (!CanDiscard())
        {
            // Restore the prior selection without triggering another prompt.
            _loading = true;
            try
            {
                foreach (ListViewItem item in pluginList.Items)
                    item.Selected = ((DevDeckPluginInventory.Entry)item.Tag!).FolderName == _selected?.FolderName;
            }
            finally { _loading = false; }
            return;
        }
        ShowSelected();
    }

    private void btnBrowse_Click(object? sender, EventArgs e)
    {
        if (!CanDiscard()) return;
        using var folder = new FolderBrowserDialog
        {
            Description = "Select the folder containing DevDeck settings.json and the plugins subfolder",
            SelectedPath = Directory.Exists(_hostDirectory) ? _hostDirectory : "",
            ShowNewFolderButton = false
        };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        if (!Directory.Exists(Path.Combine(folder.SelectedPath, "plugins")))
        {
            MessageBox.Show(this, "This folder has no plugins subfolder. Select the installed DevDeck host directory.",
                "Invalid folder", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _hostDirectory = folder.SelectedPath;
        _preferences.PluginHostDirectory = _hostDirectory;
        _preferences.Save();
        LoadInventory();
    }
    private void btnOpenFolder_Click(object? sender, EventArgs e)
    {
        string folder = _selected?.Folder ?? Path.Combine(_hostDirectory, "plugins");
        if (Directory.Exists(folder))
            Process.Start(new ProcessStartInfo { FileName = folder, UseShellExecute = true });
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!CanDiscard()) e.Cancel = true;
        base.OnFormClosing(e);
    }
}
