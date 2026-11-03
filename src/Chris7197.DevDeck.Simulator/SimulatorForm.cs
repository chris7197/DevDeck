using System.Drawing;
using chris7197.DevDeck.Core;

namespace chris7197.DevDeck.Simulator;

/// <summary>Clickable, live-updating 15-key mock controller using the same statuses and PNGs as the device.</summary>
public sealed class SimulatorForm : Form
{
    private readonly ForegroundWindowTracker _foreground = new();
    private readonly DevDeckRuntime _runtime;
    private readonly DevDeckActionCoordinator _actions;
    private readonly ActiveDeveloperContext _context;
    private readonly DevDeckStatusMonitor _monitor;
    private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
    private readonly ToolTip _tooltip = new();
    private readonly TextBox _solution = new();
    private readonly Label _info = new();
    private readonly Label _foregroundStatus = new();
    private long _visualStudioObservation;
    private readonly CheckBox _automatic = new();
    private readonly ComboBox _profilePicker = new();
    private readonly CheckBox _automaticProfile = new();
    private readonly string? _initialPath;

    public SimulatorForm(string? initialPath)
    {
        _initialPath = initialPath;
        _runtime = DevDeckRuntime.Create(AppContext.BaseDirectory, foregroundWindowReader: _foreground);
        _actions = new DevDeckActionCoordinator(_runtime.Settings.MaxConcurrentActions);
        _context = new ActiveDeveloperContext(_runtime.ContextProvider);
        _monitor = new DevDeckStatusMonitor(_runtime, _context, _runtime.Profiles, Publish,
            TimeSpan.FromSeconds(_runtime.Settings.LiveRefreshSeconds));

        Text = "chris7197 DevDeck — Simulator";
        Size = new Size(940, 735);
        MinimumSize = new Size(740, 580);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(20, 25, 36);
        ForeColor = Color.White;

        var top = new TableLayoutPanel { Dock = DockStyle.Top, Height = 110, ColumnCount = 4, Padding = new Padding(16) };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        var caption = new Label { Text = "Solution (.sln/.slnx)", Dock = DockStyle.Top, Height = 23, Font = new Font("Segoe UI", 10) };
        var editPanel = new Panel { Dock = DockStyle.Fill };
        _solution.Dock = DockStyle.Bottom;
        _solution.Height = 32;
        _solution.Font = new Font("Consolas", 10);
        _solution.PlaceholderText = "Choose a solution, or focus Visual Studio for automatic detection";
        editPanel.Controls.Add(caption);
        editPanel.Controls.Add(_solution);
        top.Controls.Add(editPanel, 0, 0);

        var browse = MakeTopButton("Browse...");
        browse.Click += (_, _) => BrowseSolution();
        top.Controls.Add(browse, 1, 0);
        var apply = MakeTopButton("Use solution");
        apply.Click += async (_, _) => await UseSolutionAsync();
        top.Controls.Add(apply, 2, 0);
        _automatic.Text = "Auto detect VS";
        _automatic.Checked = true;
        _automatic.ForeColor = Color.White;
        _automatic.Dock = DockStyle.Fill;
        _automatic.CheckedChanged += (_, _) =>
        {
            if (_automatic.Checked) _context.UseAutomaticDetection();
            _ = _monitor.RefreshAsync();
        };
        top.Controls.Add(_automatic, 3, 0);

        // Profile override for testing without opening SSMS or the BI browser.
        var profilePanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Top, Height = 50, Padding = new Padding(18, 8, 8, 2),
            FlowDirection = FlowDirection.LeftToRight, WrapContents = false
        };
        profilePanel.Controls.Add(new Label { Text = "Active profile:", Width = 105, Height = 29,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.White });
        _profilePicker.Width = 175;
        _profilePicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _profilePicker.Items.AddRange(_runtime.Profiles.AvailableProfiles.Cast<object>().ToArray());
        _profilePicker.SelectedItem = "VisualStudio";
        _profilePicker.Enabled = false;
        _profilePicker.SelectedIndexChanged += async (_, _) =>
        {
            if (!_automaticProfile.Checked && _profilePicker.SelectedItem is string profile)
            {
                _runtime.Profiles.SetManualProfile(profile);
                await _monitor.RefreshAsync();
            }
        };
        profilePanel.Controls.Add(_profilePicker);
        _automaticProfile.Text = "Detect foreground application";
        _automaticProfile.Checked = true;
        _automaticProfile.ForeColor = Color.White;
        _automaticProfile.AutoSize = true;
        _automaticProfile.CheckedChanged += async (_, _) =>
        {
            _profilePicker.Enabled = !_automaticProfile.Checked;
            _runtime.Profiles.SetManualProfile(_automaticProfile.Checked ? null :
                _profilePicker.SelectedItem as string ?? "Default");
            await _monitor.RefreshAsync();
        };
        profilePanel.Controls.Add(_automaticProfile);
        _foregroundStatus.AutoSize = true;
        _foregroundStatus.ForeColor = Color.LightGray;
        _foregroundStatus.Margin = new Padding(15, 7, 0, 0);
        _foregroundStatus.Text = "Detected: none (switch to Visual Studio or SSMS once)";
        profilePanel.Controls.Add(_foregroundStatus);

        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 5, RowCount = 3,
            Padding = new Padding(16, 4, 16, 4) };
        for (int i = 0; i < 5; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
        for (int i = 0; i < 3; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 3));
        for (int i = 0; i < 15; i++)
        {
            var button = new Button
            {
                Dock = DockStyle.Fill, Margin = new Padding(7),
                BackColor = Color.FromArgb(27, 34, 48), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9, FontStyle.Bold),
                Text = "Loading...",
                ImageAlign = ContentAlignment.MiddleCenter,
                TextImageRelation = TextImageRelation.Overlay,
                Enabled = true
            };
            button.FlatAppearance.BorderColor = Color.FromArgb(66, 83, 108);
            {
                string keyId = $"sim-{i}";
                string slotId = DevDeckProfiles.SlotId(i + 1);
                _buttons[keyId] = button;
                button.Click += async (_, _) => await RunSlotAsync(keyId, slotId);
                _tooltip.SetToolTip(button, $"Profile slot {i + 1}");
                _monitor.Show(keyId, slotId);
            }
            grid.Controls.Add(button, i % 5, i / 5);
        }

        _info.Dock = DockStyle.Bottom;
        _info.Padding = new Padding(16, 7, 16, 7);
        _info.Height = 62;
        _info.ForeColor = Color.LightGray;
        _info.Text = "15 profile slots · Select a profile to test its layout. " +
                     "Switching to an app updates automatically; Git counts use cached origin/main.";
        Controls.Add(grid);
        Controls.Add(profilePanel);
        Controls.Add(top);
        Controls.Add(_info);

        Shown += async (_, _) =>
        {
            try
            {
                // Unlike a 10-second foreground poll, a system event catches even a
                // short visit to VS/SSMS before the simulator regains focus.
                _foreground.ApplicationActivated += OnApplicationActivated;
                _foreground.Start();
            }
            catch (Exception ex)
            {
                _runtime.Log.Write("Simulator foreground tracking unavailable: " + ex);
                _foregroundStatus.Text = "Foreground watcher unavailable: " + ex.Message;
            }
            if (!string.IsNullOrWhiteSpace(_initialPath))
            {
                _solution.Text = _initialPath;
                await UseSolutionAsync();
            }
            _monitor.Start();
        };
    }

    private async void OnApplicationActivated(ObservedForegroundWindow observed)
    {
        // SetWinEventHook callbacks are delivered on the WinForms UI thread.
        // Keep only the last *external* application; refocusing the simulator
        // should not erase its preview or the remembered Visual Studio solution.
        if (IsDisposed || Disposing) return;
        try
        {
            string profile = _runtime.Profiles.Refresh();
            if (_automaticProfile.Checked)
            {
                _profilePicker.SelectedItem = profile;
                _foregroundStatus.Text = $"Detected: {profile} ({observed.Window.ProcessName})";
            }

            // Show the new layout straight away, even if Visual Studio COM is busy.
            await _monitor.RefreshAsync();

            if (observed.Window.ProcessName.Equals("devenv", StringComparison.OrdinalIgnoreCase))
            {
                long observation = Interlocked.Increment(ref _visualStudioObservation);
                try
                {
                    // Use the captured process ID, rather than requiring VS to
                    // remain foreground when the async COM lookup runs.
                    DeveloperContext context = await _runtime.ContextProvider.ResolveVisualStudioProcessAsync(
                        observed.ProcessId);
                    if (observation == Volatile.Read(ref _visualStudioObservation))
                    {
                        _context.SetObserved(context);
                        if (!IsDisposed && _automatic.Checked)
                            _solution.Text = context.SolutionPath;
                    }
                }
                catch (Exception ex)
                {
                    _runtime.Log.Write("Simulator Visual Studio context capture: " + ex.Message);
                    if (!IsDisposed) _foregroundStatus.Text =
                        $"Detected: {profile} (solution unavailable: {ex.Message})";
                }
            }
            if (!IsDisposed) await _monitor.RefreshAsync();
        }
        catch (Exception ex)
        {
            _runtime.Log.Write("Simulator foreground update: " + ex);
        }
    }

    private static Button MakeTopButton(string text) => new()
    {
        Text = text, Dock = DockStyle.Fill, Margin = new Padding(6, 15, 6, 8),
        FlatStyle = FlatStyle.System
    };

    private void BrowseSolution()
    {
        using var dialog = new OpenFileDialog { Filter = "Visual Studio solutions (*.sln;*.slnx)|*.sln;*.slnx" };
        if (dialog.ShowDialog(this) == DialogResult.OK) _solution.Text = dialog.FileName;
    }

    private async Task UseSolutionAsync()
    {
        try
        {
            DeveloperContext selected = await _runtime.ContextProvider.ResolveAsync(_solution.Text);
            if (selected.SolutionPath is null) throw new InvalidOperationException("Select a saved solution file.");
            _context.SetExplicit(selected);
            _automatic.Checked = false;
            _automaticProfile.Checked = false;
            _profilePicker.SelectedItem = "VisualStudio";
            _info.Text = "Pinned: " + selected.SolutionPath;
            await _monitor.RefreshAsync();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Invalid solution", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private Task Publish(string keyId, DevDeckButtonState state)
    {
        if (!_buttons.ContainsKey(keyId) || IsDisposed || !IsHandleCreated) return Task.CompletedTask;
        byte[] png = DevDeckButtonRenderer.RenderPng(state);
        BeginInvoke((Action)(() =>
        {
            if (!_buttons.TryGetValue(keyId, out var key) || key.IsDisposed) return;
            using var stream = new MemoryStream(png);
            using var source = new Bitmap(stream);
            Image? old = key.Image;
            key.Image = new Bitmap(source); // independent of stream lifetime
            key.Text = "";
            old?.Dispose();
            _tooltip.SetToolTip(key, string.IsNullOrWhiteSpace(state.Description) ?
                state.Title + " — " + state.Subtitle : state.Description);
        }));
        return Task.CompletedTask;
    }

    private async Task RunSlotAsync(string keyId, string slotId)
    {
        _runtime.Profiles.Refresh();
        IDevDeckOperation? operation = _runtime.ResolveProfileAction(slotId);
        if (operation is null) return;
        if (operation is ShortcutOperation)
        {
            MessageBox.Show(this, "This is a real keyboard shortcut. The simulator currently has focus, so " +
                "it won't send keys to another application. Use the controller, or focus the intended app " +
                "and use the CLI only for read-only status checks.", "Shortcut preview");
            return;
        }
        // Match device behaviour: don't run the same key twice or overlap two mutations.
        using IDisposable? lease = _actions.TryBegin(keyId, operation.PluginActionId);
        if (lease is null)
        {
            _info.Text = "Action already running or all action slots are busy.";
            return;
        }
        // An explicitly pinned simulator solution is intentional; otherwise use the
        // same focused-VS / numbered-overlay resolution as the physical controller.
        DeckActionSettings? options = _monitor.SettingsFor(keyId);
        bool needsSolution = operation.RequiresSolutionFor(options);
        DeveloperContext? context = needsSolution
            ? !_automatic.Checked && _context.Last is { SolutionPath: not null } pinned
                ? pinned
                : await _runtime.SolutionSelector.ResolveAsync(operation.Name, operation.SaveAllBeforeExecute)
            : _context.Last ?? await _context.PollAsync();
        if (context is null && needsSolution) return; // Esc
        context ??= new DeveloperContext(Environment.CurrentDirectory, null, null);
        if (operation.PluginActionId.EndsWith("syncmain", StringComparison.Ordinal))
        {
            var confirm = MessageBox.Show(this,
                $"Execute Sync Main in {context.WorkingDirectory}?\n\nThis will STAGE AND COMMIT ALL changes as 'Pre merge', " +
                "then fetch and merge. Save unsaved Visual Studio files first.\n\nContinue?",
                "Confirm Git changes", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
        }
        _monitor.SetBusy(keyId, true);
        try
        {
            await Publish(keyId, new DevDeckButtonState(operation.Name, "Working", DeckStatus.Busy, IconName: operation.IconName));
            DevDeckOperationResult result = await operation.ExecuteAsync(new DevDeckOperationContext(context,
                stage => Publish(keyId, new DevDeckButtonState(operation.Name,
                    stage.Replace('\n', ' '), DeckStatus.Busy, IconName: operation.IconName)),
                ActionSettings: options));
            _info.Text = result.Message;
            if (result.Cancelled) return;
            MessageBox.Show(this, result.Message, result.Success ? "Action complete" : "Action failed", MessageBoxButtons.OK,
                result.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            _runtime.Log.WriteError("Simulator action failed: " + ex);
            MessageBox.Show(this, ex.Message, "Action failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _monitor.SetBusy(keyId, false);
            await _monitor.RefreshAsync();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _foreground.ApplicationActivated -= OnApplicationActivated;
        _foreground.Dispose();
        _monitor.DisposeAsync().AsTask().GetAwaiter().GetResult();
        foreach (var button in _buttons.Values) button.Image?.Dispose();
        _tooltip.Dispose();
        base.OnFormClosed(e);
    }
}
