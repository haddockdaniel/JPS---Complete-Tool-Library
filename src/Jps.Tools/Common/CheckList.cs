using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace Jps.Tools.Common
{
    /// <summary>
    /// Searchable multi-select list of <see cref="LookupItem"/>s with Select all / Clear and a "x of y selected" count.
    /// Used wherever a tool lets the user pick several clients, matters, vendors, timekeepers, banks...
    /// </summary>
    public class CheckList : Control
    {
        static CheckList()
        {
            DefaultStyleKeyProperty.OverrideMetadata(typeof(CheckList), new FrameworkPropertyMetadata(typeof(CheckList)));
        }

        public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
            nameof(ItemsSource), typeof(IEnumerable<LookupItem>), typeof(CheckList), new PropertyMetadata(null, (d, _) => ((CheckList)d).Rebind()));
        public static readonly DependencyProperty SearchProperty = DependencyProperty.Register(
            nameof(Search), typeof(string), typeof(CheckList), new PropertyMetadata("", (d, _) => ((CheckList)d).View?.Refresh()));
        public static readonly DependencyProperty ViewProperty = DependencyProperty.Register(nameof(View), typeof(ICollectionView), typeof(CheckList));
        public static readonly DependencyProperty SummaryProperty = DependencyProperty.Register(nameof(Summary), typeof(string), typeof(CheckList));
        public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(nameof(Placeholder), typeof(string), typeof(CheckList), new PropertyMetadata("Search"));

        public IEnumerable<LookupItem> ItemsSource { get => (IEnumerable<LookupItem>)GetValue(ItemsSourceProperty); set => SetValue(ItemsSourceProperty, value); }
        public string Search { get => (string)GetValue(SearchProperty); set => SetValue(SearchProperty, value); }
        public ICollectionView View { get => (ICollectionView)GetValue(ViewProperty); private set => SetValue(ViewProperty, value); }
        public string Summary { get => (string)GetValue(SummaryProperty); private set => SetValue(SummaryProperty, value); }
        public string Placeholder { get => (string)GetValue(PlaceholderProperty); set => SetValue(PlaceholderProperty, value); }

        public Command SelectAllCommand { get; }
        public Command ClearCommand { get; }

        /// <summary>Raised whenever an item is checked or unchecked.</summary>
        public event EventHandler SelectionChanged;

        public CheckList()
        {
            // Select all applies to what's visible, so "search, then select all" does what people expect.
            SelectAllCommand = new Command(() => SetVisible(true));
            ClearCommand = new Command(() => SetVisible(false));
        }

        public IReadOnlyList<LookupItem> Checked => (ItemsSource ?? Enumerable.Empty<LookupItem>()).Where(i => i.IsChecked).ToList();

        private void SetVisible(bool value)
        {
            if (View == null) return;
            foreach (LookupItem item in View) item.IsChecked = value;
        }

        private IEnumerable<LookupItem> _subscribed = Enumerable.Empty<LookupItem>();

        private void Rebind()
        {
            foreach (var item in _subscribed) item.PropertyChanged -= OnItemChanged;
            _subscribed = ItemsSource?.ToList() ?? new List<LookupItem>();
            foreach (var item in _subscribed) item.PropertyChanged += OnItemChanged;

            var view = new ListCollectionView((IList)_subscribed);
            view.Filter = o =>
            {
                var terms = (Search ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var item = (LookupItem)o;
                return terms.All(t => item.Display.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0);
            };
            View = view;
            UpdateSummary();
        }

        private void OnItemChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(LookupItem.IsChecked)) return;
            UpdateSummary();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        private void UpdateSummary()
        {
            var all = _subscribed.Count();
            Summary = _subscribed.Count(i => i.IsChecked).ToString("N0") + " of " + all.ToString("N0") + " selected";
        }
    }
}
