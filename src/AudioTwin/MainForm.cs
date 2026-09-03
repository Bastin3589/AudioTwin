using System.Runtime.InteropServices;
using AudioTwin.Audio;

namespace AudioTwin;

/// <summary>
/// The entire user interface. Built in code on purpose: no designer files, no
/// resx, one window, ten controls.
///
/// The application lives in the notification area (system tray), not on the
/// taskbar: the window is only a settings panel that can be closed at any time
/// while routing keeps running in the background.
/// </summary>
public sealed class MainForm : Form
{
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    private readonly DeviceWatcher _watcher = new();
    private readonly AudioRouter _router = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly System.Windows.Forms.Timer _deviceRefreshTimer = new() { Interval = 750 };

    private readonly Label _titleLabel = new();
    private readonly Label _inputLabel = new();
    private readonly Button _refreshButton = new();
    private readonly OutputPanel _output1 = new();
    private readonly OutputPanel _output2 = new();
    private readonly Button _startButton = new();
    private readonly Label _statusLabel = new();
    private readonly CheckBox _autoStartCheck = new();
    private readonly CheckBox _startWithWindowsCheck = new();
    private readonly CheckBox _startMinimizedCheck = new();
    private readonly CheckBox _closeToTrayCheck = new();
    private readonly Button _hideButton = new();
    private readonly Label _messageLabel = new();

    // ---- notification area ----
    private readonly AppIcons _icons = new();
    private readonly NotifyIcon _tray = new();
    private readonly ContextMenuStrip _trayMenu = new();
    private readonly ToolStripMenuItem _menuOpen = new("Open AudioTwin");
    private readonly ToolStripMenuItem _menuRouting = new("Start routing");
    private readonly ToolStripMenuItem _menuStartWithWindows = new("Start with Windows");
    private readonly ToolStripMenuItem _menuExit = new("Exit");

    private string? _defaultDeviceId;
    private bool _loading;
    private bool _allowVisible;
    private bool _exiting;
    private bool _launchHandled;

    public MainForm(bool startHidden = false)
    {
        BuildUi();
        BuildTray();
        WireEvents();

        _loading = true;
        _output1.Volume.Value = Math.Clamp(_settings.Volume1, 0, 100);
        _output2.Volume.Value = Math.Clamp(_settings.Volume2, 0, 100);
        _output1.Delay.Value = Math.Clamp(_settings.Delay1Ms, 0, OutputBranch.MaxDelayMilliseconds);
        _output2.Delay.Value = Math.Clamp(_settings.Delay2Ms, 0, OutputBranch.MaxDelayMilliseconds);
        _autoStartCheck.Checked = _settings.StartRoutingOnLaunch;
        _startWithWindowsCheck.Checked = StartupManager.IsEnabled();
        _startMinimizedCheck.Checked = _settings.StartMinimized;
        _closeToTrayCheck.Checked = _settings.CloseToTray;
        _loading = false;

        // Decided before the framework asks the form to show itself.
        _allowVisible = !(startHidden || _settings.StartMinimized);

        UpdateSliderLabels();
        RefreshDevices(applySavedSelection: true);
        UpdateStatus();
    }

    // ---------------------------------------------------------------- layout

