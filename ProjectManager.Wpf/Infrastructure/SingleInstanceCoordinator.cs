namespace ProjectManager.Wpf.Infrastructure;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = "Local\\XProj.SingleInstance";
    private const string ActivationEventName = "Local\\XProj.Activation";
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task? _listenerTask;
    private bool _ownsMutex;
    private bool _disposed;

    public SingleInstanceCoordinator()
    {
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _mutex = new Mutex(false, MutexName, out var createdNew);

        if (createdNew)
        {
            _mutex.WaitOne();
            _ownsMutex = true;
        }
        else
        {
            try
            {
                _ownsMutex = _mutex.WaitOne(0);
            }
            catch (AbandonedMutexException)
            {
                _ownsMutex = true;
            }
        }

        IsFirstInstance = _ownsMutex;
        if (IsFirstInstance)
        {
            _listenerTask = Task.Run(ListenForActivation);
        }
    }

    public bool IsFirstInstance { get; }

    public event EventHandler? ActivationRequested;

    public void ActivateExistingInstance()
    {
        if (IsFirstInstance || _disposed)
        {
            return;
        }

        try
        {
            _activationEvent.Set();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _listenerTask?.GetAwaiter().GetResult();
        _activationEvent.Dispose();
        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }
        }

        _mutex.Dispose();
        _shutdown.Dispose();
    }

    private void ListenForActivation()
    {
        var waitHandles = new[] { _activationEvent, _shutdown.Token.WaitHandle };
        while (WaitHandle.WaitAny(waitHandles) == 0)
        {
            if (_shutdown.IsCancellationRequested)
            {
                return;
            }

            ActivationRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}
