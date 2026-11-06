using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Jps.Shell
{
    /// <summary>
    /// Dark-first LexisNexis look: WPF-UI's dark or light theme with the accent forced to LexisNexis red, so primary
    /// buttons, focus rings, toggles and selections all match the brand instead of the PC's Windows accent colour.
    /// </summary>
    public static class Theme
    {
        public static readonly Color BrandRed = Color.FromRgb(0xE3, 0x05, 0x1B);

        public static void Apply(ApplicationTheme theme)
        {
            ApplicationThemeManager.Apply(theme, WindowBackdropType.None, false);
            ApplicationAccentColorManager.Apply(BrandRed, theme);
        }

        public static void Toggle() =>
            Apply(ApplicationThemeManager.GetAppTheme() == ApplicationTheme.Dark ? ApplicationTheme.Light : ApplicationTheme.Dark);
    }
}
