using System.Collections.Concurrent;

namespace Muse2Api.Computer;

/// <summary>Long-lived MTA apartment; a hung provider cannot build an unbounded queue.</summary>
internal sealed class ComputerWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(1);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _operationTimeout, _queueTimeout;
    private int _disposed;

    public ComputerWorker(TimeSpan? operationTimeout = null, TimeSpan? queueTimeout = null)
    {
        _operationTimeout = operationTimeout ?? TimeSpan.FromSeconds(12);
        _queueTimeout = queueTimeout ?? TimeSpan.FromSeconds(2);
        var thread = new Thread(() =>
        {
            foreach (var action in _queue.GetConsumingEnumerable()) action();
        }) { IsBackground = true, Name = "Muse UI Automation MTA" };
        thread.SetApartmentState(ApartmentState.MTA); thread.Start();
    }

    public Task<T> RunAsync<T>(Func<T> action, Action stop, CancellationToken cancel, bool skipWhenBusy = false)
        => RunAsync(_ => action(), stop, cancel, skipWhenBusy);

    public async Task<T> RunAsync<T>(Func<CancellationToken, T> action, Action stop, CancellationToken cancel, bool skipWhenBusy = false, Action? timedOut = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!await _gate.WaitAsync(skipWhenBusy ? TimeSpan.Zero : _queueTimeout, cancel))
        {
            if (skipWhenBusy) return default!;
            stop(); throw new InvalidOperationException("Thao tác giao diện trước chưa thoát. Không gửi thao tác mới; chờ ứng dụng đích phản hồi hoặc khởi động lại Muse.");
        }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var operationToken = operation.Token;
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            _queue.Add(() =>
            {
                try { operationToken.ThrowIfCancellationRequested(); completion.TrySetResult(action(operationToken)); }
                catch (OperationCanceledException) { completion.TrySetCanceled(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { _gate.Release(); }
            });
        }
        catch { _gate.Release(); throw; }
        _ = completion.Task.ContinueWith(t => { _ = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        try { return await completion.Task.WaitAsync(_operationTimeout, cancel); }
        catch (TimeoutException)
        {
            operation.Cancel(); (timedOut ?? stop)();
            throw new InvalidOperationException(timedOut is null ? "Ứng dụng đích hoặc UI Automation không phản hồi. Đã thu hồi quyền điều khiển; không tự gửi lại thao tác."
                : "ui_read_timeout: Đọc UI Automation quá lâu; đã hủy lần đọc và làm mới worker một lần. Thử đọc vùng nhỏ (max_depth=3, limit=30). Không coi đây là test đạt.");
        }
        catch (OperationCanceledException) { operation.Cancel(); stop(); throw; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) _queue.CompleteAdding();
        // Never block the host UI waiting for an unresponsive COM provider.
    }
}
