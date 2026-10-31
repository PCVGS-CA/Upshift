using System.Globalization;

namespace Upshift.Core.Install;

/// <summary>
/// The key that opens OptiScaler's in-game menu, as a line for the game page ("OptiScaler menu: Insert").
/// OptiScaler 0.9.4 opens it on one key, <c>[Menu] ShortcutKey</c> (default 0x2D, Insert; -1 none; its menu_common.cpp).
/// Its README adds: if the menu closes the moment it opens, "try Alt + Insert", a reported workaround for alternate keyboard
/// layouts (issue #484). Upshift reads that as the input-method layouts (Chinese, Japanese, Korean), where Alt+Insert is
/// given first; elsewhere it's the fallback in the same line.
/// </summary>
public static class OptiScalerMenuKey
{
    public const int Insert = 0x2D;

    /// <summary>The key from the ini value: its default (Insert) for auto or missing, null for -1 (no key).</summary>
    public static int? Key(string? iniValue)
    {
        var value = iniValue?.Trim();
        if (string.IsNullOrEmpty(value) || value.Equals("auto", StringComparison.OrdinalIgnoreCase)) return Insert;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return hex is > 0 and < 256 ? hex : null;
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var dec) && dec is > 0 and < 256 ? dec : null;
    }

    /// <summary>True for keyboard layouts whose language uses an input method: Chinese (0x04), Japanese (0x11), Korean (0x12).</summary>
    public static bool InputMethodLayout(int languageId) => (languageId & 0x3FF) is 0x04 or 0x11 or 0x12;

    /// <summary>"OptiScaler menu: Insert (if it closes at once, try Alt+Insert)", or null when the menu has no key.</summary>
    public static string? Line(string? iniValue, int languageId, Func<int, string> keyName)
    {
        if (Key(iniValue) is not { } vk) return null;
        if (vk != Insert) return $"OptiScaler menu: {keyName(vk)}";
        return InputMethodLayout(languageId)
            ? "OptiScaler menu: Alt+Insert (or Insert)"
            : "OptiScaler menu: Insert (if it closes at once, try Alt+Insert)";
    }
}
