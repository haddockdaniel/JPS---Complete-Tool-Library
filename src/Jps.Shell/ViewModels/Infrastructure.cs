using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Wpf.Ui.Controls;

namespace Jps.Shell.ViewModels
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }

        protected void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object> _execute;

        public RelayCommand(Action execute) : this(_ => execute()) { }
        public RelayCommand(Action<object> execute) => _execute = execute;

        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => _execute(parameter);
    }

    public static class Icons
    {
        /// <summary>Catalog icons are WPF-UI symbol names; anything unknown falls back to a generic icon.</summary>
        public static SymbolRegular Parse(string name) =>
            Enum.TryParse(name, out SymbolRegular symbol) ? symbol : SymbolRegular.Apps24;
    }
}
