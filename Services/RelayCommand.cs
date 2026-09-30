using System.Windows.Input;

namespace OICQStickerManager.Services;

public class RelayCommand<T> : ICommand
{
    private readonly Func<T?, Task> _execute;
    public RelayCommand(Func<T?, Task> execute) => _execute = execute;
    public bool CanExecute(object? parameter) => true;

    // CanExecute 恒为 true，永远不会触发重查询；显式空实现避免 CS0067 警告
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public async void Execute(object? parameter) => await _execute((T?)parameter);
}

// 保留原来的非泛型版本方便 LoadStickersCommand 使用
public class RelayCommand : RelayCommand<object>
{
    public RelayCommand(Func<Task> execute) : base(_ => execute()) { }
}