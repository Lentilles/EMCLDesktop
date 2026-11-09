using CommunityToolkit.Mvvm.Input;
using EMCLDesktop.Services;

namespace EMCLDesktop.ViewModels;

public partial class MainViewModel
{
    private UserOperation? activeOperation;

    public bool IsOperationInProgress => activeOperation is not null;
    public string CancelOperationText => activeOperation?.IsCancellationRequested == true ? "Отмена…" : "Отменить";
    private CancellationToken OperationToken => activeOperation?.Token ?? shutdown.Token;

    private bool CanCancelOperation() => Volatile.Read(ref disposed) == 0 &&
        activeOperation is { IsCancellationRequested: false };

    [RelayCommand(CanExecute = nameof(CanCancelOperation))]
    private void CancelOperation()
    {
        if (!CanCancelOperation()) return;
        var operation = activeOperation!;
        Status = $"Отмена: {operation.Name}…";
        operation.Source.Cancel();
        NotifyOperationChanged();
        NotifyConnectionDisplayChanged();
        CancelConnectionCommand.NotifyCanExecuteChanged();
    }

    private UserOperation BeginOperation(string name)
    {
        if (activeOperation is not null) throw new InvalidOperationException("Дождитесь завершения текущей операции.");
        var operation = new UserOperation(this, name, shutdown.Token);
        activeOperation = operation;
        NotifyOperationChanged();
        return operation;
    }

    private void NotifyOperationChanged()
    {
        OnPropertyChanged(nameof(IsOperationInProgress));
        OnPropertyChanged(nameof(CancelOperationText));
        CancelOperationCommand.NotifyCanExecuteChanged();
    }

    private async Task<bool> CloseCancelledConnectionAsync(MeterConnection? meter)
    {
        if (meter is null || meter != connection || meter.IsActive) return false;
        IsConnected = false;
        try { await CloseConnectionAsync(); }
        catch (Exception error) { trace.Add($"Закрытие отменённого обмена: {error.Message}"); }
        return true;
    }

    private async Task ReportCancellationAsync(MeterConnection? meter, string message)
    {
        bool closed = await CloseCancelledConnectionAsync(meter);
        Status = closed ? $"{message} Соединение закрыто. Подключитесь повторно." : message;
    }

    public async Task SaveTraceAsync(Stream stream)
    {
        if (IsBusy || IsReading) return;
        using var operation = BeginOperation("сохранение трассировки");
        IsBusy = true;
        try
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(GetTraceSnapshot());
            await stream.WriteAsync(bytes, operation.Token);
            await stream.FlushAsync(operation.Token);
            Status = "Трассировка сохранена.";
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            Status = "Сохранение трассировки отменено. Файл может быть неполным.";
        }
        finally { IsBusy = false; }
    }

    private sealed class UserOperation : IDisposable
    {
        private readonly MainViewModel owner;
        public UserOperation(MainViewModel owner, string name, CancellationToken shutdown)
        {
            this.owner = owner;
            Name = name;
            Source = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
            Token = Source.Token;
        }
        public string Name { get; }
        public CancellationTokenSource Source { get; }
        public CancellationToken Token { get; }
        public bool IsCancellationRequested => Token.IsCancellationRequested;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            if (ReferenceEquals(owner.activeOperation, this))
            {
                owner.activeOperation = null;
                owner.NotifyOperationChanged();
            }
            Source.Dispose();
            Completion.TrySetResult();
        }
    }
}
