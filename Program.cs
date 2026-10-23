using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WinForms = System.Windows.Forms;

namespace KnobControl;

internal static class Program
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string AppName = "KnobControl";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--render")
            return RenderPreviews(args[1]);

        using var mutex = new Mutex(true, @"Local\KnobControl.SingleInstance", out bool first);
        if (!first) return 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        Settings settings;
        try { settings = Settings.Load(); }
        catch (Exception ex)
        {
            MessageBox.Show($"Couldn't read {Settings.FilePath}:\n\n{ex.Message}", AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }

        var menu = new MenuController(settings);
        using var input = new KnobInput(settings.DeviceMatch);
        input.Knob += menu.Handle;
        using var tray = CreateTray(app, menu, input);

        app.Run();
        menu.Close();
        return 0;
    }

    static WinForms.NotifyIcon CreateTray(Application app, MenuController menu, KnobInput input)
    {
        var enabled = new WinForms.ToolStripMenuItem("Enabled") { Checked = true, CheckOnClick = true };
        enabled.CheckedChanged += (_, _) => input.Enabled = enabled.Checked;

        var startup = new WinForms.ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);

        var edit = new WinForms.ToolStripMenuItem("Edit settings…");
        edit.Click += (_, _) => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Settings.FilePath}\""))?.Dispose();

        var reload = new WinForms.ToolStripMenuItem("Reload settings");
        reload.Click += (_, _) =>
        {
            try
            {
                var s = Settings.Load();
                input.DeviceMatch = s.DeviceMatch;
                menu.Load(s);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };

        var exit = new WinForms.ToolStripMenuItem("Exit");
        exit.Click += (_, _) => app.Shutdown();

        var strip = new WinForms.ContextMenuStrip();
        strip.Items.AddRange(new WinForms.ToolStripItem[]
        {
            enabled, startup, new WinForms.ToolStripSeparator(), edit, reload, new WinForms.ToolStripSeparator(), exit,
        });

        return new WinForms.NotifyIcon
        {
            Icon = DrawTrayIcon(),
            Text = "Knob Control",
            ContextMenuStrip = strip,
            Visible = true,
        };
    }

    static Icon DrawTrayIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var ring = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0x4C, 0x8D, 0xFF), 4);
            g.DrawEllipse(ring, 4, 4, 24, 24);
            using var dot = new SolidBrush(System.Drawing.Color.White);
            g.FillEllipse(dot, 13, 7, 6, 6);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    static bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(AppName) is string;
    }

    static void SetStartup(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(AppName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(AppName, throwOnMissingValue: false);
    }

    /// <summary>Renders the menu and a gauge to PNGs so the look can be checked without the knob.</summary>
    static int RenderPreviews(string folder)
    {
        Directory.CreateDirectory(folder);
        var settings = Settings.Load();
        var icons = settings.Items.Select(i => i.IsLevel ? null : ShellIcon.For(i)).ToList();
        var overlay = new OverlayWindow(settings.Items, icons, settings.Size);
        var scene = overlay.Scene;
        overlay.Content = null;
        var backdrop = new System.Windows.Controls.Border
        {
            Background = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2B, 0x30, 0x38)),
            Child = scene,
        };

        void Save(string name)
        {
            backdrop.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
            backdrop.Arrange(new Rect(backdrop.DesiredSize));
            backdrop.UpdateLayout();
            var rtb = new RenderTargetBitmap((int)backdrop.ActualWidth, (int)backdrop.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(backdrop);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(rtb));
            using var file = File.Create(System.IO.Path.Combine(folder, name));
            png.Save(file);
        }

        overlay.ShowMenu(0);
        Save("menu-brightness.png");
        overlay.ShowMenu(2);
        Save("menu-chrome.png");
        overlay.ShowMenu(4);
        overlay.ShowToast($"Opening {settings.Items[4].Name}…");
        Save("menu-toast.png");
        overlay.ShowLevel("Brightness", 70);
        Save("level.png");
        return 0;
    }
}
