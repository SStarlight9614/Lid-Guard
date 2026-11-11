using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

using WpfWindow = System.Windows.Window;
using WpfStyle = System.Windows.Style;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfButton = System.Windows.Controls.Button;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfColor = System.Windows.Media.Color;
using DrawingColor = System.Drawing.Color;
using DrawingPen = System.Drawing.Pen;
using DrawingBrush = System.Drawing.Brush;
using FormsTimer = System.Windows.Forms.Timer;

[assembly: AssemblyTitle("LidGuard")]
[assembly: AssemblyDescription("Windows 11 Dock-Aware Lid Close & Modern Standby Power Automation")]
[assembly: AssemblyCompany("SStarlight9614")]
[assembly: AssemblyProduct("LidGuard")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace LidGuard
{
    public enum LidOperatingMode
    {
        AutoDetectDisplay = 0,
        ForceDoNothing = 1,
        ForceSleep = 2
    }

    public enum PowerPerformancePolicy
    {
        AutoHighPerfWhenDocked = 0,
        DisabledDoNotManage = 1,
        ForceBestPerformance = 2,
        ForceBalanced = 3
    }

    [Flags]
    public enum TrayMenuFlags
    {
        None = 0,
        ShowStatus = 1,
        ShowLidToggle = 2,
        ShowPowerToggle = 4,
        ShowRefresh = 8
    }

    public class LidGuardAppContext : ApplicationContext
    {
        private NotifyIcon _trayIcon;
        private ContextMenuStrip _menu;

        private ToolStripMenuItem _openSettingsItem;
        private ToolStripSeparator _topSep;
        private ToolStripMenuItem _statusMenuItem;
        private ToolStripMenuItem _lidQuickToggleItem;
        private ToolStripMenuItem _powerQuickToggleItem;
        private ToolStripMenuItem _refreshMenuItem;
        private ToolStripSeparator _bottomSep;
        private ToolStripMenuItem _exitItem;

        private FormsTimer _pollTimer;
        private FormsTimer _updateTimer;
        private EventWaitHandle _showGuiEvent;
        private LidGuardWpfController _wpfSettings;

        public LidOperatingMode CurrentLidMode { get; private set; }
        public PowerPerformancePolicy CurrentPowerPolicy { get; private set; }
        public bool ShowNotifications { get; private set; }
        public bool AutoCheckUpdates { get; private set; }
        public TrayMenuFlags TrayFlags { get; private set; }

        public int LastAppliedLidAction { get; private set; }
        public string LastPowerOverlayName { get; private set; }
        public bool LastExternalDisplayState { get; private set; }
        public int LastExternalCount { get; private set; }
        public string LastDisplaySummary { get; private set; }
        public bool IsPluggedIn { get; private set; }

        public UpdateInfo AvailableUpdate { get; private set; }
        public bool IsCheckingUpdate { get; private set; }
        public string UpdateStatusText { get; private set; }

        private Guid _lastAppliedOverlayGuid = new Guid("11111111-1111-1111-1111-111111111111");
        private Icon _staticAppIcon;
        private Icon _iconDoNothing;
        private Icon _iconSleep;

        private const string StartupRegKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string AppRegKey = @"Software\LidGuard";
        private const string AppName = "LidGuard";

        public event EventHandler StateUpdated;
        public event EventHandler ThemeChanged;

        public Icon StaticAppIcon
        {
            get { return _staticAppIcon; }
        }

        public LidGuardAppContext(bool openGuiOnStart)
        {
            UpdateManager.CleanupOldBinary();

            CurrentLidMode = LidOperatingMode.AutoDetectDisplay;
            CurrentPowerPolicy = PowerPerformancePolicy.AutoHighPerfWhenDocked;
            ShowNotifications = true;
            AutoCheckUpdates = true;
            TrayFlags = TrayMenuFlags.None; // Default all extra right-click tray items to OFF
            LastAppliedLidAction = -1;
            LastPowerOverlayName = "Balanced";
            LastExternalCount = -1;
            LastDisplaySummary = "Checking...";
            UpdateStatusText = "Check Now";

            LoadPreferences();

            _staticAppIcon = LoadEmbeddedStaticIcon();
            _iconDoNothing = _staticAppIcon ?? CreateStatusIcon(true);
            _iconSleep = CreateStatusIcon(false);

            BuildTrayMenu();
            ApplyTrayTheme();

            _trayIcon = new NotifyIcon
            {
                Icon = _staticAppIcon ?? _iconDoNothing,
                Text = "LidGuard",
                ContextMenuStrip = _menu,
                Visible = true
            };

            _trayIcon.DoubleClick += (s, e) => OpenSettingsGui();

            SystemEvents.DisplaySettingsChanged += OnSystemStateChanged;
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

            _pollTimer = new FormsTimer { Interval = 5000 };
            _pollTimer.Tick += (s, e) =>
            {
                if (_showGuiEvent != null && _showGuiEvent.WaitOne(0))
                {
                    OpenSettingsGui();
                }
                EvaluateAndApply(false);
            };
            _pollTimer.Start();

            // Background update check timer (checks GitHub Releases every 6 hours if enabled)
            _updateTimer = new FormsTimer { Interval = 6 * 60 * 60 * 1000 };
            _updateTimer.Tick += (s, e) =>
            {
                if (AutoCheckUpdates)
                {
                    TriggerUpdateCheck(false);
                }
            };
            _updateTimer.Start();

            try
            {
                _showGuiEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\LidGuard_ShowGui_Event");
            }
            catch { }

            EvaluateAndApply(true);

            if (openGuiOnStart)
            {
                OpenSettingsGui();
            }

            if (AutoCheckUpdates)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(10000);
                    if (AutoCheckUpdates)
                    {
                        TriggerUpdateCheck(false);
                    }
                });
            }
        }

        private Icon LoadEmbeddedStaticIcon()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LidGuard.ico"))
                {
                    if (s != null)
                    {
                        return new Icon(s, new Size(64, 64));
                    }
                }
            }
            catch { }

            try
            {
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { }

            return CreateStatusIcon(true);
        }

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            ApplyTrayTheme();
            if (ThemeChanged != null)
            {
                ThemeChanged(this, EventArgs.Empty);
            }
        }

        private void BuildTrayMenu()
        {
            _menu = new ContextMenuStrip
            {
                ShowImageMargin = true,
                Font = Win11TrayStyle.CreateFont(9.5f, FontStyle.Regular)
            };

            _openSettingsItem = new ToolStripMenuItem("Open Settings", null, (s, e) => OpenSettingsGui())
            {
                Font = Win11TrayStyle.CreateFont(9.5f, FontStyle.Bold),
                Padding = new Padding(6, 4, 6, 4)
            };

            _topSep = new ToolStripSeparator();

            _statusMenuItem = new ToolStripMenuItem("Status: Checking...")
            {
                Enabled = false,
                Padding = new Padding(6, 2, 6, 2)
            };

            _lidQuickToggleItem = new ToolStripMenuItem("Auto Lid Close (Docked)", null, (s, e) =>
            {
                if (CurrentLidMode == LidOperatingMode.AutoDetectDisplay)
                    SetLidMode(LidOperatingMode.ForceSleep);
                else
                    SetLidMode(LidOperatingMode.AutoDetectDisplay);
            })
            {
                Padding = new Padding(6, 3, 6, 3)
            };

            _powerQuickToggleItem = new ToolStripMenuItem("Auto High Power Mode", null, (s, e) =>
            {
                if (CurrentPowerPolicy == PowerPerformancePolicy.AutoHighPerfWhenDocked)
                    SetPowerPolicy(PowerPerformancePolicy.DisabledDoNotManage);
                else
                    SetPowerPolicy(PowerPerformancePolicy.AutoHighPerfWhenDocked);
            })
            {
                Padding = new Padding(6, 3, 6, 3)
            };

            _refreshMenuItem = new ToolStripMenuItem("Refresh Displays", null, (s, e) => EvaluateAndApply(true))
            {
                Padding = new Padding(6, 3, 6, 3)
            };

            _bottomSep = new ToolStripSeparator();

            _exitItem = new ToolStripMenuItem("Exit", null, (s, e) => ExitApp())
            {
                Padding = new Padding(6, 4, 6, 4)
            };

            _menu.Items.Add(_openSettingsItem);
            _menu.Items.Add(_topSep);
            _menu.Items.Add(_statusMenuItem);
            _menu.Items.Add(_lidQuickToggleItem);
            _menu.Items.Add(_powerQuickToggleItem);
            _menu.Items.Add(_refreshMenuItem);
            _menu.Items.Add(_bottomSep);
            _menu.Items.Add(_exitItem);

            UpdateTrayMenuVisibility();
        }

        private void ApplyTrayTheme()
        {
            bool isDark = ThemeManager.IsSystemDarkTheme();
            WpfColor accent = ThemeManager.GetSystemAccentColor();
            DrawingColor drawAccent = DrawingColor.FromArgb(accent.R, accent.G, accent.B);

            _menu.Renderer = new Win11MenuRenderer(isDark, drawAccent);
            DrawingColor bg = isDark ? DrawingColor.FromArgb(43, 43, 43) : DrawingColor.FromArgb(251, 251, 251);
            DrawingColor fg = isDark ? DrawingColor.FromArgb(255, 255, 255) : DrawingColor.FromArgb(26, 26, 26);

            _menu.BackColor = bg;
            _menu.ForeColor = fg;
            foreach (ToolStripItem item in _menu.Items)
            {
                item.ForeColor = fg;
            }
        }

        public void UpdateTrayMenuVisibility()
        {
            bool showStatus = (TrayFlags & TrayMenuFlags.ShowStatus) != 0;
            bool showLid = (TrayFlags & TrayMenuFlags.ShowLidToggle) != 0;
            bool showPower = (TrayFlags & TrayMenuFlags.ShowPowerToggle) != 0;
            bool showRefresh = (TrayFlags & TrayMenuFlags.ShowRefresh) != 0;

            bool anyOptional = showStatus || showLid || showPower || showRefresh;

            _topSep.Visible = anyOptional;
            _statusMenuItem.Visible = showStatus;
            _lidQuickToggleItem.Visible = showLid;
            _powerQuickToggleItem.Visible = showPower;
            _refreshMenuItem.Visible = showRefresh;
            _bottomSep.Visible = true;

            _lidQuickToggleItem.Checked = (CurrentLidMode == LidOperatingMode.AutoDetectDisplay);
            _powerQuickToggleItem.Checked = (CurrentPowerPolicy == PowerPerformancePolicy.AutoHighPerfWhenDocked);
        }

        public void SetTrayFlag(TrayMenuFlags flag, bool enabled)
        {
            if (enabled)
                TrayFlags |= flag;
            else
                TrayFlags &= ~flag;

            UpdateTrayMenuVisibility();
            SavePreferences();
            if (StateUpdated != null) StateUpdated(this, EventArgs.Empty);
        }

        public void OpenSettingsGui()
        {
            if (_wpfSettings == null || _wpfSettings.IsClosed)
            {
                _wpfSettings = new LidGuardWpfController(this);
            }
            _wpfSettings.ShowAndActivate();
        }

        public void SetLidMode(LidOperatingMode mode)
        {
            CurrentLidMode = mode;
            UpdateTrayMenuVisibility();
            SavePreferences();
            EvaluateAndApply(true);
        }

        public void SetPowerPolicy(PowerPerformancePolicy policy)
        {
            CurrentPowerPolicy = policy;
            UpdateTrayMenuVisibility();
            SavePreferences();
            EvaluateAndApply(true);
        }

        public void SetNotifications(bool enabled)
        {
            ShowNotifications = enabled;
            SavePreferences();
            if (StateUpdated != null) StateUpdated(this, EventArgs.Empty);
        }

        public void SetAutoCheckUpdates(bool enabled)
        {
            AutoCheckUpdates = enabled;
            SavePreferences();
            if (StateUpdated != null) StateUpdated(this, EventArgs.Empty);
        }

        public void TriggerUpdateCheck(bool isManualUserClick)
        {
            if (IsCheckingUpdate) return;

            if (isManualUserClick && AvailableUpdate != null && AvailableUpdate.IsNewer)
            {
                InstallAvailableUpdate();
                return;
            }

            IsCheckingUpdate = true;
            UpdateStatusText = "Checking...";
            if (StateUpdated != null) StateUpdated(this, EventArgs.Empty);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                UpdateInfo info = UpdateManager.CheckForUpdate();
                IsCheckingUpdate = false;

                if (info != null && info.IsNewer)
                {
                    AvailableUpdate = info;
                    UpdateStatusText = "Install v" + info.LatestVersion;
                    if (StateUpdated != null) StateUpdated(this, EventArgs.Empty);

                    if (isManualUserClick)
                    {
                        DialogResult dr = MessageBox.Show(
                            string.Format("LidGuard v{0} is available on GitHub Releases (current: v{1}).\n\n{2}\n\nDownload and install now?",
                                info.LatestVersion, UpdateManager.CurrentVersion, info.Notes ?? "New release available."),
                            "LidGuard Update Available",
                            MessageBoxButtons.YesNo,
                            MessageBoxIcon.Information);
                        if (dr == DialogResult.Yes)
                        {
                            InstallAvailableUpdate();
                        }
                    }
                    else if (ShowNotifications)
                    {
                        _trayIcon.ShowBalloonTip(3500,
                            "LidGuard Update Available (v" + info.LatestVersion + ")",
                            "Open LidGuard Settings to install the update with one click.",
                            ToolTipIcon.Info);
                    }
                }
                else
                {
                    AvailableUpdate = null;
                    UpdateStatusText = "Up to Date";
                    if (StateUpdated != null) StateUpdated(this, EventArgs.Empty);

                    if (isManualUserClick)
                    {
                        MessageBox.Show(
                            "You are running the latest release of LidGuard (v" + UpdateManager.CurrentVersion + ").",
                            "LidGuard Up to Date",
                            MessageBoxButtons.OK,
                            MessageBoxIcon.Information);
                    }

                    Thread.Sleep(3000);
                    if (AvailableUpdate == null && !IsCheckingUpdate)
                    {
                        UpdateStatusText = "Check Now";
                        if (StateUpdated != null) StateUpdated(this, EventArgs.Empty);
                    }
                }
            });
        }

        private void InstallAvailableUpdate()
        {
            if (AvailableUpdate == null || IsCheckingUpdate) return;

            IsCheckingUpdate = true;
            UpdateStatusText = "Updating...";
            if (StateUpdated != null) StateUpdated(this, EventArgs.Empty);

            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err;
                bool ok = UpdateManager.DownloadAndApplyUpdate(AvailableUpdate, out err);
                if (ok)
                {
                    try
                    {
                        Process.Start(Application.ExecutablePath);
                    }
                    catch { }
                    ExitApp();
                }
                else
                {
                    IsCheckingUpdate = false;
                    UpdateStatusText = "Install v" + AvailableUpdate.LatestVersion;
                    if (StateUpdated != null) StateUpdated(this, EventArgs.Empty);
                    MessageBox.Show("Update failed: " + err, "LidGuard Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            });
        }

        private void OnSystemStateChanged(object sender, EventArgs e)
        {
            EvaluateAndApply(false);
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.StatusChange || e.Mode == PowerModes.Resume)
            {
                EvaluateAndApply(false);
            }
        }

        public void EvaluateAndApply(bool forceNotify)
        {
            int externalCount;
            string displaySummary;
            bool isProjecting = DisplayDetector.HasExternalDisplay(out externalCount, out displaySummary);
            bool isAcOnline = (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online);

            IsPluggedIn = isAcOnline;
            LastExternalDisplayState = isProjecting;
            LastExternalCount = externalCount;
            LastDisplaySummary = displaySummary;

            int desiredLidAction;
            switch (CurrentLidMode)
            {
                case LidOperatingMode.ForceDoNothing:
                    desiredLidAction = 0;
                    break;
                case LidOperatingMode.ForceSleep:
                    desiredLidAction = 1;
                    break;
                case LidOperatingMode.AutoDetectDisplay:
                default:
                    desiredLidAction = isProjecting ? 0 : 1;
                    break;
            }

            bool lidChanged = (desiredLidAction != LastAppliedLidAction);
            int currentSystemLid = PowerManager.GetPluggedInLidAction();
            if (lidChanged || currentSystemLid != desiredLidAction || forceNotify)
            {
                PowerManager.SetPluggedInLidAction(desiredLidAction);
            }

            bool powerChanged = false;
            Guid? desiredOverlay = null;
            switch (CurrentPowerPolicy)
            {
                case PowerPerformancePolicy.AutoHighPerfWhenDocked:
                    desiredOverlay = (isProjecting && isAcOnline)
                        ? PowerManager.GUID_OVERLAY_BEST_PERFORMANCE
                        : PowerManager.GUID_OVERLAY_BALANCED;
                    break;
                case PowerPerformancePolicy.ForceBestPerformance:
                    desiredOverlay = PowerManager.GUID_OVERLAY_BEST_PERFORMANCE;
                    break;
                case PowerPerformancePolicy.ForceBalanced:
                    desiredOverlay = PowerManager.GUID_OVERLAY_BALANCED;
                    break;
                case PowerPerformancePolicy.DisabledDoNotManage:
                default:
                    desiredOverlay = null;
                    break;
            }

            if (desiredOverlay.HasValue)
            {
                Guid targetGuid = desiredOverlay.Value;
                Guid currentEffective = PowerManager.GetEffectiveOverlayScheme();
                if (targetGuid != _lastAppliedOverlayGuid || currentEffective != targetGuid || forceNotify)
                {
                    PowerManager.SetOverlayScheme(targetGuid);
                    powerChanged = (targetGuid != _lastAppliedOverlayGuid);
                    _lastAppliedOverlayGuid = targetGuid;
                }
            }

            Guid activeOverlay = PowerManager.GetEffectiveOverlayScheme();
            string overlayLabel = PowerManager.GetOverlayFriendlyName(activeOverlay);
            if (desiredOverlay.HasValue && activeOverlay != desiredOverlay.Value)
            {
                overlayLabel = PowerManager.GetOverlayFriendlyName(desiredOverlay.Value);
            }
            LastPowerOverlayName = overlayLabel;

            bool isDoNothing = (desiredLidAction == 0);
            _trayIcon.Icon = _staticAppIcon ?? (isDoNothing ? _iconDoNothing : _iconSleep);

            string shortLid = isDoNothing ? "Awake" : "Sleep";
            string shortPower = overlayLabel.Contains("Best Performance") ? "High Power" : "Balanced";
            _statusMenuItem.Text = string.Format("Lid: {0}  •  {1}", shortLid, shortPower);

            string tooltip = string.Format("LidGuard — {0}\nPower: {1}\n{2}",
                isDoNothing ? "Lid: Do Nothing" : "Lid: Sleep",
                shortPower,
                displaySummary);
            if (tooltip.Length >= 63)
            {
                tooltip = tooltip.Substring(0, 62);
            }
            _trayIcon.Text = tooltip;

            if (ShowNotifications && (LastAppliedLidAction != -1 || forceNotify) && (lidChanged || powerChanged || forceNotify))
            {
                string title = isDoNothing ? "Docked Mode Active" : "Portable Mode Active";
                string body = string.Format("Lid Close: {0}  |  Power: {1}\n{2}",
                    isDoNothing ? "Do Nothing" : "Sleep", shortPower, displaySummary);

                _trayIcon.ShowBalloonTip(2500, title, body, ToolTipIcon.Info);
            }

            LastAppliedLidAction = desiredLidAction;
            UpdateTrayMenuVisibility();

            if (StateUpdated != null)
            {
                StateUpdated(this, EventArgs.Empty);
            }
        }

        private Icon CreateStatusIcon(bool isDoNothing)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(DrawingColor.Transparent);

                DrawingColor badgeColor = isDoNothing
                    ? DrawingColor.FromArgb(16, 185, 129)
                    : DrawingColor.FromArgb(0, 103, 192);
                using (DrawingBrush bgBrush = new SolidBrush(badgeColor))
                {
                    g.FillEllipse(bgBrush, 1, 1, 30, 30);
                }

                using (DrawingPen whitePen = new DrawingPen(DrawingColor.White, 2.2f))
                {
                    g.DrawRectangle(whitePen, 7, 8, 18, 12);
                    g.DrawLine(whitePen, 13, 23, 19, 23);
                    g.DrawLine(whitePen, 16, 20, 16, 23);

                    if (isDoNothing)
                    {
                        g.DrawLine(whitePen, 12, 14, 15, 17);
                        g.DrawLine(whitePen, 15, 17, 21, 11);
                    }
                    else
                    {
                        g.DrawLine(whitePen, 13, 11, 19, 11);
                        g.DrawLine(whitePen, 19, 11, 13, 17);
                        g.DrawLine(whitePen, 13, 17, 19, 17);
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                return Icon.FromHandle(hIcon);
            }
        }

        public bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRegKey, false))
                {
                    if (key == null) return false;
                    object val = key.GetValue(AppName);
                    return val != null;
                }
            }
            catch
            {
                return false;
            }
        }

        public void SetStartupEnabled(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRegKey, true))
                {
                    if (key == null) return;
                    if (!enable)
                    {
                        key.DeleteValue(AppName, false);
                    }
                    else
                    {
                        string exePath = "\"" + Application.ExecutablePath + "\" --minimized";
                        key.SetValue(AppName, exePath);
                    }
                }
                if (StateUpdated != null) StateUpdated(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not update startup setting: " + ex.Message, "LidGuard", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void LoadPreferences()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(AppRegKey, false))
                {
                    if (key != null)
                    {
                        object modeVal = key.GetValue("Mode");
                        if (modeVal is int && Enum.IsDefined(typeof(LidOperatingMode), (int)modeVal))
                        {
                            CurrentLidMode = (LidOperatingMode)(int)modeVal;
                        }

                        object powerVal = key.GetValue("PowerPolicy");
                        if (powerVal is int && Enum.IsDefined(typeof(PowerPerformancePolicy), (int)powerVal))
                        {
                            CurrentPowerPolicy = (PowerPerformancePolicy)(int)powerVal;
                        }

                        object notifyVal = key.GetValue("Notify");
                        if (notifyVal is int)
                        {
                            ShowNotifications = ((int)notifyVal) != 0;
                        }

                        object autoUpdVal = key.GetValue("AutoCheckUpdates");
                        if (autoUpdVal is int)
                        {
                            AutoCheckUpdates = ((int)autoUpdVal) != 0;
                        }

                        object trayVal = key.GetValue("TrayFlags");
                        if (trayVal is int)
                        {
                            TrayFlags = (TrayMenuFlags)(int)trayVal;
                        }
                    }
                }
            }
            catch { }
        }

        private void SavePreferences()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(AppRegKey))
                {
                    if (key != null)
                    {
                        key.SetValue("Mode", (int)CurrentLidMode, RegistryValueKind.DWord);
                        key.SetValue("PowerPolicy", (int)CurrentPowerPolicy, RegistryValueKind.DWord);
                        key.SetValue("Notify", ShowNotifications ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("AutoCheckUpdates", AutoCheckUpdates ? 1 : 0, RegistryValueKind.DWord);
                        key.SetValue("TrayFlags", (int)TrayFlags, RegistryValueKind.DWord);
                    }
                }
            }
            catch { }
        }

        public void ExitApp()
        {
            _pollTimer.Stop();
            _updateTimer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnSystemStateChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            if (_wpfSettings != null)
            {
                _wpfSettings.ForceClose();
            }
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            Application.Exit();
        }
    }

    // =========================================================================
    // Native Windows Shortcut (.lnk) Manager
    // =========================================================================
    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    internal class ShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMaxPath, IntPtr pfd, int fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, int dwReserved);
        void Resolve(IntPtr hwnd, int fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    public static class ShortcutManager
    {
        public static string GetStartMenuShortcutPath()
        {
            string programsDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
            return Path.Combine(programsDir, "LidGuard.lnk");
        }

        public static string GetCommonStartMenuShortcutPath()
        {
            string commonPrograms = Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);
            return Path.Combine(commonPrograms, "LidGuard.lnk");
        }

        public static string GetDesktopShortcutPath()
        {
            string desktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return Path.Combine(desktopDir, "LidGuard.lnk");
        }

        public static bool HasStartMenuShortcut()
        {
            return File.Exists(GetStartMenuShortcutPath()) || File.Exists(GetCommonStartMenuShortcutPath());
        }

        public static bool HasDesktopShortcut()
        {
            return File.Exists(GetDesktopShortcutPath());
        }

        public static void SetStartMenuShortcut(bool enable, string targetExePath)
        {
            string userLnk = GetStartMenuShortcutPath();
            string commonLnk = GetCommonStartMenuShortcutPath();
            if (enable)
            {
                CreateShortcut(userLnk, targetExePath, "LidGuard — Dock-Aware Lid & Power Automation");
            }
            else
            {
                if (File.Exists(userLnk)) { try { File.Delete(userLnk); } catch { } }
                if (File.Exists(commonLnk)) { try { File.Delete(commonLnk); } catch { } }
            }
        }

        public static void SetDesktopShortcut(bool enable, string targetExePath)
        {
            string lnkPath = GetDesktopShortcutPath();
            if (enable)
            {
                CreateShortcut(lnkPath, targetExePath, "LidGuard — Dock-Aware Lid & Power Automation");
            }
            else if (File.Exists(lnkPath))
            {
                try { File.Delete(lnkPath); } catch { }
            }
        }

        public static void CreateShortcut(string shortcutPath, string targetExePath, string description)
        {
            try
            {
                string parentDir = Path.GetDirectoryName(shortcutPath);
                if (!Directory.Exists(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                IShellLinkW link = (IShellLinkW)new ShellLink();
                link.SetPath(targetExePath);
                link.SetWorkingDirectory(Path.GetDirectoryName(targetExePath));
                link.SetDescription(description);
                link.SetIconLocation(targetExePath, 0);

                IPersistFile file = (IPersistFile)link;
                file.Save(shortcutPath, false);
            }
            catch { }
        }
    }

    // =========================================================================
    // GitHub Releases Auto-Update Manager (Manual Release Driven)
    // =========================================================================
    public class UpdateInfo
    {
        public string LatestVersion;
        public string DownloadUrl;
        public string Notes;
        public bool IsNewer;
        public string ErrorMessage;
    }

    public static class UpdateManager
    {
        public const string CurrentVersion = "1.0.0";
        public const string DefaultGitHubRepo = "SStarlight9614/Lid-Guard";
        public const string FallbackGitHubRepo = "SStarlight9614/LidGuard";

        public static void CleanupOldBinary()
        {
            try
            {
                string exeDir = Path.GetDirectoryName(Application.ExecutablePath);
                string old1 = Path.Combine(exeDir, "LidGuard.old.exe");
                string old2 = Application.ExecutablePath + ".old";
                if (File.Exists(old1)) File.Delete(old1);
                if (File.Exists(old2)) File.Delete(old2);
            }
            catch { }
        }

        public static string GetConfiguredRepo()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(LidGuardAppContext.AppRegKey, false))
                {
                    if (key != null)
                    {
                        string val = key.GetValue("UpdateRepo") as string;
                        if (!string.IsNullOrEmpty(val))
                            return val.Trim();
                    }
                }
            }
            catch { }

            return DefaultGitHubRepo;
        }

        public static void SetConfiguredRepo(string ownerRepo)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(LidGuardAppContext.AppRegKey))
                {
                    if (key != null)
                    {
                        key.SetValue("UpdateRepo", ownerRepo.Trim(), RegistryValueKind.String);
                    }
                }
            }
            catch { }
        }

        public static UpdateInfo CheckForUpdate()
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            }
            catch { }

            string primaryRepo = GetConfiguredRepo();
            string[] reposToTry = string.Equals(primaryRepo, DefaultGitHubRepo, StringComparison.OrdinalIgnoreCase)
                ? new[] { DefaultGitHubRepo, FallbackGitHubRepo }
                : new[] { primaryRepo };

            foreach (string repo in reposToTry)
            {
                try
                {
                    string apiUrl = string.Format("https://api.github.com/repos/{0}/releases/latest", repo);
                    string json = DownloadStringWithUA(apiUrl);
                    if (!string.IsNullOrEmpty(json))
                    {
                        string tag = CleanVersion(ExtractJsonString(json, "tag_name"));
                        string dlUrl = ExtractJsonString(json, "browser_download_url");
                        string body = ExtractJsonString(json, "body");

                        if (!string.IsNullOrEmpty(tag) && !string.IsNullOrEmpty(dlUrl))
                        {
                            bool newer = IsVersionNewer(CurrentVersion, tag);
                            return new UpdateInfo
                            {
                                LatestVersion = tag,
                                DownloadUrl = dlUrl,
                                Notes = body,
                                IsNewer = newer
                            };
                        }
                    }
                }
                catch { }
            }

            return new UpdateInfo { LatestVersion = CurrentVersion, IsNewer = false };
        }

        public static bool DownloadAndApplyUpdate(UpdateInfo info, out string errorMessage)
        {
            errorMessage = null;
            if (info == null || string.IsNullOrEmpty(info.DownloadUrl))
            {
                errorMessage = "Invalid update download URL.";
                return false;
            }

            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                string exePath = Application.ExecutablePath;
                string tempExe = Path.Combine(Path.GetTempPath(), "LidGuard.exe.update");

                using (WebClient wc = new WebClient())
                {
                    wc.Headers.Add("User-Agent", "LidGuard-Updater/" + CurrentVersion);
                    wc.DownloadFile(info.DownloadUrl, tempExe);
                }

                FileInfo fi = new FileInfo(tempExe);
                if (!fi.Exists || fi.Length < 8192)
                {
                    if (fi.Exists) fi.Delete();
                    errorMessage = "Downloaded executable was incomplete or invalid.";
                    return false;
                }

                byte[] header = new byte[2];
                using (FileStream fs = File.OpenRead(tempExe))
                {
                    fs.Read(header, 0, 2);
                }
                if (header[0] != (byte)'M' || header[1] != (byte)'Z')
                {
                    File.Delete(tempExe);
                    errorMessage = "Downloaded file is not a valid Windows executable.";
                    return false;
                }

                string exeDir = Path.GetDirectoryName(exePath);
                string oldExe = Path.Combine(exeDir, "LidGuard.old.exe");
                if (File.Exists(oldExe))
                {
                    try { File.Delete(oldExe); } catch { }
                }
                File.Move(exePath, oldExe);
                File.Move(tempExe, exePath);
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private static string DownloadStringWithUA(string url)
        {
            using (WebClient wc = new WebClient())
            {
                wc.Headers.Add("User-Agent", "LidGuard-Updater/" + CurrentVersion);
                wc.Headers.Add("Cache-Control", "no-cache");
                return wc.DownloadString(url);
            }
        }

        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            string pattern = "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"([^\"]*)\"";
            Match m = Regex.Match(json, pattern, RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static string CleanVersion(string v)
        {
            if (string.IsNullOrEmpty(v)) return null;
            v = v.Trim();
            if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase))
                v = v.Substring(1).Trim();
            return v;
        }

        public static bool IsVersionNewer(string currentVer, string remoteVer)
        {
            try
            {
                Version vCur = new Version(CleanVersion(currentVer));
                Version vRem = new Version(CleanVersion(remoteVer));
                return vRem > vCur;
            }
            catch
            {
                return !string.Equals(CleanVersion(currentVer), CleanVersion(remoteVer), StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    // =========================================================================
    // Real Windows 11 DWM Mica + System Dark/Light + System Accent Manager
    // =========================================================================
    public static class ThemeManager
    {
        private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        private const string DwmKey = @"Software\Microsoft\Windows\DWM";
        private const string ExplorerAccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMWA_MICA_EFFECT = 1029;

        private const int DWMSBT_MAINWINDOW = 2; // Real Mica
        private const int DWMWCP_ROUND = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

        public static bool IsSystemDarkTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("AppsUseLightTheme");
                        if (val is int)
                        {
                            return ((int)val) == 0;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        public static WpfColor GetSystemAccentColor()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(DwmKey, false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("AccentColor");
                        if (val is int)
                        {
                            uint abgr = unchecked((uint)(int)val);
                            byte r = (byte)(abgr & 0xFF);
                            byte g = (byte)((abgr >> 8) & 0xFF);
                            byte b = (byte)((abgr >> 16) & 0xFF);
                            return WpfColor.FromRgb(r, g, b);
                        }
                    }
                }
            }
            catch { }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(ExplorerAccentKey, false))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("AccentColorMenu");
                        if (val is int)
                        {
                            uint abgr = unchecked((uint)(int)val);
                            byte r = (byte)(abgr & 0xFF);
                            byte g = (byte)((abgr >> 8) & 0xFF);
                            byte b = (byte)((abgr >> 16) & 0xFF);
                            return WpfColor.FromRgb(r, g, b);
                        }
                    }
                }
            }
            catch { }

            return WpfColor.FromRgb(0, 103, 192);
        }

        private static WpfColor AdjustBrightness(WpfColor c, int delta)
        {
            int r = Math.Max(0, Math.Min(255, c.R + delta));
            int g = Math.Max(0, Math.Min(255, c.G + delta));
            int b = Math.Max(0, Math.Min(255, c.B + delta));
            return WpfColor.FromRgb((byte)r, (byte)g, (byte)b);
        }

        public static bool ApplyMicaAndThemeToWindow(WpfWindow window, bool isDark)
        {
            bool micaApplied = false;
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    HwndSource src = HwndSource.FromHwnd(hwnd);
                    if (src != null && src.CompositionTarget != null)
                    {
                        src.CompositionTarget.BackgroundColor = Colors.Transparent;
                    }

                    MARGINS margins = new MARGINS
                    {
                        cxLeftWidth = -1,
                        cxRightWidth = -1,
                        cyTopHeight = -1,
                        cyBottomHeight = -1
                    };
                    DwmExtendFrameIntoClientArea(hwnd, ref margins);

                    int darkVal = isDark ? 1 : 0;
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkVal, sizeof(int));
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref darkVal, sizeof(int));

                    int roundVal = DWMWCP_ROUND;
                    DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref roundVal, sizeof(int));

                    int backdropType = DWMSBT_MAINWINDOW; // 2 = Mica
                    int res = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
                    if (res == 0)
                    {
                        micaApplied = true;
                    }
                    else
                    {
                        int micaVal = 1;
                        if (DwmSetWindowAttribute(hwnd, DWMWA_MICA_EFFECT, ref micaVal, sizeof(int)) == 0)
                        {
                            micaApplied = true;
                        }
                    }
                }
            }
            catch { }

            UpdateResourceBrushes(window, isDark, micaApplied);
            return micaApplied;
        }

        public static void UpdateResourceBrushes(WpfWindow window, bool isDark, bool micaEnabled)
        {
            var res = window.Resources;
            WpfColor accent = GetSystemAccentColor();
            WpfColor accentHover = AdjustBrightness(accent, 18);
            WpfColor accentPressed = AdjustBrightness(accent, -14);

            res["AccentBrush"] = new SolidColorBrush(accent);
            res["AccentHoverBrush"] = new SolidColorBrush(accentHover);
            res["AccentPressedBrush"] = new SolidColorBrush(accentPressed);
            res["OnAccentBrush"] = new SolidColorBrush(Colors.White);

            if (isDark)
            {
                res["WindowFallbackBrush"] = micaEnabled
                    ? new SolidColorBrush(Colors.Transparent)
                    : new SolidColorBrush(WpfColor.FromRgb(32, 32, 32));

                res["CardBgBrush"] = new SolidColorBrush(WpfColor.FromRgb(43, 43, 43));         // Solid #2B2B2B
                res["CardBorderBrush"] = new SolidColorBrush(WpfColor.FromRgb(58, 58, 58));     // #3A3A3A
                res["RowDividerBrush"] = new SolidColorBrush(WpfColor.FromRgb(54, 54, 54));     // #363636

                res["TextPrimaryBrush"] = new SolidColorBrush(WpfColor.FromRgb(255, 255, 255));
                res["TextSecondaryBrush"] = new SolidColorBrush(WpfColor.FromRgb(197, 197, 197));

                res["ControlFillBrush"] = new SolidColorBrush(WpfColor.FromRgb(53, 53, 53));
                res["ControlFillHoverBrush"] = new SolidColorBrush(WpfColor.FromRgb(64, 64, 64));
                res["ControlFillPressedBrush"] = new SolidColorBrush(WpfColor.FromRgb(48, 48, 48));
                res["ControlBorderBrush"] = new SolidColorBrush(WpfColor.FromRgb(74, 74, 74));

                res["SwitchTrackOffBrush"] = new SolidColorBrush(WpfColor.FromRgb(43, 43, 43));
                res["SwitchBorderOffBrush"] = new SolidColorBrush(WpfColor.FromRgb(158, 158, 158));
                res["SwitchThumbOffBrush"] = new SolidColorBrush(WpfColor.FromRgb(206, 206, 206));

                res["BadgeBorderBrush"] = new SolidColorBrush(WpfColor.FromRgb(138, 138, 138));
                res["BadgeTextBrush"] = new SolidColorBrush(WpfColor.FromRgb(185, 185, 185));
                res["BadgeHoverBgBrush"] = new SolidColorBrush(WpfColor.FromRgb(58, 58, 58));

                res["PopupBgBrush"] = new SolidColorBrush(WpfColor.FromRgb(43, 43, 43));
                res["PopupBorderBrush"] = new SolidColorBrush(WpfColor.FromRgb(70, 70, 70));
            }
            else
            {
                res["WindowFallbackBrush"] = micaEnabled
                    ? new SolidColorBrush(Colors.Transparent)
                    : new SolidColorBrush(WpfColor.FromRgb(243, 243, 243));

                res["CardBgBrush"] = new SolidColorBrush(WpfColor.FromRgb(251, 251, 251));      // Solid #FBFBFB
                res["CardBorderBrush"] = new SolidColorBrush(WpfColor.FromRgb(229, 229, 229));  // #E5E5E5
                res["RowDividerBrush"] = new SolidColorBrush(WpfColor.FromRgb(234, 234, 234));  // #EAEAEA

                res["TextPrimaryBrush"] = new SolidColorBrush(WpfColor.FromRgb(26, 26, 26));
                res["TextSecondaryBrush"] = new SolidColorBrush(WpfColor.FromRgb(96, 96, 96));

                res["ControlFillBrush"] = new SolidColorBrush(WpfColor.FromRgb(251, 251, 251));
                res["ControlFillHoverBrush"] = new SolidColorBrush(WpfColor.FromRgb(242, 242, 242));
                res["ControlFillPressedBrush"] = new SolidColorBrush(WpfColor.FromRgb(234, 234, 234));
                res["ControlBorderBrush"] = new SolidColorBrush(WpfColor.FromRgb(212, 212, 212));

                res["SwitchTrackOffBrush"] = new SolidColorBrush(WpfColor.FromRgb(245, 245, 245));
                res["SwitchBorderOffBrush"] = new SolidColorBrush(WpfColor.FromRgb(134, 134, 134));
                res["SwitchThumbOffBrush"] = new SolidColorBrush(WpfColor.FromRgb(93, 93, 93));

                res["BadgeBorderBrush"] = new SolidColorBrush(WpfColor.FromRgb(140, 140, 140));
                res["BadgeTextBrush"] = new SolidColorBrush(WpfColor.FromRgb(110, 110, 110));
                res["BadgeHoverBgBrush"] = new SolidColorBrush(WpfColor.FromRgb(238, 238, 238));

                res["PopupBgBrush"] = new SolidColorBrush(WpfColor.FromRgb(251, 251, 251));
                res["PopupBorderBrush"] = new SolidColorBrush(WpfColor.FromRgb(218, 218, 218));
            }
        }
    }

    // =========================================================================
    // WPF Window Controller
    // =========================================================================
    public class LidGuardWpfController
    {
        private readonly LidGuardAppContext _ctx;
        private WpfWindow _window;
        private bool _updatingUi;
        private bool _allowClose;
        public bool IsClosed { get; private set; }

        private WpfTextBlock _txtVersionTag;
        private WpfEllipse _statusDot;
        private WpfTextBlock _txtStatusHeadline;
        private WpfTextBlock _txtStatusSub;

        private WpfComboBox _cmbLidBehavior;
        private WpfCheckBox _tglAutoPower;
        private WpfCheckBox _tglNotifications;
        private WpfCheckBox _tglStartup;
        private WpfCheckBox _tglStartMenuShortcut;
        private WpfCheckBox _tglDesktopShortcut;
        private WpfCheckBox _tglAutoUpdates;
        private WpfButton _btnCheckUpdates;

        private WpfCheckBox _tglTrayStatus;
        private WpfCheckBox _tglTrayLid;
        private WpfCheckBox _tglTrayPower;
        private WpfCheckBox _tglTrayRefresh;

        private WpfButton _btnRefresh;
        private WpfButton _btnWinPower;
        private WpfButton _btnDone;

        public LidGuardWpfController(LidGuardAppContext ctx)
        {
            _ctx = ctx;
            BuildWindowFromXaml();
            _ctx.StateUpdated += OnContextStateUpdated;
            _ctx.ThemeChanged += OnSystemThemeChanged;
        }

        private void BuildWindowFromXaml()
        {
            string xamlContent = LoadEmbeddedXamlContent();
            _window = (WpfWindow)XamlReader.Parse(xamlContent);

            bool isDark = ThemeManager.IsSystemDarkTheme();
            ThemeManager.UpdateResourceBrushes(_window, isDark, false);

            SetStaticWindowIcons();

            _window.SourceInitialized += (s, e) =>
            {
                bool darkNow = ThemeManager.IsSystemDarkTheme();
                ThemeManager.ApplyMicaAndThemeToWindow(_window, darkNow);
            };

            _txtVersionTag = (WpfTextBlock)_window.FindName("TxtVersionTag");
            _statusDot = (WpfEllipse)_window.FindName("StatusDot");
            _txtStatusHeadline = (WpfTextBlock)_window.FindName("TxtStatusHeadline");
            _txtStatusSub = (WpfTextBlock)_window.FindName("TxtStatusSub");

            _cmbLidBehavior = (WpfComboBox)_window.FindName("CmbLidBehavior");
            _tglAutoPower = (WpfCheckBox)_window.FindName("TglAutoPower");
            _tglNotifications = (WpfCheckBox)_window.FindName("TglNotifications");
            _tglStartup = (WpfCheckBox)_window.FindName("TglStartup");
            _tglStartMenuShortcut = (WpfCheckBox)_window.FindName("TglStartMenuShortcut");
            _tglDesktopShortcut = (WpfCheckBox)_window.FindName("TglDesktopShortcut");
            _tglAutoUpdates = (WpfCheckBox)_window.FindName("TglAutoUpdates");
            _btnCheckUpdates = (WpfButton)_window.FindName("BtnCheckUpdates");

            _tglTrayStatus = (WpfCheckBox)_window.FindName("TglTrayStatus");
            _tglTrayLid = (WpfCheckBox)_window.FindName("TglTrayLid");
            _tglTrayPower = (WpfCheckBox)_window.FindName("TglTrayPower");
            _tglTrayRefresh = (WpfCheckBox)_window.FindName("TglTrayRefresh");

            _btnRefresh = (WpfButton)_window.FindName("BtnRefresh");
            _btnWinPower = (WpfButton)_window.FindName("BtnWinPower");
            _btnDone = (WpfButton)_window.FindName("BtnDone");

            if (_txtVersionTag != null) _txtVersionTag.Text = "v" + UpdateManager.CurrentVersion;

            _cmbLidBehavior.SelectionChanged += (s, e) =>
            {
                if (_updatingUi) return;
                if (_cmbLidBehavior.SelectedIndex >= 0)
                {
                    _ctx.SetLidMode((LidOperatingMode)_cmbLidBehavior.SelectedIndex);
                }
            };

            _tglAutoPower.Checked += (s, e) => OnAutoPowerChanged();
            _tglAutoPower.Unchecked += (s, e) => OnAutoPowerChanged();

            _tglNotifications.Checked += (s, e) => { if (!_updatingUi) _ctx.SetNotifications(true); };
            _tglNotifications.Unchecked += (s, e) => { if (!_updatingUi) _ctx.SetNotifications(false); };

            _tglStartup.Checked += (s, e) => { if (!_updatingUi) _ctx.SetStartupEnabled(true); };
            _tglStartup.Unchecked += (s, e) => { if (!_updatingUi) _ctx.SetStartupEnabled(false); };

            if (_tglStartMenuShortcut != null)
            {
                _tglStartMenuShortcut.Checked += (s, e) =>
                {
                    if (!_updatingUi) ShortcutManager.SetStartMenuShortcut(true, Application.ExecutablePath);
                };
                _tglStartMenuShortcut.Unchecked += (s, e) =>
                {
                    if (!_updatingUi) ShortcutManager.SetStartMenuShortcut(false, Application.ExecutablePath);
                };
            }

            if (_tglDesktopShortcut != null)
            {
                _tglDesktopShortcut.Checked += (s, e) =>
                {
                    if (!_updatingUi) ShortcutManager.SetDesktopShortcut(true, Application.ExecutablePath);
                };
                _tglDesktopShortcut.Unchecked += (s, e) =>
                {
                    if (!_updatingUi) ShortcutManager.SetDesktopShortcut(false, Application.ExecutablePath);
                };
            }

            if (_tglAutoUpdates != null)
            {
                _tglAutoUpdates.Checked += (s, e) => { if (!_updatingUi) _ctx.SetAutoCheckUpdates(true); };
                _tglAutoUpdates.Unchecked += (s, e) => { if (!_updatingUi) _ctx.SetAutoCheckUpdates(false); };
            }

            if (_btnCheckUpdates != null)
            {
                _btnCheckUpdates.Click += (s, e) => _ctx.TriggerUpdateCheck(true);
            }

            _tglTrayStatus.Checked += (s, e) => { if (!_updatingUi) _ctx.SetTrayFlag(TrayMenuFlags.ShowStatus, true); };
            _tglTrayStatus.Unchecked += (s, e) => { if (!_updatingUi) _ctx.SetTrayFlag(TrayMenuFlags.ShowStatus, false); };

            _tglTrayLid.Checked += (s, e) => { if (!_updatingUi) _ctx.SetTrayFlag(TrayMenuFlags.ShowLidToggle, true); };
            _tglTrayLid.Unchecked += (s, e) => { if (!_updatingUi) _ctx.SetTrayFlag(TrayMenuFlags.ShowLidToggle, false); };

            _tglTrayPower.Checked += (s, e) => { if (!_updatingUi) _ctx.SetTrayFlag(TrayMenuFlags.ShowPowerToggle, true); };
            _tglTrayPower.Unchecked += (s, e) => { if (!_updatingUi) _ctx.SetTrayFlag(TrayMenuFlags.ShowPowerToggle, false); };

            _tglTrayRefresh.Checked += (s, e) => { if (!_updatingUi) _ctx.SetTrayFlag(TrayMenuFlags.ShowRefresh, true); };
            _tglTrayRefresh.Unchecked += (s, e) => { if (!_updatingUi) _ctx.SetTrayFlag(TrayMenuFlags.ShowRefresh, false); };

            _btnRefresh.Click += (s, e) => _ctx.EvaluateAndApply(true);
            _btnWinPower.Click += (s, e) =>
            {
                try { Process.Start("control.exe", "powercfg.cpl,,1"); } catch { }
            };
            _btnDone.Click += (s, e) => _window.Hide();

            _window.Closing += (s, e) =>
            {
                if (!_allowClose)
                {
                    e.Cancel = true;
                    _window.Hide();
                }
            };

            _window.Closed += (s, e) =>
            {
                IsClosed = true;
                _ctx.StateUpdated -= OnContextStateUpdated;
                _ctx.ThemeChanged -= OnSystemThemeChanged;
            };

            RefreshFromContext();
        }

        private void SetStaticWindowIcons()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("LidGuard.ico"))
                {
                    if (s != null)
                    {
                        BitmapDecoder decoder = BitmapDecoder.Create(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        if (decoder.Frames.Count > 0)
                        {
                            _window.Icon = decoder.Frames[0];
                            return;
                        }
                    }
                }
            }
            catch { }

            try
            {
                Icon ic = _ctx.StaticAppIcon;
                if (ic != null)
                {
                    ImageSource imgSrc = Imaging.CreateBitmapSourceFromHIcon(
                        ic.Handle,
                        System.Windows.Int32Rect.Empty,
                        BitmapSizeOptions.FromEmptyOptions());
                    _window.Icon = imgSrc;
                }
            }
            catch { }
        }

        private string LoadEmbeddedXamlContent()
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LidGuardWindow.xaml"))
            using (StreamReader reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        }

        private void OnAutoPowerChanged()
        {
            if (_updatingUi) return;
            bool isChecked = (_tglAutoPower.IsChecked == true);
            _ctx.SetPowerPolicy(isChecked
                ? PowerPerformancePolicy.AutoHighPerfWhenDocked
                : PowerPerformancePolicy.DisabledDoNotManage);
        }

        private void OnSystemThemeChanged(object sender, EventArgs e)
        {
            if (_window == null || IsClosed) return;
            Action apply = () =>
            {
                bool isDark = ThemeManager.IsSystemDarkTheme();
                ThemeManager.ApplyMicaAndThemeToWindow(_window, isDark);
                RefreshFromContext();
            };

            if (!_window.Dispatcher.CheckAccess())
                _window.Dispatcher.BeginInvoke(apply);
            else
                apply();
        }

        private void OnContextStateUpdated(object sender, EventArgs e)
        {
            if (_window == null || IsClosed) return;
            if (!_window.Dispatcher.CheckAccess())
            {
                _window.Dispatcher.BeginInvoke(new Action(RefreshFromContext));
            }
            else
            {
                RefreshFromContext();
            }
        }

        public void RefreshFromContext()
        {
            if (_window == null || IsClosed) return;
            _updatingUi = true;
            try
            {
                bool isAwake = (_ctx.LastAppliedLidAction == 0);
                string lidState = isAwake ? "Lid Close: Do Nothing" : "Lid Close: Sleep";
                string shortPower = _ctx.LastPowerOverlayName.Contains("Best Performance") ? "Best Performance" : "Balanced";

                _txtStatusHeadline.Text = string.Format("{0}  •  {1}", lidState, shortPower);
                _txtStatusSub.Text = string.Format("{0}  •  {1}",
                    _ctx.LastDisplaySummary,
                    _ctx.IsPluggedIn ? "Plugged In" : "On Battery");

                if (isAwake)
                {
                    _statusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(16, 185, 129));
                }
                else
                {
                    _statusDot.Fill = new SolidColorBrush(ThemeManager.GetSystemAccentColor());
                }

                int modeIdx = (int)_ctx.CurrentLidMode;
                if (modeIdx >= 0 && modeIdx < _cmbLidBehavior.Items.Count)
                {
                    _cmbLidBehavior.SelectedIndex = modeIdx;
                }

                _tglAutoPower.IsChecked = (_ctx.CurrentPowerPolicy == PowerPerformancePolicy.AutoHighPerfWhenDocked);
                _tglNotifications.IsChecked = _ctx.ShowNotifications;
                _tglStartup.IsChecked = _ctx.IsStartupEnabled();

                if (_tglStartMenuShortcut != null)
                {
                    _tglStartMenuShortcut.IsChecked = ShortcutManager.HasStartMenuShortcut();
                }
                if (_tglDesktopShortcut != null)
                {
                    _tglDesktopShortcut.IsChecked = ShortcutManager.HasDesktopShortcut();
                }

                if (_tglAutoUpdates != null)
                {
                    _tglAutoUpdates.IsChecked = _ctx.AutoCheckUpdates;
                }
                if (_btnCheckUpdates != null)
                {
                    _btnCheckUpdates.Content = _ctx.UpdateStatusText ?? "Check Now";
                    _btnCheckUpdates.IsEnabled = !_ctx.IsCheckingUpdate;
                    bool hasUpdate = (_ctx.AvailableUpdate != null && _ctx.AvailableUpdate.IsNewer);
                    WpfStyle btnStyle = (WpfStyle)_window.FindResource(hasUpdate ? "Win11AccentButton" : "Win11Button");
                    if (btnStyle != null)
                    {
                        _btnCheckUpdates.Style = btnStyle;
                    }
                }

                _tglTrayStatus.IsChecked = (_ctx.TrayFlags & TrayMenuFlags.ShowStatus) != 0;
                _tglTrayLid.IsChecked = (_ctx.TrayFlags & TrayMenuFlags.ShowLidToggle) != 0;
                _tglTrayPower.IsChecked = (_ctx.TrayFlags & TrayMenuFlags.ShowPowerToggle) != 0;
                _tglTrayRefresh.IsChecked = (_ctx.TrayFlags & TrayMenuFlags.ShowRefresh) != 0;
            }
            finally
            {
                _updatingUi = false;
            }
        }

        public void ShowAndActivate()
        {
            bool isDark = ThemeManager.IsSystemDarkTheme();
            ThemeManager.ApplyMicaAndThemeToWindow(_window, isDark);
            RefreshFromContext();
            _window.Show();
            if (_window.WindowState == System.Windows.WindowState.Minimized)
            {
                _window.WindowState = System.Windows.WindowState.Normal;
            }
            _window.Activate();
            _window.Topmost = true;
            _window.Topmost = false;
            _window.Focus();
        }

        public void ForceClose()
        {
            if (_window != null && !IsClosed)
            {
                _allowClose = true;
                _window.Close();
            }
        }
    }

    // =========================================================================
    // Windows 11 Tray ContextMenu Styling (Supports Dark & Light System Themes)
    // =========================================================================
    public static class Win11TrayStyle
    {
        public static Font CreateFont(float sizePt, FontStyle style)
        {
            try
            {
                Font f = new Font("Segoe UI Variable Text", sizePt, style);
                if (f.Name.IndexOf("Segoe UI", StringComparison.OrdinalIgnoreCase) >= 0)
                    return f;
                f.Dispose();
            }
            catch { }
            return new Font("Segoe UI", sizePt, style);
        }

        public static GraphicsPath CreateRoundedRect(Rectangle bounds, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    public class Win11MenuRenderer : ToolStripProfessionalRenderer
    {
        private readonly bool _isDark;
        private readonly DrawingColor _accent;

        public Win11MenuRenderer(bool isDark, DrawingColor accent) : base(new Win11ColorTable(isDark))
        {
            _isDark = isDark;
            _accent = accent;
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle rect = new Rectangle(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            if (e.Item.Selected && e.Item.Enabled)
            {
                DrawingColor hoverCol = _isDark
                    ? DrawingColor.FromArgb(62, 62, 62)
                    : DrawingColor.FromArgb(234, 238, 244);
                using (GraphicsPath path = Win11TrayStyle.CreateRoundedRect(rect, 5))
                using (DrawingBrush b = new SolidBrush(hoverCol))
                {
                    g.FillPath(b, path);
                }
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (!e.Item.Enabled)
            {
                e.TextColor = _isDark
                    ? DrawingColor.FromArgb(165, 165, 165)
                    : DrawingColor.FromArgb(110, 110, 110);
            }
            else
            {
                e.TextColor = _isDark
                    ? DrawingColor.FromArgb(255, 255, 255)
                    : DrawingColor.FromArgb(26, 26, 26);
            }
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(e.ImageRectangle.X + 2, e.ImageRectangle.Y + 2, 14, 14);

            using (GraphicsPath path = Win11TrayStyle.CreateRoundedRect(r, 3))
            using (DrawingBrush b = new SolidBrush(_accent))
            {
                g.FillPath(b, path);
            }
            using (DrawingPen p = new DrawingPen(DrawingColor.White, 1.8f))
            {
                g.DrawLine(p, r.X + 3, r.Y + 7, r.X + 6, r.Y + 10);
                g.DrawLine(p, r.X + 6, r.Y + 10, r.X + 11, r.Y + 4);
            }
        }
    }

    public class Win11ColorTable : ProfessionalColorTable
    {
        private readonly bool _isDark;
        public Win11ColorTable(bool isDark) { _isDark = isDark; }

        private DrawingColor Bg { get { return _isDark ? DrawingColor.FromArgb(43, 43, 43) : DrawingColor.FromArgb(251, 251, 251); } }
        public override DrawingColor ToolStripDropDownBackground { get { return Bg; } }
        public override DrawingColor ImageMarginGradientBegin { get { return Bg; } }
        public override DrawingColor ImageMarginGradientMiddle { get { return Bg; } }
        public override DrawingColor ImageMarginGradientEnd { get { return Bg; } }
        public override DrawingColor MenuBorder { get { return _isDark ? DrawingColor.FromArgb(68, 68, 68) : DrawingColor.FromArgb(215, 215, 215); } }
        public override DrawingColor SeparatorDark { get { return _isDark ? DrawingColor.FromArgb(64, 64, 64) : DrawingColor.FromArgb(230, 230, 230); } }
        public override DrawingColor SeparatorLight { get { return DrawingColor.Transparent; } }
    }

    // =========================================================================
    // Power & Display Managers
    // =========================================================================
    public static class PowerManager
    {
        private static readonly Guid GUID_SUB_BUTTONS = new Guid("4f971e89-eebd-4455-a8de-9e59040e7347");
        private static readonly Guid GUID_LIDACTION = new Guid("5ca83367-6e45-459f-a27b-476b1d01c936");

        public static readonly Guid GUID_OVERLAY_BETTER_BATTERY = new Guid("961cc777-2547-4f9d-8174-7d86181b8a7a");
        public static readonly Guid GUID_OVERLAY_BALANCED = Guid.Empty;
        public static readonly Guid GUID_OVERLAY_BEST_PERFORMANCE = new Guid("ded574b5-45a0-4f42-8737-46345c09c238");

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetActiveScheme(IntPtr UserRootPowerKey, out IntPtr ActivePolicyGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerReadACValueIndex(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            out uint AcValueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerWriteACValueIndex(
            IntPtr RootPowerKey,
            ref Guid SchemeGuid,
            ref Guid SubGroupOfPowerSettingsGuid,
            ref Guid PowerSettingGuid,
            uint AcValueIndex);

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveScheme(IntPtr UserRootPowerKey, ref Guid SchemeGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerGetEffectiveOverlayScheme(out Guid EffectiveOverlayPolicyGuid);

        [DllImport("powrprof.dll")]
        private static extern uint PowerSetActiveOverlayScheme(Guid OverlaySchemeGuid);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public static Guid GetEffectiveOverlayScheme()
        {
            try
            {
                Guid guid;
                if (PowerGetEffectiveOverlayScheme(out guid) == 0)
                {
                    return guid;
                }
            }
            catch { }
            return GUID_OVERLAY_BALANCED;
        }

        public static bool SetOverlayScheme(Guid overlayGuid)
        {
            try
            {
                uint res = PowerSetActiveOverlayScheme(overlayGuid);
                return (res == 0);
            }
            catch
            {
                return false;
            }
        }

        public static string GetOverlayFriendlyName(Guid overlayGuid)
        {
            if (overlayGuid == GUID_OVERLAY_BEST_PERFORMANCE)
                return "Best Performance (High Power)";
            if (overlayGuid == GUID_OVERLAY_BETTER_BATTERY)
                return "Best Power Efficiency";
            return "Balanced";
        }

        public static int GetPluggedInLidAction()
        {
            IntPtr ptrGuid = IntPtr.Zero;
            try
            {
                if (PowerGetActiveScheme(IntPtr.Zero, out ptrGuid) == 0 && ptrGuid != IntPtr.Zero)
                {
                    Guid schemeGuid = (Guid)Marshal.PtrToStructure(ptrGuid, typeof(Guid));
                    Guid subGroup = GUID_SUB_BUTTONS;
                    Guid setting = GUID_LIDACTION;
                    uint val;
                    if (PowerReadACValueIndex(IntPtr.Zero, ref schemeGuid, ref subGroup, ref setting, out val) == 0)
                    {
                        return (int)val;
                    }
                }
            }
            catch { }
            finally
            {
                if (ptrGuid != IntPtr.Zero) LocalFree(ptrGuid);
            }
            return -1;
        }

        public static void SetPluggedInLidAction(int actionIndex)
        {
            IntPtr ptrGuid = IntPtr.Zero;
            bool updatedViaApi = false;
            try
            {
                if (PowerGetActiveScheme(IntPtr.Zero, out ptrGuid) == 0 && ptrGuid != IntPtr.Zero)
                {
                    Guid schemeGuid = (Guid)Marshal.PtrToStructure(ptrGuid, typeof(Guid));
                    Guid subGroup = GUID_SUB_BUTTONS;
                    Guid setting = GUID_LIDACTION;
                    uint resWrite = PowerWriteACValueIndex(IntPtr.Zero, ref schemeGuid, ref subGroup, ref setting, (uint)actionIndex);
                    uint resActive = PowerSetActiveScheme(IntPtr.Zero, ref schemeGuid);
                    updatedViaApi = (resWrite == 0 && resActive == 0);
                }
            }
            catch { }
            finally
            {
                if (ptrGuid != IntPtr.Zero) LocalFree(ptrGuid);
            }

            if (!updatedViaApi)
            {
                RunPowerCfg(string.Format("/setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION {0}", actionIndex));
                RunPowerCfg("/setactive SCHEME_CURRENT");
            }
        }

        private static void RunPowerCfg(string args)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "powercfg.exe",
                    Arguments = args,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process p = Process.Start(psi))
                {
                    if (p != null)
                    {
                        p.WaitForExit(3000);
                    }
                }
            }
            catch { }
        }
    }

    public static class DisplayDetector
    {
        private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
        private const int ERROR_SUCCESS = 0;

        private const uint TECH_INTERNAL = 0x80000000;
        private const uint TECH_LVDS = 6;
        private const uint TECH_DISPLAYPORT_EMBEDDED = 11;
        private const uint TECH_UDI_EMBEDDED = 13;

        [StructLayout(LayoutKind.Sequential)]
        private struct LUID
        {
            public uint LowPart;
            public int HighPart;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_SOURCE_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_RATIONAL
        {
            public uint Numerator;
            public uint Denominator;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public LUID adapterId;
            public uint id;
            public uint modeInfoIdx;
            public uint outputTechnology;
            public uint rotation;
            public uint scaling;
            public DISPLAYCONFIG_RATIONAL refreshRate;
            public uint scanLineOrdering;
            public int targetAvailable;
            public uint statusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_PATH_INFO
        {
            public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
            public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
            public uint flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DISPLAYCONFIG_MODE_INFO
        {
            public uint infoType;
            public uint id;
            public LUID adapterId;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 48)]
            public byte[] modeInfoData;
        }

        [DllImport("user32.dll")]
        private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

        [DllImport("user32.dll")]
        private static extern int QueryDisplayConfig(
            uint flags,
            ref uint numPathArrayElements,
            [Out] DISPLAYCONFIG_PATH_INFO[] pathInfoArray,
            ref uint numModeInfoArrayElements,
            [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
            IntPtr currentTopologyId);

        public static bool HasExternalDisplay(out int externalCount, out string summary)
        {
            externalCount = 0;
            int internalCount = 0;

            try
            {
                uint numPaths;
                uint numModes;
                int res = GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPaths, out numModes);
                if (res == ERROR_SUCCESS && numPaths > 0)
                {
                    DISPLAYCONFIG_PATH_INFO[] paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
                    DISPLAYCONFIG_MODE_INFO[] modes = new DISPLAYCONFIG_MODE_INFO[numModes];

                    res = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
                    if (res == ERROR_SUCCESS)
                    {
                        HashSet<string> seenTargets = new HashSet<string>();
                        for (int i = 0; i < numPaths; i++)
                        {
                            var target = paths[i].targetInfo;
                            if (target.targetAvailable == 0) continue;

                            string targetKey = string.Format("{0}:{1}:{2}", target.adapterId.LowPart, target.adapterId.HighPart, target.id);
                            if (seenTargets.Contains(targetKey)) continue;
                            seenTargets.Add(targetKey);

                            uint tech = target.outputTechnology;
                            bool isInternal = (tech == TECH_INTERNAL || tech == TECH_LVDS || tech == TECH_DISPLAYPORT_EMBEDDED || tech == TECH_UDI_EMBEDDED);

                            if (isInternal)
                                internalCount++;
                            else
                                externalCount++;
                        }

                        if (externalCount > 0)
                        {
                            summary = string.Format("{0} External Display(s)", externalCount);
                            return true;
                        }
                        else
                        {
                            summary = "Internal Screen Only";
                            return false;
                        }
                    }
                }
            }
            catch { }

            int totalScreens = Screen.AllScreens.Length;
            if (totalScreens > 1)
            {
                externalCount = totalScreens - 1;
                summary = string.Format("{0} External Display(s)", externalCount);
                return true;
            }

            summary = "Internal Screen Only";
            return false;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool startMinimized = false;

            if (args != null && args.Length > 0)
            {
                if (args[0] == "--status")
                {
                    int extCount;
                    string summary;
                    bool hasExt = DisplayDetector.HasExternalDisplay(out extCount, out summary);
                    int currentAc = PowerManager.GetPluggedInLidAction();
                    Guid overlay = PowerManager.GetEffectiveOverlayScheme();
                    string overlayName = PowerManager.GetOverlayFriendlyName(overlay);
                    bool acOnline = (SystemInformation.PowerStatus.PowerLineStatus == PowerLineStatus.Online);
                    bool isDark = ThemeManager.IsSystemDarkTheme();
                    WpfColor accent = ThemeManager.GetSystemAccentColor();
                    Console.WriteLine("Version={0}; Repo={1}; Is64BitProcess={2}; ExternalConnected={3}; ExternalCount={4}; Summary={5}; PluggedIn={6}; CurrentACLidAction={7}; PowerOverlay={8}; SystemDarkTheme={9}; SystemAccent=#{10:X2}{11:X2}{12:X2}",
                        UpdateManager.CurrentVersion, UpdateManager.GetConfiguredRepo(), Environment.Is64BitProcess, hasExt, extCount, summary, acOnline, currentAc, overlayName, isDark, accent.R, accent.G, accent.B);
                    return;
                }
                if (args[0] == "--set-repo" && args.Length > 1)
                {
                    UpdateManager.SetConfiguredRepo(args[1]);
                    Console.WriteLine("Updated GitHub repository to: " + UpdateManager.GetConfiguredRepo());
                    return;
                }
                if (args[0] == "--minimized")
                {
                    startMinimized = true;
                }
            }

            bool createdNew;
            using (var mutex = new Mutex(true, "Local\\LidGuard_SingleInstance_Mutex", out createdNew))
            {
                if (!createdNew)
                {
                    try
                    {
                        using (var evt = EventWaitHandle.OpenExisting("Local\\LidGuard_ShowGui_Event"))
                        {
                            evt.Set();
                        }
                    }
                    catch { }
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new LidGuardAppContext(!startMinimized));
            }
        }
    }
}
