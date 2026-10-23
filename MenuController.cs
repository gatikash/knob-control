using System;
using System.Diagnostics;
using System.Linq;
using System.Windows.Threading;

namespace KnobControl;

/// <summary>
/// The menu's state machine. Hidden → any knob event shows the circle. In the circle, turning
/// moves the selection and pressing opens the item. In a level gauge, turning adjusts and
/// pressing goes back to the circle. Inactivity hides everything.
/// </summary>
internal sealed class MenuController
{
    readonly DispatcherTimer _hideTimer = new();
    readonly Brightness _brightness = new();
    Settings _settings = null!;
    OverlayWindow? _overlay;
    int _selected;
    bool _adjusting;
    int _level;

    public MenuController(Settings settings)
    {
        _hideTimer.Tick += (_, _) => Hide();
        Load(settings);
    }

    public void Load(Settings settings)
    {
        _overlay?.Close();
        _settings = settings;
        var icons = settings.Items.Select(i => i.IsLevel ? null : ShellIcon.For(i)).ToList();
        _overlay = new OverlayWindow(settings.Items, icons, settings.Size);
        _selected = 0;
        _adjusting = false;
    }

    public void Handle(KnobEvent e)
    {
        var overlay = _overlay!;
        var items = _settings.Items;

        if (!overlay.IsShown)
        {
            _adjusting = false;
            overlay.ShowMenu(_selected);
            overlay.Appear();
            Arm();
            return;
        }

        var item = items[_selected];
        if (_adjusting)
        {
            if (e == KnobEvent.Press)
            {
                _adjusting = false;
                overlay.ShowMenu(_selected);
            }
            else
            {
                _level = Math.Clamp(_level + (e == KnobEvent.Right ? _settings.Step : -_settings.Step), 0, 100);
                Apply(item, _level);
                overlay.ShowLevel(item.Name, _level);
            }
            Arm();
            return;
        }

        switch (e)
        {
            case KnobEvent.Left:
                _selected = (_selected + items.Count - 1) % items.Count;
                overlay.ShowMenu(_selected);
                Arm();
                break;
            case KnobEvent.Right:
                _selected = (_selected + 1) % items.Count;
                overlay.ShowMenu(_selected);
                Arm();
                break;
            case KnobEvent.Press:
                Open(item);
                break;
        }
    }

    void Open(MenuItemConfig item)
    {
        if (item.IsLevel)
        {
            int? current = Read(item);
            if (current is null)
            {
                _overlay!.ShowToast($"{item.Name} isn't available on this display");
                Arm();
                return;
            }
            _level = current.Value;
            _adjusting = true;
            _overlay!.ShowLevel(item.Name, _level);
            Arm();
            return;
        }

        try
        {
            AppLauncher.Launch(item);
            _overlay!.ShowToast($"Opening {item.Name}…");
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            _overlay!.ShowToast($"Couldn't open {item.Name}");
        }
        Arm(800);
    }

    int? Read(MenuItemConfig item)
    {
        try { return item.IsBrightness ? _brightness.Get() : Volume.Get(); }
        catch (Exception ex) { Debug.WriteLine(ex); return null; }
    }

    void Apply(MenuItemConfig item, int percent)
    {
        try
        {
            if (item.IsBrightness) _brightness.Set(percent);
            else Volume.Set(percent);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    void Arm(int? ms = null)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromMilliseconds(ms ?? (_adjusting ? _settings.HideAfterMs + 1000 : _settings.HideAfterMs));
        _hideTimer.Start();
    }

    void Hide()
    {
        _hideTimer.Stop();
        _adjusting = false;
        _overlay?.Vanish();
    }

    public void Close()
    {
        _hideTimer.Stop();
        _overlay?.Close();
    }
}
