// Material Design 3 color tokens (baseline scheme, seed #6750A4) + custom Success / Warning roles.
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

static class Theme
{
    static readonly Dictionary<string, string> Light = new Dictionary<string, string>
    {
        { "Primary", "#6750A4" }, { "OnPrimary", "#FFFFFF" }, { "PrimaryContainer", "#EADDFF" }, { "OnPrimaryContainer", "#21005D" },
        { "Secondary", "#625B71" }, { "OnSecondary", "#FFFFFF" }, { "SecondaryContainer", "#E8DEF8" }, { "OnSecondaryContainer", "#1D192B" },
        { "Tertiary", "#7D5260" }, { "TertiaryContainer", "#FFD8E4" }, { "OnTertiaryContainer", "#31111D" },
        { "Error", "#B3261E" }, { "OnError", "#FFFFFF" }, { "ErrorContainer", "#F9DEDC" }, { "OnErrorContainer", "#410E0B" },
        { "Surface", "#FEF7FF" }, { "OnSurface", "#1D1B20" }, { "OnSurfaceVariant", "#49454F" },
        { "SurfaceContainerLowest", "#FFFFFF" }, { "SurfaceContainerLow", "#F7F2FA" }, { "SurfaceContainer", "#F3EDF7" },
        { "SurfaceContainerHigh", "#ECE6F0" }, { "SurfaceContainerHighest", "#E6E0E9" },
        { "Outline", "#79747E" }, { "OutlineVariant", "#CAC4D0" }, { "Scrim", "#000000" },
        { "InverseSurface", "#322F35" }, { "InverseOnSurface", "#F5EFF7" }, { "InversePrimary", "#D0BCFF" },
        { "Success", "#386A20" }, { "SuccessContainer", "#B8F397" }, { "OnSuccessContainer", "#042100" },
        { "Warning", "#7C5800" }, { "WarningContainer", "#FFDEA6" }, { "OnWarningContainer", "#271900" },
    };

    static readonly Dictionary<string, string> Dark = new Dictionary<string, string>
    {
        { "Primary", "#D0BCFF" }, { "OnPrimary", "#381E72" }, { "PrimaryContainer", "#4F378B" }, { "OnPrimaryContainer", "#EADDFF" },
        { "Secondary", "#CCC2DC" }, { "OnSecondary", "#332D41" }, { "SecondaryContainer", "#4A4458" }, { "OnSecondaryContainer", "#E8DEF8" },
        { "Tertiary", "#EFB8C8" }, { "TertiaryContainer", "#633B48" }, { "OnTertiaryContainer", "#FFD8E4" },
        { "Error", "#F2B8B5" }, { "OnError", "#601410" }, { "ErrorContainer", "#8C1D18" }, { "OnErrorContainer", "#F9DEDC" },
        { "Surface", "#141218" }, { "OnSurface", "#E6E0E9" }, { "OnSurfaceVariant", "#CAC4D0" },
        { "SurfaceContainerLowest", "#0F0D13" }, { "SurfaceContainerLow", "#1D1B20" }, { "SurfaceContainer", "#211F26" },
        { "SurfaceContainerHigh", "#2B2930" }, { "SurfaceContainerHighest", "#36343B" },
        { "Outline", "#938F99" }, { "OutlineVariant", "#49454F" }, { "Scrim", "#000000" },
        { "InverseSurface", "#E6E0E9" }, { "InverseOnSurface", "#322F35" }, { "InversePrimary", "#6750A4" },
        { "Success", "#9DD67D" }, { "SuccessContainer", "#1F5108" }, { "OnSuccessContainer", "#B8F397" },
        { "Warning", "#F5BD48" }, { "WarningContainer", "#5E4200" }, { "OnWarningContainer", "#FFDEA6" },
    };

    const string Key = @"Software\UltimateOptimizer";

    public static bool IsDark { get; private set; }

    /// "system" | "light" | "dark"
    public static string Mode
    {
        get
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                {
                    var v = k == null ? null : k.GetValue("Theme") as string;
                    if (v == "light" || v == "dark") return v;
                }
            }
            catch { }
            return "system";
        }
        set
        {
            try { using (var k = Registry.CurrentUser.CreateSubKey(Key)) k.SetValue("Theme", value); } catch { }
        }
    }

    public static bool SystemIsDark()
    {
        try
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                return v is int && (int)v == 0;
            }
        }
        catch { return false; }
    }

    public static void ApplyMode()
    {
        string m = Mode;
        Apply(m == "dark" || (m == "system" && SystemIsDark()));
    }

    /// target = null -> application resources (live UI); a detached tree needs its own dictionary.
    public static void Apply(bool dark, ResourceDictionary target = null)
    {
        IsDark = dark;
        var res = target ?? Application.Current.Resources;
        foreach (var kv in dark ? Dark : Light)
        {
            var c = (Color)ColorConverter.ConvertFromString(kv.Value);
            var b = new SolidColorBrush(c);
            b.Freeze();
            res[kv.Key] = b;
            res[kv.Key + "Color"] = c;
        }
    }

    public static Color Get(string token)
    {
        return (Color)ColorConverter.ConvertFromString((IsDark ? Dark : Light)[token]);
    }
}
