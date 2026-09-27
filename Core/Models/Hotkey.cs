using System.Windows.Input;

namespace TimeTracker.Core.Models;

/// <summary>
/// Combinaison de touches globale, persistée en texte (« Ctrl+Alt+T ») dans les réglages
/// et convertie en codes Win32 au moment de l'enregistrement.
/// </summary>
public sealed record Hotkey(ModifierKeys Modifiers, Key Key)
{
    /// <summary>Un raccourci global sans modificateur confisquerait la touche à tout le système.</summary>
    public bool IsValid => Modifiers != ModifierKeys.None && Key != Key.None;

    /// <summary>Code de touche virtuelle attendu par <c>RegisterHotKey</c>.</summary>
    public uint VirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = new Hotkey(ModifierKeys.None, Key.None);
        if (string.IsNullOrWhiteSpace(text)) return false;

        var mods = ModifierKeys.None;
        var key = Key.None;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            var token = raw.Trim();
            switch (token.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; continue;
                case "alt": mods |= ModifierKeys.Alt; continue;
                case "maj" or "shift": mods |= ModifierKeys.Shift; continue;
                case "win" or "windows": mods |= ModifierKeys.Windows; continue;
            }

            // Les chiffres du clavier alphanumérique s'appellent D0…D9 côté WPF.
            if (token.Length == 1 && char.IsDigit(token[0])) token = "D" + token;
            if (!Enum.TryParse(token, ignoreCase: true, out key)) return false;
        }

        hotkey = new Hotkey(mods, key);
        return hotkey.IsValid;
    }

    public override string ToString()
    {
        if (!IsValid) return "(aucun)";

        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Maj");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");

        var name = Key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name[1..];
        parts.Add(name);

        return string.Join("+", parts);
    }
}
