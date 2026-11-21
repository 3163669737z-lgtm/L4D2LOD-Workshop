using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace LodStudio.ViewModels
{
    /// <summary>轻量 MVVM 基类（不引第三方库）。</summary>
    public abstract class VmBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise([CallerMemberName] string name = null)
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(name));
        }

        /// <summary>批量刷新（null = 所有属性）</summary>
        public void RaiseAll()
        {
            var h = PropertyChanged;
            if (h != null) h(this, new PropertyChangedEventArgs(string.Empty));
        }
    }

    /// <summary>同步命令。</summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException("execute");
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter) { return _canExecute == null || _canExecute(); }

        public void Execute(object parameter) { _execute(); }

        public void RaiseCanExecuteChanged()
        {
            var h = CanExecuteChanged;
            if (h != null) h(this, EventArgs.Empty);
        }
    }

    /// <summary>带参数命令。</summary>
    public sealed class RelayCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Func<T, bool> _canExecute;

        public RelayCommand(Action<T> execute, Func<T, bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException("execute");
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged;

        public bool CanExecute(object parameter)
        {
            if (_canExecute == null) return true;
            return parameter is T ? _canExecute((T)parameter) : _canExecute(default(T));
        }

        public void Execute(object parameter)
        {
            _execute(parameter is T ? (T)parameter : default(T));
        }

        public void RaiseCanExecuteChanged()
        {
            var h = CanExecuteChanged;
            if (h != null) h(this, EventArgs.Empty);
        }
    }
}