    private void BuildUi()
    {
        Text = "AudioTwin";
        Font = new Font("Segoe UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(480, 664);
        BackColor = SystemColors.Window;

        // The tray icon is the application's presence; the window is not.
        // Set before the handle exists so it never has to be recreated.
        ShowInTaskbar = false;

        _titleLabel.Text = "AudioTwin";
        _titleLabel.Font = new Font("Segoe UI", 13F, FontStyle.Bold);
        _titleLabel.Location = new Point(15, 12);
        _titleLabel.AutoSize = true;

        _inputLabel.Location = new Point(18, 46);
        _inputLabel.Size = new Size(340, 20);
        _inputLabel.Text = "Input:  Windows Default Audio";

        _refreshButton.Text = "Refresh devices";
        _refreshButton.Location = new Point(355, 42);
        _refreshButton.Size = new Size(110, 26);

        _output1.Build("Output 1", new Point(15, 76), new Size(450, 152));
        _output2.Build("Output 2", new Point(15, 236), new Size(450, 152));

        _startButton.Text = "START ROUTING";
        _startButton.Font = new Font("Segoe UI", 11F, FontStyle.Bold);
        _startButton.Location = new Point(15, 400);
        _startButton.Size = new Size(450, 44);

        _statusLabel.Location = new Point(18, 454);
        _statusLabel.Size = new Size(447, 20);
        _statusLabel.Font = new Font("Segoe UI", 9.75F, FontStyle.Bold);

        _autoStartCheck.Text = "Start routing when application launches";
        _autoStartCheck.Location = new Point(16, 482);
        _autoStartCheck.Size = new Size(440, 22);

        _startWithWindowsCheck.Text = "Start with Windows (starts hidden in the notification area)";
        _startWithWindowsCheck.Location = new Point(16, 506);
        _startWithWindowsCheck.Size = new Size(440, 22);

        _startMinimizedCheck.Text = "Start hidden in the notification area";
        _startMinimizedCheck.Location = new Point(16, 530);
        _startMinimizedCheck.Size = new Size(440, 22);

        _closeToTrayCheck.Text = "Closing this window hides it instead of quitting";
        _closeToTrayCheck.Location = new Point(16, 554);
        _closeToTrayCheck.Size = new Size(440, 22);

        _hideButton.Text = "Hide to notification area";
        _hideButton.Location = new Point(295, 582);
        _hideButton.Size = new Size(170, 28);

        _messageLabel.Location = new Point(18, 616);
        _messageLabel.Size = new Size(447, 40);
        _messageLabel.ForeColor = Color.FromArgb(90, 90, 90);

        Controls.AddRange(new Control[]
        {
            _titleLabel, _inputLabel, _refreshButton,
            _output1.Group, _output2.Group,
            _startButton, _statusLabel,
            _autoStartCheck, _startWithWindowsCheck, _startMinimizedCheck, _closeToTrayCheck,
            _hideButton, _messageLabel
        });
    }

    private void BuildTray()
    {
        Icon = _icons.Window;

        _menuOpen.Font = new Font(_menuOpen.Font, FontStyle.Bold);
        _menuStartWithWindows.CheckOnClick = true;

        _trayMenu.Items.AddRange(new ToolStripItem[]
        {
            _menuOpen,
            new ToolStripSeparator(),
            _menuRouting,
            _menuStartWithWindows,
            new ToolStripSeparator(),
            _menuExit
        });

        _tray.Icon = _icons.Stopped;
        _tray.Text = "AudioTwin";
        _tray.ContextMenuStrip = _trayMenu;
        _tray.Visible = true;
    }

    private void WireEvents()
    {
        _refreshButton.Click += (_, _) => { RefreshDevices(false); ShowMessage("Device list refreshed."); };
        _startButton.Click += (_, _) => ToggleRouting();
        _hideButton.Click += (_, _) => HideToTray();

        _output1.Devices.SelectedIndexChanged += (_, _) => OnDeviceSelectionChanged();
        _output2.Devices.SelectedIndexChanged += (_, _) => OnDeviceSelectionChanged();

        _output1.Volume.ValueChanged += (_, _) => OnSliderChanged();
        _output2.Volume.ValueChanged += (_, _) => OnSliderChanged();
        _output1.Delay.ValueChanged += (_, _) => OnSliderChanged();
        _output2.Delay.ValueChanged += (_, _) => OnSliderChanged();

        _autoStartCheck.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.StartRoutingOnLaunch = _autoStartCheck.Checked;
            _settings.Save();
        };

        _startWithWindowsCheck.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            var error = StartupManager.SetEnabled(_startWithWindowsCheck.Checked);
            if (error != null)
            {
                ShowMessage(error, isError: true);
                _loading = true;
                _startWithWindowsCheck.Checked = StartupManager.IsEnabled();
                _loading = false;
            }
            _settings.StartWithWindows = _startWithWindowsCheck.Checked;
            _settings.Save();
            SyncTrayMenu();
        };

