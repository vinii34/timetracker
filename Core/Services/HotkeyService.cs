using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using TimeTracker.Core.Models;

namespace TimeTracker.Core.Services;

/// <summary>
/// Raccourcis clavier globaux via l'API Win32 <c>RegisterHotKey</c>.
/// Utilise une fenêtre « message-only » invisible pour recevoir WM_HOTKEY.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    [Flags]
    public enum Modifiers : uint
    {
        Alt = 0x0001,
        Control = 0x0002,
        Shift = 0x0004,
        Win = 0x0008,
        NoRepeat = 0x4000
    }

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _handlers = new();
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyService()
    {
        // HWND_MESSAGE = -3 → fenêtre invisible dédiée aux messages.
        var parameters = new HwndSourceParameters("TimeTrackerHotkeyWindow")
        {
            ParentWindow = new IntPtr(-3)
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    /// <summary>
    /// Enregistre un raccourci. Renvoie false si la combinaison est déjà prise par une autre appli.
    /// </summary>
    public bool Register(Modifiers modifiers, uint virtualKey, Action handler)
    {
        int id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, (uint)(modifiers | Modifiers.NoRepeat), virtualKey))
            return false;
        _handlers[id] = handler;
        return true;
    }

    /// <summary>Enregistre un raccourci décrit par un <see cref="Hotkey"/> (réglages utilisateur).</summary>
    public bool Register(Hotkey hotkey, Action handler)
    {
        if (!hotkey.IsValid) return false;
        return Register(ToWin32(hotkey.Modifiers), hotkey.VirtualKey, handler);
    }

    /// <summary>Libère tous les raccourcis, pour les réenregistrer après changement de réglages.</summary>
    public void UnregisterAll()
    {
        foreach (var id in _handlers.Keys)
            UnregisterHotKey(_source.Handle, id);
        _handlers.Clear();
    }

    private static Modifiers ToWin32(ModifierKeys mods)
    {
        Modifiers result = 0;
        if (mods.HasFlag(ModifierKeys.Control)) result |= Modifiers.Control;
        if (mods.HasFlag(ModifierKeys.Alt)) result |= Modifiers.Alt;
        if (mods.HasFlag(ModifierKeys.Shift)) result |= Modifiers.Shift;
        if (mods.HasFlag(ModifierKeys.Windows)) result |= Modifiers.Win;
        return result;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _handlers.TryGetValue(wParam.ToInt32(), out var action))
        {
            action();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
