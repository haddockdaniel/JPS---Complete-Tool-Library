using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Jps.Tools.Common
{
    /// <summary>
    /// Makes a ComboBox's drop-down build only the rows on screen. WPF can only do that when the drop-down's ScrollViewer
    /// scrolls item by item (CanContentScroll = true); the WPF-UI template doesn't guarantee that, so a 30,000-client list
    /// froze the window for 10–40 seconds on open/close. Applied to every ComboBox by the style in ToolStyles.xaml.
    /// </summary>
    public static class ComboVirtualization
    {
        public static readonly DependencyProperty EnableProperty = DependencyProperty.RegisterAttached(
            "Enable", typeof(bool), typeof(ComboVirtualization), new PropertyMetadata(false, OnEnableChanged));

        public static bool GetEnable(DependencyObject d) => (bool)d.GetValue(EnableProperty);
        public static void SetEnable(DependencyObject d, bool value) => d.SetValue(EnableProperty, value);

        private static void OnEnableChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (!(d is ComboBox combo)) return;
            combo.DropDownOpened -= OnOpened;
            combo.RemoveHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, (TextChangedEventHandler)OnTyped);
            if (!(bool)e.NewValue) return;
            combo.DropDownOpened += OnOpened;
            combo.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, (TextChangedEventHandler)OnTyped);
        }

        private static readonly DependencyProperty BusyProperty = DependencyProperty.RegisterAttached("Busy", typeof(bool), typeof(ComboVirtualization));

        /// <summary>
        /// Type-to-find on long, editable lists. WPF's own TextSearch evaluates the display binding item by item, which froze
        /// the window 3–10 s per keystroke on a 45,000-client list; this compares the code/name strings directly (milliseconds).
        /// The first item whose code or display text starts with what was typed is selected, with the rest of its text
        /// highlighted so typing continues naturally.
        /// </summary>
        private static void OnTyped(object sender, TextChangedEventArgs e)
        {
            var combo = (ComboBox)sender;
            if (!combo.IsEditable || combo.IsTextSearchEnabled || (bool)combo.GetValue(BusyProperty)) return;
            if (!(combo.Template?.FindName("PART_EditableTextBox", combo) is TextBox box)) return;
            var typed = box.Text;
            if (box.SelectionLength > 0 && box.SelectionStart + box.SelectionLength == typed.Length)
                typed = typed.Substring(0, box.SelectionStart); // ignore the auto-completed tail we highlighted
            if (typed.Length == 0) return;

            object match = null;
            foreach (var item in combo.Items)
            {
                var code = (item as LookupItem)?.Code ?? item?.ToString() ?? "";
                var display = (item as LookupItem)?.Display ?? code;
                if (code.StartsWith(typed, System.StringComparison.OrdinalIgnoreCase) || display.StartsWith(typed, System.StringComparison.OrdinalIgnoreCase))
                {
                    match = item;
                    break;
                }
            }
            if (match == null || ReferenceEquals(match, combo.SelectedItem)) return;

            combo.SetValue(BusyProperty, true);
            try
            {
                combo.SelectedItem = match;
                var shown = box.Text;
                if (shown.StartsWith(typed, System.StringComparison.OrdinalIgnoreCase))
                    box.Select(typed.Length, shown.Length - typed.Length);
                else
                    box.Select(shown.Length, 0);
            }
            finally
            {
                combo.SetValue(BusyProperty, false);
            }
        }

        private static void OnOpened(object sender, System.EventArgs e)
        {
            var combo = (ComboBox)sender;
            if (!(combo.Template?.FindName("PART_Popup", combo) is Popup popup) || !(popup.Child is DependencyObject root)) return;
            var viewer = Find<ScrollViewer>(root);
            if (viewer == null || viewer.CanContentScroll) return;
            viewer.CanContentScroll = true;
            VirtualizingPanel.SetIsVirtualizing(viewer, true);
            VirtualizingPanel.SetVirtualizationMode(viewer, VirtualizationMode.Recycling);
        }

        private static T Find<T>(DependencyObject root) where T : DependencyObject
        {
            if (root is T hit) return hit;
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var found = Find<T>(VisualTreeHelper.GetChild(root, i));
                if (found != null) return found;
            }
            return null;
        }
    }
}