        _startMinimizedCheck.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.StartMinimized = _startMinimizedCheck.Checked;
            _settings.Save();
        };

        _closeToTrayCheck.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _settings.CloseToTray = _closeToTrayCheck.Checked;
            _settings.Save();
        };

        _watcher.DevicesChanged += (_, _) => RunOnUi(RestartDebounce);
        _watcher.DefaultRenderDeviceChanged += (_, _) => RunOnUi(RestartDebounce);
        _deviceRefreshTimer.Tick += (_, _) => { _deviceRefreshTimer.Stop(); HandleDeviceChange(); };

        _router.Notice += (_, message) => RunOnUi(() => ShowMessage(message));
        _router.StoppedUnexpectedly += (_, reason) => RunOnUi(() =>
        {
            ShowMessage(reason + " Routing stopped.", isError: true);
            UpdateStatus();
            if (!Visible) Balloon("Routing stopped", reason, ToolTipIcon.Warning);
        });
        _router.StateChanged += (_, _) => RunOnUi(UpdateStatus);

        // ---- notification area ----
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowFromTray(); };
        _tray.BalloonTipClicked += (_, _) => ShowFromTray();
        _menuOpen.Click += (_, _) => ShowFromTray();
        _menuRouting.Click += (_, _) => ToggleRouting();
        _menuExit.Click += (_, _) => ExitApplication();
        _menuStartWithWindows.Click += (_, _) =>
        {
            // Let the checkbox handler own the registry work.
            _startWithWindowsCheck.Checked = _menuStartWithWindows.Checked;
        };

        Resize += (_, _) =>
        {
            // Minimising has nowhere to go without a taskbar button, so it means
            // "get out of the way" - the tray icon takes over.
            if (WindowState == FormWindowState.Minimized && !_exiting)
                RunOnUi(() => { if (WindowState == FormWindowState.Minimized) HideToTray(); });
        };

        FormClosing += HandleFormClosing;
    }

    // ------------------------------------------------- window / tray plumbing

    /// <summary>
    /// Application.Run() insists on showing the main form. When the app was asked
    /// to start hidden we swallow that, but the handle still has to exist or the
    /// message loop would have nothing to pump and would exit immediately.
    /// </summary>
    protected override void SetVisibleCore(bool value)
    {
        if (!_allowVisible)
        {
            if (!IsHandleCreated) CreateHandle();
            value = false;
        }

        base.SetVisibleCore(value);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        if (_launchHandled) return;
        _launchHandled = true;

        // Queued so it runs once the message loop is up, whether or not the
        // window was ever shown.
        BeginInvoke(new Action(HandleLaunch));
    }

    private void HandleLaunch()
    {
        if (!_settings.StartRoutingOnLaunch) return;

        StartRouting();

        if (Visible) return;

        if (_router.IsRunning)
            Balloon("Routing", $"Duplicating \"{_router.SourceDeviceName}\" to both outputs.", ToolTipIcon.Info);
        else
            Balloon("Routing did not start", _messageLabel.Text, ToolTipIcon.Warning);
    }

    /// <summary>Brings the settings window up; also called by a second launch.</summary>
    public void ShowFromTray()
    {
        _allowVisible = true;

        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;

        Activate();
        try { SetForegroundWindow(Handle); } catch { }
    }

    private void HideToTray()
    {
        Hide();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;

        if (_settings.TrayHintShown) return;
        _settings.TrayHintShown = true;
        _settings.Save();

        Balloon(
            "Still running",
            "AudioTwin keeps working here. Click the icon to open it, right-click to exit.",
            ToolTipIcon.Info);
    }

    private void ExitApplication()
    {
        _exiting = true;
        Close();
    }

    private void HandleFormClosing(object? sender, FormClosingEventArgs e)
    {
        // The X button is "put it away", not "quit" - unless the user said otherwise.
        if (!_exiting && e.CloseReason == CloseReason.UserClosing && _closeToTrayCheck.Checked)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _exiting = true;
        SaveSettings();
        _tray.Visible = false;
        try { _router.Dispose(); } catch { }
        try { _watcher.Dispose(); } catch { }
        _deviceRefreshTimer.Stop();
    }

    private void Balloon(string title, string text, ToolTipIcon icon)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try { _tray.ShowBalloonTip(6000, title, Shorten(text, 200), icon); } catch { }
    }

    private void SyncTrayMenu()
    {
        _menuRouting.Text = _router.IsRunning ? "Stop routing" : "Start routing";
        _menuStartWithWindows.Checked = _startWithWindowsCheck.Checked;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // A NotifyIcon that is disposed while visible leaves a ghost icon
            // behind until the user waves the mouse over it.
            try { _tray.Visible = false; } catch { }
            try { _tray.Dispose(); } catch { }
            try { _trayMenu.Dispose(); } catch { }
            try { _deviceRefreshTimer.Dispose(); } catch { }
        }

        base.Dispose(disposing);

        // Only once the form has let go of the icon it was displaying.
        if (disposing)
        {
            try { _icons.Dispose(); } catch { }
        }
    }

    // ------------------------------------------------------------- behaviour

    private void ToggleRouting()
    {
        if (_router.IsRunning)
        {
            _router.Stop();
            ShowMessage("Routing stopped.");
        }
        else
        {
            StartRouting();
        }

        UpdateStatus();
    }

    private void StartRouting()
    {
        var device1 = _output1.SelectedDevice;
        var device2 = _output2.SelectedDevice;

        if (device1 == null || device2 == null)
        {
            ShowMessage("Choose a playback device for both outputs.", isError: true);
            return;
        }

        try
        {
            _router.Start(
                device1.Id, device1.Name,
                device2.Id, device2.Name,
                _output1.Volume.Value / 100f,
                _output2.Volume.Value / 100f,
                _output1.Delay.Value,
                _output2.Delay.Value);

            ShowMessage($"Routing audio from \"{_router.SourceDeviceName}\" to both outputs.");
        }
        catch (Exception ex)
        {
            ShowMessage(ex.Message, isError: true);
            if (!Visible) Balloon("Could not start routing", ex.Message, ToolTipIcon.Error);
        }

        SaveSettings();
        UpdateStatus();
    }

    private void OnDeviceSelectionChanged()
    {
        if (_loading) return;
        SaveSettings();

        if (_router.IsRunning)
        {
            // Restart so the change takes effect immediately (this is also how
            // the user escapes a device that just disappeared).
            StartRouting();
        }
        else
        {
            UpdateStatus();
        }
    }

    private void OnSliderChanged()
    {
        UpdateSliderLabels();
        if (_loading) return;

        _router.SetVolume(1, _output1.Volume.Value / 100f);
        _router.SetVolume(2, _output2.Volume.Value / 100f);
        _router.SetDelay(1, _output1.Delay.Value);
        _router.SetDelay(2, _output2.Delay.Value);

        SaveSettings();
    }

    private void UpdateSliderLabels()
    {
        _output1.VolumeValue.Text = _output1.Volume.Value + "%";
        _output2.VolumeValue.Text = _output2.Volume.Value + "%";
        _output1.DelayValue.Text = _output1.Delay.Value + " ms";
        _output2.DelayValue.Text = _output2.Delay.Value + " ms";
    }

    private void RestartDebounce()
    {
        _deviceRefreshTimer.Stop();
        _deviceRefreshTimer.Start();
    }

    private void HandleDeviceChange()
    {
        RefreshDevices(false);

        if (!_router.IsRunning) return;

        if (_router.DefaultDeviceChanged())
        {
            ShowMessage("The Windows default playback device changed. Restarting routing...");
            StartRouting();
            return;
        }

        _router.RetryFailedOutputs();
        UpdateStatus();
    }

    private void RefreshDevices(bool applySavedSelection)
    {
        var devices = _watcher.GetPlaybackDevices();
        _defaultDeviceId = _watcher.TryGetDefaultRenderDeviceId();
        string defaultName = _watcher.TryGetDefaultRenderDeviceName() ?? "none";

        _inputLabel.Text = "Input:  Windows Default Audio  (" + Shorten(defaultName, 40) + ")";

        _loading = true;
        try
        {
            string? wanted1 = applySavedSelection ? _settings.Output1DeviceId : _output1.SelectedDevice?.Id;
            string? wanted2 = applySavedSelection ? _settings.Output2DeviceId : _output2.SelectedDevice?.Id;

            _output1.Fill(devices, wanted1, preferDefault: true, avoidId: null);
            _output2.Fill(devices, wanted2, preferDefault: false, avoidId: _output1.SelectedDevice?.Id);
        }
        finally
        {
            _loading = false;
        }

        if (devices.Count == 0)
            ShowMessage("No active playback devices found.", isError: true);
        else if (devices.Count == 1)
            ShowMessage("Only one playback device is active. Connect a second one to duplicate audio.", isError: true);

        UpdateStatus();
    }

    private void UpdateStatus()
    {
        bool running = _router.IsRunning;
        _startButton.Text = running ? "STOP ROUTING" : "START ROUTING";

        var branch1 = _router.Branch1;
        var branch2 = _router.Branch2;

        bool ok1 = branch1?.IsActive ?? false;
        bool ok2 = branch2?.IsActive ?? false;
        bool healthy = ok1 && ok2;

        if (!running)
        {
            _statusLabel.Text = "Status:  \u25CF  Stopped";
            _statusLabel.ForeColor = Color.FromArgb(110, 110, 110);
        }
        else if (healthy)
        {
            _statusLabel.Text = "Status:  \u25CF  Routing";
            _statusLabel.ForeColor = Color.FromArgb(0, 140, 60);
        }
        else
        {
            _statusLabel.Text = "Status:  \u25CF  Routing (one output is down)";
            _statusLabel.ForeColor = Color.FromArgb(200, 120, 0);
        }

        UpdatePanelNote(_output1, branch1, running);
        UpdatePanelNote(_output2, branch2, running);

        _tray.Icon = !running ? _icons.Stopped : healthy ? _icons.Running : _icons.Degraded;
        _tray.Text = Shorten(
            "AudioTwin - " +
            (!running ? "stopped" : healthy ? "routing" : "routing, one output down"),
            63);

        SyncTrayMenu();
    }

    private void UpdatePanelNote(OutputPanel panel, OutputBranch? branch, bool running)
    {
        var selected = panel.SelectedDevice;
        bool isDefaultDevice = selected != null &&
                               string.Equals(selected.Id, _defaultDeviceId, StringComparison.OrdinalIgnoreCase);

        if (running && branch != null)
        {
            if (branch.IsPassThrough)
            {
                panel.SetNote(
                    "Windows default device \u2013 already playing natively, so it is left untouched.",
                    Color.FromArgb(0, 100, 170));
                panel.SetSlidersEnabled(false);
                return;
            }

            if (branch.IsActive)
            {
                panel.SetNote("Playing.", Color.FromArgb(0, 140, 60));
                panel.SetSlidersEnabled(true);
                return;
            }

            panel.SetNote("Stopped: " + (branch.LastError ?? "device unavailable") +
                          ". It will resume automatically if it comes back.", Color.FromArgb(190, 40, 40));
            panel.SetSlidersEnabled(true);
            return;
        }

        panel.SetSlidersEnabled(true);
        panel.SetNote(
            isDefaultDevice
                ? "This is the Windows default device \u2013 it will keep playing natively."
                : string.Empty,
            Color.FromArgb(110, 110, 110));
    }

    private void ShowMessage(string message, bool isError = false)
    {
        _messageLabel.ForeColor = isError ? Color.FromArgb(190, 40, 40) : Color.FromArgb(90, 90, 90);
        _messageLabel.Text = message;
    }

    private void SaveSettings()
    {
        _settings.Output1DeviceId = _output1.SelectedDevice?.Id;
        _settings.Output2DeviceId = _output2.SelectedDevice?.Id;
        _settings.Volume1 = _output1.Volume.Value;
        _settings.Volume2 = _output2.Volume.Value;
        _settings.Delay1Ms = _output1.Delay.Value;
        _settings.Delay2Ms = _output2.Delay.Value;
        _settings.StartRoutingOnLaunch = _autoStartCheck.Checked;
        _settings.StartWithWindows = _startWithWindowsCheck.Checked;
        _settings.StartMinimized = _startMinimizedCheck.Checked;
        _settings.CloseToTray = _closeToTrayCheck.Checked;
        _settings.Save();
    }

    private void RunOnUi(Action action)
    {
        try
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(action);
        }
        catch
        {
            // The window is going away; nothing to update.
        }
    }

    private static string Shorten(string text, int max) =>
        text.Length <= max ? text : text[..(max - 1)] + "\u2026";

    // ------------------------------------------------------------ sub-layout

    /// <summary>One "Output N" group box and its controls.</summary>
    private sealed class OutputPanel
    {
        public GroupBox Group { get; } = new();
        public ComboBox Devices { get; } = new();
        public TrackBar Volume { get; } = new();
        public Label VolumeValue { get; } = new();
        public TrackBar Delay { get; } = new();
        public Label DelayValue { get; } = new();
        public Label Note { get; } = new();

        private readonly Label _volumeCaption = new();
        private readonly Label _delayCaption = new();

        public AudioDeviceInfo? SelectedDevice => Devices.SelectedItem as AudioDeviceInfo;

        public void Build(string title, Point location, Size size)
        {
            Group.Text = title;
            Group.Location = location;
            Group.Size = size;

            Devices.DropDownStyle = ComboBoxStyle.DropDownList;
            Devices.Location = new Point(12, 26);
            Devices.Size = new Size(422, 24);

            _volumeCaption.Text = "Volume";
            _volumeCaption.Location = new Point(12, 62);
            _volumeCaption.Size = new Size(55, 20);

            Volume.AutoSize = false;
            Volume.Location = new Point(68, 56);
            Volume.Size = new Size(300, 32);
            Volume.Minimum = 0;
            Volume.Maximum = 100;
            Volume.TickFrequency = 10;
            Volume.SmallChange = 1;
            Volume.LargeChange = 10;
            Volume.Value = 100;

            VolumeValue.Location = new Point(378, 62);
            VolumeValue.Size = new Size(56, 20);
            VolumeValue.TextAlign = ContentAlignment.MiddleRight;

            _delayCaption.Text = "Delay";
            _delayCaption.Location = new Point(12, 98);
            _delayCaption.Size = new Size(55, 20);

            Delay.AutoSize = false;
            Delay.Location = new Point(68, 92);
            Delay.Size = new Size(300, 32);
            Delay.Minimum = 0;
            Delay.Maximum = OutputBranch.MaxDelayMilliseconds;
            Delay.TickFrequency = 50;
            Delay.SmallChange = 5;
            Delay.LargeChange = 25;
            Delay.Value = 0;

            DelayValue.Location = new Point(378, 98);
            DelayValue.Size = new Size(56, 20);
            DelayValue.TextAlign = ContentAlignment.MiddleRight;

            Note.Location = new Point(12, 126);
            Note.Size = new Size(422, 18);
            Note.Font = new Font("Segoe UI", 8F);
            Note.ForeColor = Color.FromArgb(110, 110, 110);

            Group.Controls.AddRange(new Control[]
            {
                Devices, _volumeCaption, Volume, VolumeValue, _delayCaption, Delay, DelayValue, Note
            });
        }

        public void SetNote(string text, Color color)
        {
            Note.Text = text;
            Note.ForeColor = color;
        }

        public void SetSlidersEnabled(bool enabled)
        {
            Volume.Enabled = enabled;
            Delay.Enabled = enabled;
        }

        /// <summary>
        /// Repopulates the drop-down, keeping the current selection alive if the
        /// device is still there.
        /// </summary>
        public void Fill(IReadOnlyList<AudioDeviceInfo> devices, string? wantedId, bool preferDefault, string? avoidId)
        {
            Devices.BeginUpdate();
            try
            {
                Devices.Items.Clear();
                foreach (var device in devices) Devices.Items.Add(device);

                int index = -1;

                if (!string.IsNullOrEmpty(wantedId))
                    index = IndexOf(devices, d => string.Equals(d.Id, wantedId, StringComparison.OrdinalIgnoreCase));

                if (index < 0 && preferDefault)
                    index = IndexOf(devices, d => d.IsDefault);

                if (index < 0 && !string.IsNullOrEmpty(avoidId))
                    index = IndexOf(devices, d => !string.Equals(d.Id, avoidId, StringComparison.OrdinalIgnoreCase));

                if (index < 0 && devices.Count > 0)
                    index = 0;

                Devices.SelectedIndex = index;
            }
            finally
            {
                Devices.EndUpdate();
            }
        }

        private static int IndexOf(IReadOnlyList<AudioDeviceInfo> devices, Func<AudioDeviceInfo, bool> predicate)
        {
            for (int i = 0; i < devices.Count; i++)
                if (predicate(devices[i]))
                    return i;
            return -1;
        }
    }
}
