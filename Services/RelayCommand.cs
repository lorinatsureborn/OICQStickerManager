using System.Windows.Input;

namespace OICQStickerManager.Services;

public class RelayCommand<T> : ICommand
{
    private readonly Func<T?, Task> _execute;
    private readonly bool _allowConcurrent;
    private readonly Action<Exception> _onError;
    private int _busy;
    public RelayCommand(Func<T?, Task> execute, bool allowConcurrent = false, Action<Exception>? onError = null)
    {
        _execute = execute;
        _allowConcurrent = allowConcurrent;
        _onError = onError ?? (ex => QqPanelWatcher.Log("command failed: " + ex.GetType().Name));
    }
    public bool CanExecute(object? parameter) => _allowConcurrent || Volatile.Read(ref _busy) == 0;

    public event EventHandler? CanExecuteChanged;

    public async void Execute(object? parameter)
    {
        if (!_allowConcurrent && Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return;
        try
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            await _execute((T?)parameter);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _onError(ex); }
        finally
        {
            if (!_allowConcurrent) Interlocked.Exchange(ref _busy, 0);
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}

// 保留原来的非泛型版本方便 LoadStickersCommand 使用
public class RelayCommand : RelayCommand<object>
{
    public RelayCommand(Func<Task> execute, bool allowConcurrent = false, Action<Exception>? onError = null)
        : base(_ => execute(), allowConcurrent, onError) { }
}
