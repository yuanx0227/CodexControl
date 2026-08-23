using System.Security.Principal;

namespace CodexControl.Agent.Desktop;

internal sealed class SingleInstanceGate : IDisposable
{
    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private RegisteredWaitHandle? _registeredWait;
    private bool _ownsMutex;

    private SingleInstanceGate(Mutex mutex, EventWaitHandle activationEvent, bool ownsMutex)
    {
        _mutex = mutex;
        _activationEvent = activationEvent;
        _ownsMutex = ownsMutex;
    }

    public bool IsPrimary => _ownsMutex;

    public static SingleInstanceGate Acquire()
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var safeSid = string.Concat(sid.Select(character => char.IsLetterOrDigit(character) ? character : '_'));
        var mutex = new Mutex(initiallyOwned: true, $"Local\\CodexControlAgent_{safeSid}", out var created);
        var activation = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            $"Local\\CodexControlAgent_Activate_{safeSid}");
        return new SingleInstanceGate(mutex, activation, created);
    }

    public void SignalPrimary() => _activationEvent.Set();

    public void Listen(Action activate)
    {
        if (!IsPrimary)
        {
            throw new InvalidOperationException("Only the primary instance can listen for activation.");
        }

        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (state, _) => ((Action)state!).Invoke(),
            activate,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public void Dispose()
    {
        _registeredWait?.Unregister(null);
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

            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
