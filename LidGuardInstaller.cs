using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace LidGuardInstaller
{
    public static class ThemeHelper
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [StructLayout(LayoutKind.Sequential)]
        private struct MARGINS
        {
            public int cxLeftWidth;
            public int cxRightWidth;
            public int cyTopHeight;
            public int cyBottomHeight;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

        private const int DWMWCP_ROUND = 2;
        private const int DWMSBT_MAINWINDOW = 2; // Mica
        private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;
        private const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF;

        public static bool IsSystemLightTheme()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("AppsUseLightTheme");
                        if (val != null)
                            return Convert.ToInt32(val) != 0;
                    }
                }
            }
            catch { }
            return true;
        }

        public static bool IsAccentColorOnTitleBarsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("ColorPrevalence");
                        if (val != null)
                            return Convert.ToInt32(val) != 0;
                    }
                }
            }
            catch { }
            return false;
        }

        public static Color GetSystemAccentColor()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("AccentColor");
                        if (val != null)
                        {
                            uint abgr = unchecked((uint)Convert.ToInt32(val));
                            byte r = (byte)(abgr & 0xFF);
                            byte g = (byte)((abgr >> 8) & 0xFF);
                            byte b = (byte)((abgr >> 16) & 0xFF);
                            return Color.FromRgb(r, g, b);
                        }
                    }
                }
            }
            catch { }

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    if (key != null)
                    {
                        object val = key.GetValue("AccentColorMenu");
                        if (val != null)
                        {
                            uint abgr = unchecked((uint)Convert.ToInt32(val));
                            byte r = (byte)(abgr & 0xFF);
                            byte g = (byte)((abgr >> 8) & 0xFF);
                            byte b = (byte)((abgr >> 16) & 0xFF);
                            return Color.FromRgb(r, g, b);
                        }
                    }
                }
            }
            catch { }

            return Color.FromRgb(0x00, 0x67, 0xC0);
        }

        public static void ApplyWindowFrameAndMica(Window window, bool isLight)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;

                HwndSource source = HwndSource.FromHwnd(hwnd);
                if (source != null && source.CompositionTarget != null)
                {
                    source.CompositionTarget.BackgroundColor = Colors.Transparent;
                }

                int dark = isLight ? 0 : 1;
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
                DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));

                int corner = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

                bool useAccentTitleBar = IsAccentColorOnTitleBarsEnabled();
                if (useAccentTitleBar)
                {
                    Color accent = GetSystemAccentColor();
                    int colorRef = accent.R | (accent.G << 8) | (accent.B << 16);
                    DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));

                    int whiteText = 0x00FFFFFF;
                    DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref whiteText, sizeof(int));

                    int defaultBorder = unchecked((int)DWMWA_COLOR_DEFAULT);
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref defaultBorder, sizeof(int));
                }
                else
                {
                    int noneColor = unchecked((int)DWMWA_COLOR_NONE);
                    DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref noneColor, sizeof(int));

                    int defaultColor = unchecked((int)DWMWA_COLOR_DEFAULT);
                    DwmSetWindowAttribute(hwnd, DWMWA_TEXT_COLOR, ref defaultColor, sizeof(int));
                    DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref defaultColor, sizeof(int));
                }

                MARGINS margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);

                int backdrop = DWMSBT_MAINWINDOW;
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

                if (hr == 0)
                {
                    window.Background = Brushes.Transparent;
                }
                else
                {
                    window.Background = new SolidColorBrush(isLight
                        ? Color.FromRgb(0xF3, 0xF3, 0xF3)
                        : Color.FromRgb(0x20, 0x20, 0x20));
                }
            }
            catch { }
        }
    }

    public static class SetupEngine
    {
        public const string GitHubOwner = "SStarlight9614";
        public static readonly string[] RepoCandidates = new string[] { "Lid-Guard", "LidGuard" };
        public static readonly string[] BranchCandidates = new string[] { "main", "master" };

        public static string GetArchitectureInstallDirectory()
        {
            // 64-bit apps install to "C:\Program Files\LidGuard"
            // 32-bit apps on 64-bit Windows install to "C:\Program Files (x86)\LidGuard"
            if (Environment.Is64BitProcess)
            {
                string pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Path.Combine(pf64, "LidGuard");
            }
            else
            {
                string pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (string.IsNullOrEmpty(pfx86))
                    pfx86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                return Path.Combine(pfx86, "LidGuard");
            }
        }

        public static bool IsAdministrator()
        {
            try
            {
                using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
                {
                    WindowsPrincipal principal = new WindowsPrincipal(identity);
                    return principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
            }
            catch { return false; }
        }

        public static string AcquireOrBuildLidGuardExe(Action<string> onProgress)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            string stagingDir = Path.Combine(Path.GetTempPath(), "LidGuardSetup_" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(stagingDir);

            string stagedExe = Path.Combine(stagingDir, "LidGuard.exe");

            // 1. Check GitHub Releases first for a pre-packaged LidGuard.exe binary
            onProgress("Checking GitHub repository (" + GitHubOwner + "/Lid-Guard)...");
            foreach (string repo in RepoCandidates)
            {
                try
                {
                    string releaseApi = "https://api.github.com/repos/" + GitHubOwner + "/" + repo + "/releases/latest";
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(releaseApi);
                    req.UserAgent = "LidGuard-Installer/1.0";
                    req.Accept = "application/vnd.github.v3+json";
                    req.Timeout = 7000;

                    using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
                    using (StreamReader reader = new StreamReader(resp.GetResponseStream()))
                    {
                        string json = reader.ReadToEnd();
                        MatchCollection matches = Regex.Matches(
                            json,
                            "\"browser_download_url\"\\s*:\\s*\"([^\"]+LidGuard\\.exe)\"",
                            RegexOptions.IgnoreCase);
                        if (matches.Count > 0)
                        {
                            string downloadUrl = matches[0].Groups[1].Value;
                            onProgress("Downloading LidGuard.exe from GitHub Release...");
                            DownloadFile(downloadUrl, stagedExe);
                            if (File.Exists(stagedExe) && new FileInfo(stagedExe).Length > 1024)
                                return stagedExe;
                        }
                    }
                }
                catch { }
            }

            // 2. Download source files from GitHub repository raw branch and compile 64-bit LidGuard.exe
            string csPath = Path.Combine(stagingDir, "LidGuard.cs");
            string xamlPath = Path.Combine(stagingDir, "LidGuardWindow.xaml");
            string icoPath = Path.Combine(stagingDir, "LidGuard.ico");

            bool downloadedFromGitHub = false;
            foreach (string repo in RepoCandidates)
            {
                if (downloadedFromGitHub) break;
                foreach (string branch in BranchCandidates)
                {
                    try
                    {
                        string baseUrl = "https://raw.githubusercontent.com/" + GitHubOwner + "/" + repo + "/" + branch + "/";
                        onProgress("Downloading LidGuard files from GitHub (" + repo + ")...");
                        DownloadFile(baseUrl + "LidGuard.cs", csPath);
                        DownloadFile(baseUrl + "LidGuardWindow.xaml", xamlPath);
                        DownloadFile(baseUrl + "LidGuard.ico", icoPath);

                        if (File.Exists(csPath) && File.Exists(xamlPath) && File.Exists(icoPath))
                        {
                            downloadedFromGitHub = true;
                            break;
                        }
                    }
                    catch { }
                }
            }

            // 3. Fallback to local files if running side-by-side before first push or offline
            if (!downloadedFromGitHub)
            {
                string localDir = AppDomain.CurrentDomain.BaseDirectory;
                string localExe = Path.Combine(localDir, "LidGuard.exe");
                string localCs = Path.Combine(localDir, "LidGuard.cs");
                string localXaml = Path.Combine(localDir, "LidGuardWindow.xaml");
                string localIco = Path.Combine(localDir, "LidGuard.ico");

                if (File.Exists(localCs) && File.Exists(localXaml) && File.Exists(localIco))
                {
                    onProgress("Staging LidGuard source files...");
                    File.Copy(localCs, csPath, true);
                    File.Copy(localXaml, xamlPath, true);
                    File.Copy(localIco, icoPath, true);
                    downloadedFromGitHub = true;
                }
                else if (File.Exists(localExe))
                {
                    onProgress("Staging LidGuard.exe...");
                    File.Copy(localExe, stagedExe, true);
                    return stagedExe;
                }
                else
                {
                    throw new Exception("Could not download LidGuard files from https://github.com/" + GitHubOwner + "/Lid-Guard. Please verify the repository is online.");
                }
            }

            onProgress("Building 64-bit LidGuard.exe...");
            CompileLidGuard(stagingDir, csPath, xamlPath, icoPath, stagedExe);
            return stagedExe;
        }

        private static void DownloadFile(string url, string destinationPath)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "LidGuard-Installer/1.0";
            req.Timeout = 12000;
            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (Stream netStream = resp.GetResponseStream())
            using (FileStream fs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write))
            {
                netStream.CopyTo(fs);
            }
        }

        private static void CompileLidGuard(string workDir, string csPath, string xamlPath, string icoPath, string outExePath)
        {
            string fwDir = Environment.Is64BitProcess
                ? @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
                : @"C:\Windows\Microsoft.NET\Framework\v4.0.30319";
            string cscPath = Path.Combine(fwDir, "csc.exe");
            string wpfDir = Path.Combine(fwDir, "WPF");
            string platformFlag = Environment.Is64BitProcess ? "x64" : "x86";

            string args = string.Format(
                "/nologo /target:winexe /platform:{0} /optimize+ \"/out:{1}\" \"/win32icon:{2}\" " +
                "\"/resource:{3},LidGuardWindow.xaml\" \"/resource:{2},LidGuard.ico\" " +
                "/r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Xaml.dll " +
                "\"/r:{4}\" \"/r:{5}\" \"/r:{6}\" \"{7}\"",
                platformFlag,
                outExePath,
                icoPath,
                xamlPath,
                Path.Combine(wpfDir, "PresentationFramework.dll"),
                Path.Combine(wpfDir, "PresentationCore.dll"),
                Path.Combine(wpfDir, "WindowsBase.dll"),
                csPath);

            ProcessStartInfo psi = new ProcessStartInfo(cscPath, args)
            {
                WorkingDirectory = workDir,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (Process p = Process.Start(psi))
            {
                string output = p.StandardOutput.ReadToEnd() + "\n" + p.StandardError.ReadToEnd();
                p.WaitForExit(20000);
                if (p.ExitCode != 0 || !File.Exists(outExePath))
                {
                    throw new Exception("Failed to compile LidGuard.exe:\n" + output.Trim());
                }
            }
        }

        public static void PerformFileInstallToProgramFiles(string sourceExePath, string targetDir)
        {
            // Stop any running LidGuard instance before replacing the binary
            int myPid = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName("LidGuard"))
            {
                if (p.Id != myPid)
                {
                    try { p.Kill(); p.WaitForExit(3000); } catch { }
                }
            }

            if (!Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            string targetExe = Path.Combine(targetDir, "LidGuard.exe");
            File.Copy(sourceExePath, targetExe, true);
        }

        public static void ConfigureUserIntegration(string installedExePath, string installDir, bool createStartMenu, bool createDesktop)
        {
            // Create or remove Start Menu shortcut
            string startMenuPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "LidGuard.lnk");
            if (createStartMenu)
                CreateShortcut(startMenuPath, installedExePath, installDir);
            else if (File.Exists(startMenuPath))
                try { File.Delete(startMenuPath); } catch { }

            // Create or remove Desktop shortcut
            string desktopPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "LidGuard.lnk");
            if (createDesktop)
                CreateShortcut(desktopPath, installedExePath, installDir);
            else if (File.Exists(desktopPath))
                try { File.Delete(desktopPath); } catch { }

            // Register Windows Startup entry
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key != null)
                        key.SetValue("LidGuard", "\"" + installedExePath + "\"");
                }
            }
            catch { }

            // Register in Installed Apps (HKCU Uninstall)
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\LidGuard"))
                {
                    if (key != null)
                    {
                        key.SetValue("DisplayName", "LidGuard");
                        key.SetValue("DisplayVersion", "0.9");
                        key.SetValue("Publisher", "SStarlight9614");
                        key.SetValue("URLInfoAbout", "https://github.com/SStarlight9614/Lid-Guard");
                        key.SetValue("DisplayIcon", installedExePath);
                        key.SetValue("InstallLocation", installDir);
                        key.SetValue("UninstallString", "\"" + installedExePath + "\" --uninstall");
                        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    }
                }
            }
            catch { }
        }

        private static void CreateShortcut(string shortcutPath, string targetExe, string workingDir)
        {
            try
            {
                string dir = Path.GetDirectoryName(shortcutPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) return;
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { shortcutPath });
                Type shortcutType = shortcut.GetType();
                shortcutType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, shortcut, new object[] { targetExe });
                shortcutType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, shortcut, new object[] { workingDir });
                shortcutType.InvokeMember("Description", BindingFlags.SetProperty, null, shortcut, new object[] { "LidGuard - Dock-Aware Lid & Power Automation" });
                shortcutType.InvokeMember("IconLocation", BindingFlags.SetProperty, null, shortcut, new object[] { targetExe + ",0" });
                shortcutType.InvokeMember("Save", BindingFlags.InvokeMethod, null, shortcut, null);
            }
            catch { }
        }
    }

    public class InstallerApp
    {
        private Window window;
        private CheckBox chkStartMenu;
        private CheckBox chkDesktop;
        private Button btnContinue;
        private Button btnCancel;
        private TextBlock lblStatus;

        private const string InstallerXaml = @"
<Window xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
        xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
        Title=""LidGuard Setup""
        Width=""460""
        SizeToContent=""Height""
        WindowStartupLocation=""CenterScreen""
        ResizeMode=""NoResize""
        Background=""Transparent""
        FontFamily=""Segoe UI Variable Display, Segoe UI""
        TextOptions.TextFormattingMode=""Display""
        TextOptions.TextRenderingMode=""ClearType""
        RenderOptions.ClearTypeHint=""Enabled""
        UseLayoutRounding=""True""
        SnapsToDevicePixels=""True"">

    <Window.Resources>
        <SolidColorBrush x:Key=""WindowBgBrush"" Color=""Transparent""/>
        <SolidColorBrush x:Key=""CardBgBrush"" Color=""#FBFBFB""/>
        <SolidColorBrush x:Key=""CardBorderBrush"" Color=""#E5E5E5""/>
        <SolidColorBrush x:Key=""DividerBrush"" Color=""#EAEAEA""/>
        <SolidColorBrush x:Key=""TextPrimaryBrush"" Color=""#1A1A1A""/>
        <SolidColorBrush x:Key=""TextSecondaryBrush"" Color=""#5C5C5C""/>
        <SolidColorBrush x:Key=""AccentBrush"" Color=""#0067C0""/>
        <SolidColorBrush x:Key=""AccentHoverBrush"" Color=""#005BA8""/>
        <SolidColorBrush x:Key=""AccentTextBrush"" Color=""#FFFFFF""/>
        <SolidColorBrush x:Key=""SubtleBtnBgBrush"" Color=""#F5F5F5""/>
        <SolidColorBrush x:Key=""SubtleBtnHoverBrush"" Color=""#EAEAEA""/>
        <SolidColorBrush x:Key=""SubtleBtnBorderBrush"" Color=""#D6D6D6""/>
        <SolidColorBrush x:Key=""ToggleOffBgBrush"" Color=""#F0F0F0""/>
        <SolidColorBrush x:Key=""ToggleOffBorderBrush"" Color=""#8A8A8A""/>
        <SolidColorBrush x:Key=""ToggleOffThumbBrush"" Color=""#5C5C5C""/>
        <SolidColorBrush x:Key=""ToggleOnThumbBrush"" Color=""#FFFFFF""/>

        <Style x:Key=""ToggleSwitch"" TargetType=""CheckBox"">
            <Setter Property=""Cursor"" Value=""Hand""/>
            <Setter Property=""VerticalAlignment"" Value=""Center""/>
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""CheckBox"">
                        <Grid Width=""40"" Height=""20"" Background=""Transparent"">
                            <Border x:Name=""Track"" CornerRadius=""10"" Background=""{DynamicResource ToggleOffBgBrush}"" BorderBrush=""{DynamicResource ToggleOffBorderBrush}"" BorderThickness=""1""/>
                            <Ellipse x:Name=""Thumb"" Width=""12"" Height=""12"" Fill=""{DynamicResource ToggleOffThumbBrush}"" HorizontalAlignment=""Left"" VerticalAlignment=""Center"" Margin=""4,0,0,0""/>
                        </Grid>
                        <ControlTemplate.Triggers>
                            <Trigger Property=""IsChecked"" Value=""True"">
                                <Setter TargetName=""Track"" Property=""Background"" Value=""{DynamicResource AccentBrush}""/>
                                <Setter TargetName=""Track"" Property=""BorderBrush"" Value=""{DynamicResource AccentBrush}""/>
                                <Setter TargetName=""Thumb"" Property=""Fill"" Value=""{DynamicResource ToggleOnThumbBrush}""/>
                                <Setter TargetName=""Thumb"" Property=""HorizontalAlignment"" Value=""Right""/>
                                <Setter TargetName=""Thumb"" Property=""Margin"" Value=""0,0,4,0""/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key=""CardBorder"" TargetType=""Border"">
            <Setter Property=""Background"" Value=""{DynamicResource CardBgBrush}""/>
            <Setter Property=""BorderBrush"" Value=""{DynamicResource CardBorderBrush}""/>
            <Setter Property=""BorderThickness"" Value=""1""/>
            <Setter Property=""CornerRadius"" Value=""8""/>
            <Setter Property=""RenderOptions.ClearTypeHint"" Value=""Enabled""/>
        </Style>

        <Style x:Key=""PrimaryButton"" TargetType=""Button"">
            <Setter Property=""Background"" Value=""{DynamicResource AccentBrush}""/>
            <Setter Property=""Foreground"" Value=""{DynamicResource AccentTextBrush}""/>
            <Setter Property=""FontSize"" Value=""13.5""/>
            <Setter Property=""FontWeight"" Value=""SemiBold""/>
            <Setter Property=""Padding"" Value=""22,8""/>
            <Setter Property=""Cursor"" Value=""Hand""/>
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""Button"">
                        <Border x:Name=""BtnBorder"" Background=""{TemplateBinding Background}"" CornerRadius=""6"" Padding=""{TemplateBinding Padding}"">
                            <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property=""IsMouseOver"" Value=""True"">
                                <Setter TargetName=""BtnBorder"" Property=""Background"" Value=""{DynamicResource AccentHoverBrush}""/>
                            </Trigger>
                            <Trigger Property=""IsEnabled"" Value=""False"">
                                <Setter TargetName=""BtnBorder"" Property=""Opacity"" Value=""0.6""/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key=""SecondaryButton"" TargetType=""Button"">
            <Setter Property=""Background"" Value=""{DynamicResource SubtleBtnBgBrush}""/>
            <Setter Property=""Foreground"" Value=""{DynamicResource TextPrimaryBrush}""/>
            <Setter Property=""BorderBrush"" Value=""{DynamicResource SubtleBtnBorderBrush}""/>
            <Setter Property=""FontSize"" Value=""13""/>
            <Setter Property=""Padding"" Value=""16,8""/>
            <Setter Property=""Cursor"" Value=""Hand""/>
            <Setter Property=""Template"">
                <Setter.Value>
                    <ControlTemplate TargetType=""Button"">
                        <Border x:Name=""BtnBorder"" Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" BorderThickness=""1"" CornerRadius=""6"" Padding=""{TemplateBinding Padding}"">
                            <ContentPresenter HorizontalAlignment=""Center"" VerticalAlignment=""Center""/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property=""IsMouseOver"" Value=""True"">
                                <Setter TargetName=""BtnBorder"" Property=""Background"" Value=""{DynamicResource SubtleBtnHoverBrush}""/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </Window.Resources>

    <StackPanel Margin=""24,18,24,22"">
        <!-- Header -->
        <Grid Margin=""2,0,2,16"">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width=""Auto""/>
                <ColumnDefinition Width=""*""/>
            </Grid.ColumnDefinitions>
            <Image x:Name=""HeaderIcon"" Grid.Column=""0"" Width=""28"" Height=""28"" VerticalAlignment=""Center"" Margin=""0,0,12,0"" RenderOptions.BitmapScalingMode=""HighQuality""/>
            <StackPanel Grid.Column=""1"" VerticalAlignment=""Center"">
                <TextBlock Text=""Install LidGuard"" FontSize=""20"" FontWeight=""Bold"" Foreground=""{DynamicResource TextPrimaryBrush}""/>
                <TextBlock Text=""Dock-Aware Lid &amp; Power Mode Automation for Windows 11"" FontSize=""12"" Foreground=""{DynamicResource TextSecondaryBrush}"" Margin=""0,2,0,0""/>
            </StackPanel>
        </Grid>

        <!-- Install Options Card -->
        <Border Style=""{StaticResource CardBorder}"" Padding=""16,6"" Margin=""0,0,0,18"">
            <StackPanel>
                <!-- Destination Folder Row -->
                <Grid MinHeight=""46"">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width=""Auto""/>
                        <ColumnDefinition Width=""*""/>
                    </Grid.ColumnDefinitions>
                    <TextBlock Grid.Column=""0"" Text=""Install Location"" FontSize=""13.5"" FontWeight=""SemiBold"" Foreground=""{DynamicResource TextPrimaryBrush}"" VerticalAlignment=""Center""/>
                    <TextBlock x:Name=""TxtInstallPath"" Grid.Column=""1"" Text=""C:\Program Files\LidGuard"" FontSize=""12.5"" Foreground=""{DynamicResource TextSecondaryBrush}"" HorizontalAlignment=""Right"" VerticalAlignment=""Center"" TextTrimming=""CharacterEllipsis"" Margin=""12,0,0,0""/>
                </Grid>

                <Rectangle Height=""1"" Fill=""{DynamicResource DividerBrush}""/>

                <!-- Add to Start Menu Toggle (Checked by default) -->
                <Grid MinHeight=""46"">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width=""*""/>
                        <ColumnDefinition Width=""Auto""/>
                    </Grid.ColumnDefinitions>
                    <StackPanel Grid.Column=""0"" VerticalAlignment=""Center"">
                        <TextBlock Text=""Add to Start Menu"" FontSize=""13.5"" Foreground=""{DynamicResource TextPrimaryBrush}""/>
                        <TextBlock Text=""Pin LidGuard to your Windows Start Menu programs list"" FontSize=""11.5"" Foreground=""{DynamicResource TextSecondaryBrush}""/>
                    </StackPanel>
                    <CheckBox x:Name=""ChkStartMenu"" Grid.Column=""1"" Style=""{StaticResource ToggleSwitch}"" IsChecked=""True""/>
                </Grid>

                <Rectangle Height=""1"" Fill=""{DynamicResource DividerBrush}""/>

                <!-- Create Desktop Shortcut Toggle -->
                <Grid MinHeight=""46"">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width=""*""/>
                        <ColumnDefinition Width=""Auto""/>
                    </Grid.ColumnDefinitions>
                    <StackPanel Grid.Column=""0"" VerticalAlignment=""Center"">
                        <TextBlock Text=""Create Desktop Shortcut"" FontSize=""13.5"" Foreground=""{DynamicResource TextPrimaryBrush}""/>
                        <TextBlock Text=""Place a LidGuard shortcut icon on your Desktop"" FontSize=""11.5"" Foreground=""{DynamicResource TextSecondaryBrush}""/>
                    </StackPanel>
                    <CheckBox x:Name=""ChkDesktop"" Grid.Column=""1"" Style=""{StaticResource ToggleSwitch}"" IsChecked=""False""/>
                </Grid>
            </StackPanel>
        </Border>

        <!-- Footer Status & Continue Button -->
        <Grid>
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width=""*""/>
                <ColumnDefinition Width=""Auto""/>
                <ColumnDefinition Width=""Auto""/>
            </Grid.ColumnDefinitions>
            <TextBlock x:Name=""LblStatus"" Grid.Column=""0"" Text=""Source: github.com/SStarlight9614/Lid-Guard"" FontSize=""12"" Foreground=""{DynamicResource TextSecondaryBrush}"" VerticalAlignment=""Center"" TextTrimming=""CharacterEllipsis"" Margin=""2,0,10,0""/>
            <Button x:Name=""BtnCancel"" Grid.Column=""1"" Content=""Cancel"" Style=""{StaticResource SecondaryButton}"" Margin=""0,0,8,0""/>
            <Button x:Name=""BtnContinue"" Grid.Column=""2"" Content=""Continue"" Style=""{StaticResource PrimaryButton}""/>
        </Grid>
    </StackPanel>
</Window>";

        public void Run()
        {
            using (Stream s = new MemoryStream(Encoding.UTF8.GetBytes(InstallerXaml)))
            {
                window = (Window)XamlReader.Load(s);
            }

            Image headerIcon = (Image)window.FindName("HeaderIcon");
            TextBlock txtInstallPath = (TextBlock)window.FindName("TxtInstallPath");
            chkStartMenu = (CheckBox)window.FindName("ChkStartMenu");
            chkDesktop = (CheckBox)window.FindName("ChkDesktop");
            lblStatus = (TextBlock)window.FindName("LblStatus");
            btnCancel = (Button)window.FindName("BtnCancel");
            btnContinue = (Button)window.FindName("BtnContinue");

            string targetDir = SetupEngine.GetArchitectureInstallDirectory();
            txtInstallPath.Text = targetDir;
            txtInstallPath.ToolTip = targetDir;

            try
            {
                using (Stream iconStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("LidGuard.ico"))
                {
                    if (iconStream != null)
                    {
                        BitmapFrame frame = BitmapFrame.Create(iconStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                        window.Icon = frame;
                        if (headerIcon != null) headerIcon.Source = frame;
                    }
                }
            }
            catch { }

            ApplyTheme();

            window.SourceInitialized += (s, e) =>
            {
                ThemeHelper.ApplyWindowFrameAndMica(window, ThemeHelper.IsSystemLightTheme());
            };

            btnCancel.Click += (s, e) => window.Close();
            btnContinue.Click += async (s, e) => await StartInstallationAsync(targetDir);

            Application app = new Application();
            app.Run(window);
        }

        private void ApplyTheme()
        {
            bool isLight = ThemeHelper.IsSystemLightTheme();
            Color accent = ThemeHelper.GetSystemAccentColor();
            Color accentHover = Color.FromRgb(
                (byte)Math.Max(0, accent.R - 18),
                (byte)Math.Max(0, accent.G - 18),
                (byte)Math.Max(0, accent.B - 18));

            ResourceDictionary res = window.Resources;
            res["AccentBrush"] = new SolidColorBrush(accent);
            res["AccentHoverBrush"] = new SolidColorBrush(accentHover);
            res["AccentTextBrush"] = new SolidColorBrush(Colors.White);
            res["ToggleOnThumbBrush"] = new SolidColorBrush(Colors.White);

            if (isLight)
            {
                res["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0xFB, 0xFB, 0xFB));
                res["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xE5, 0xE5, 0xE5));
                res["DividerBrush"] = new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA));
                res["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
                res["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(0x5C, 0x5C, 0x5C));
                res["SubtleBtnBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
                res["SubtleBtnHoverBrush"] = new SolidColorBrush(Color.FromRgb(0xEA, 0xEA, 0xEA));
                res["SubtleBtnBorderBrush"] = new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xD6));
                res["ToggleOffBgBrush"] = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
                res["ToggleOffBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
                res["ToggleOffThumbBrush"] = new SolidColorBrush(Color.FromRgb(0x5C, 0x5C, 0x5C));
            }
            else
            {
                res["CardBgBrush"] = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
                res["CardBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D));
                res["DividerBrush"] = new SolidColorBrush(Color.FromRgb(0x38, 0x38, 0x38));
                res["TextPrimaryBrush"] = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
                res["TextSecondaryBrush"] = new SolidColorBrush(Color.FromRgb(0xC5, 0xC5, 0xC5));
                res["SubtleBtnBgBrush"] = new SolidColorBrush(Color.FromRgb(0x35, 0x35, 0x35));
                res["SubtleBtnHoverBrush"] = new SolidColorBrush(Color.FromRgb(0x40, 0x40, 0x40));
                res["SubtleBtnBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A));
                res["ToggleOffBgBrush"] = new SolidColorBrush(Color.FromRgb(0x24, 0x24, 0x24));
                res["ToggleOffBorderBrush"] = new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
                res["ToggleOffThumbBrush"] = new SolidColorBrush(Color.FromRgb(0xCE, 0xCE, 0xCE));
            }
        }

        private async Task StartInstallationAsync(string targetDir)
        {
            bool createStartMenu = chkStartMenu.IsChecked == true;
            bool createDesktop = chkDesktop.IsChecked == true;

            btnContinue.IsEnabled = false;
            btnCancel.IsEnabled = false;
            chkStartMenu.IsEnabled = false;
            chkDesktop.IsEnabled = false;
            btnContinue.Content = "Installing...";

            try
            {
                string stagedExe = await Task.Run(() =>
                    SetupEngine.AcquireOrBuildLidGuardExe(msg =>
                        window.Dispatcher.BeginInvoke(new Action(() => lblStatus.Text = msg))));

                lblStatus.Text = "Installing to " + targetDir + "...";

                await Task.Run(() =>
                {
                    if (SetupEngine.IsAdministrator())
                    {
                        SetupEngine.PerformFileInstallToProgramFiles(stagedExe, targetDir);
                    }
                    else
                    {
                        // Elevate helper copy into C:\Program Files\LidGuard
                        string selfExe = Process.GetCurrentProcess().MainModule.FileName;
                        string args = string.Format("--elevated-copy \"{0}\" \"{1}\"", stagedExe, targetDir);
                        ProcessStartInfo psi = new ProcessStartInfo(selfExe, args)
                        {
                            Verb = "runas",
                            UseShellExecute = true,
                            WindowStyle = ProcessWindowStyle.Hidden
                        };
                        using (Process p = Process.Start(psi))
                        {
                            p.WaitForExit();
                            if (p.ExitCode != 0)
                                throw new Exception("Installation to Program Files failed (exit code " + p.ExitCode + ").");
                        }
                    }

                    string installedExe = Path.Combine(targetDir, "LidGuard.exe");
                    if (!File.Exists(installedExe))
                        throw new Exception("LidGuard.exe was not found in " + targetDir + " after installation.");

                    // Configure user shortcuts & startup registration as the current user
                    SetupEngine.ConfigureUserIntegration(installedExe, targetDir, createStartMenu, createDesktop);
                });

                lblStatus.Text = "Launching LidGuard...";

                string finalExe = Path.Combine(targetDir, "LidGuard.exe");
                Process.Start(new ProcessStartInfo(finalExe)
                {
                    WorkingDirectory = targetDir,
                    UseShellExecute = true
                });

                window.Close();
            }
            catch (Exception ex)
            {
                btnContinue.IsEnabled = true;
                btnCancel.IsEnabled = true;
                chkStartMenu.IsEnabled = true;
                chkDesktop.IsEnabled = true;
                btnContinue.Content = "Continue";
                lblStatus.Text = "Installation stopped.";
                MessageBox.Show(
                    window,
                    ex.Message,
                    "LidGuard Installer",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            if (args != null && args.Length >= 1 && string.Equals(args[0], "--test-fetch", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string exe = SetupEngine.AcquireOrBuildLidGuardExe(msg => Console.WriteLine(msg));
                    Console.WriteLine("SUCCESS: StagedExe=" + exe + "; TargetDir=" + SetupEngine.GetArchitectureInstallDirectory());
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("ERROR: " + ex.Message);
                    return 1;
                }
            }

            if (args != null && args.Length >= 3 && string.Equals(args[0], "--elevated-copy", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    SetupEngine.PerformFileInstallToProgramFiles(args[1], args[2]);
                    return 0;
                }
                catch
                {
                    return 1;
                }
            }

            InstallerApp app = new InstallerApp();
            app.Run();
            return 0;
        }
    }
}
