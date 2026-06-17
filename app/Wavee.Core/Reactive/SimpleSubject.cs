namespace Wavee.Core;

/// <summary>
/// A minimal hand-rolled <see cref="IObservable{T}"/> so Wavee.Core takes ZERO package dependency
/// (WaveeMusic leans on System.Reactive; FluentGpu is signals-first / NativeAOT / zero-alloc, so Rx
/// is kept out). New subscribers immediately receive the last value if one has been published
/// (BehaviorSubject-like), which is what the UI bridge wants on (re)subscribe.
/// </summary>
public sealed class SimpleSubject<T> : IObservable<T>
{
    readonly object _gate = new();
    readonly List<IObserver<T>> _observers = new();
    T _last;
    bool _hasLast;

    public SimpleSubject() => _last = default!;
    public SimpleSubject(T initial) { _last = initial; _hasLast = true; }

    /// <summary>The most recently published value, or <c>default</c> if nothing has been published.</summary>
    public T? Current => _hasLast ? _last : default;

    public IDisposable Subscribe(IObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        lock (_gate) _observers.Add(observer);
        if (_hasLast) observer.OnNext(_last);
        return new Subscription(this, observer);
    }

    /// <summary>Publish a value to all current subscribers.</summary>
    public void OnNext(T value)
    {
        IObserver<T>[] snapshot;
        lock (_gate)
        {
            _last = value;
            _hasLast = true;
            snapshot = _observers.ToArray();
        }
        foreach (var o in snapshot) o.OnNext(value);
    }

    void Remove(IObserver<T> observer)
    {
        lock (_gate) _observers.Remove(observer);
    }

    sealed class Subscription(SimpleSubject<T> parent, IObserver<T> observer) : IDisposable
    {
        SimpleSubject<T>? _parent = parent;
        public void Dispose()
        {
            _parent?.Remove(observer);
            _parent = null;
        }
    }
}
