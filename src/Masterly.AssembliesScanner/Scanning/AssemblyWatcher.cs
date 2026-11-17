namespace Masterly.AssembliesScanner;

/// <summary>Describes a change in the set of assemblies a watcher is following.</summary>
public sealed class AssembliesChangedEventArgs : EventArgs
{
    internal AssembliesChangedEventArgs(ScanResult previous, ScanResult current, ScanDiff diff)
    {
        Previous = previous;
        Current = current;
        Diff = diff;
    }

    /// <summary>The scan before the change.</summary>
    public ScanResult Previous { get; }

    /// <summary>The scan after the change.</summary>
    public ScanResult Current { get; }

    /// <summary>What was added, removed and changed.</summary>
    public ScanDiff Diff { get; }
}

/// <summary>
/// Watches the directories of a scanner and rescans when assembly files appear, change or disappear.
/// Create one with <see cref="AssemblyScanner.Watch"/>.
/// </summary>
/// <remarks>
/// File events are debounced, so a build that writes many files produces one notification. Events are
/// raised on a thread-pool thread. To replace a file that is in use, load it with
/// <see cref="IsolationOptions.LoadInMemory"/>, which does not lock it.
/// </remarks>
public sealed class AssemblyWatcher : IDisposable
{
    private readonly AssemblyScanner _scanner;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _timer;
    private readonly TimeSpan _debounce;
    private readonly object _stateGate = new();
    private readonly object _scanGate = new();
    private ScanResult _current;
    private bool _disposed;

    internal AssemblyWatcher(AssemblyScanner scanner, IReadOnlyList<(string Path, bool Recursive)> directories, TimeSpan debounce)
    {
        _scanner = scanner;
        _debounce = debounce;
        _current = scanner.Scan();
        _timer = new Timer(_ => Rescan(), null, Timeout.Infinite, Timeout.Infinite);

        try
        {
            foreach ((string path, bool recursive) in directories)
            {
                var watcher = new FileSystemWatcher(path, "*.dll")
                {
                    IncludeSubdirectories = recursive,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime
                };
                watcher.Created += OnFileEvent;
                watcher.Changed += OnFileEvent;
                watcher.Deleted += OnFileEvent;
                watcher.Renamed += OnFileEvent;
                watcher.Error += (_, e) => Error?.Invoke(this, e);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Raised after the set of matching assemblies changed.</summary>
    public event EventHandler<AssembliesChangedEventArgs>? Changed;

    /// <summary>Raised when watching or rescanning fails. The watcher keeps running.</summary>
    public event EventHandler<ErrorEventArgs>? Error;

    /// <summary>The most recent scan.</summary>
    public ScanResult Current
    {
        get
        {
            lock (_stateGate)
                return _current;
        }
    }

    /// <summary>Rescans now instead of waiting for a file event, and raises <see cref="Changed"/> if anything differs.</summary>
    public void Refresh() => Rescan();

    /// <summary>Stops watching.</summary>
    public void Dispose()
    {
        lock (_stateGate)
        {
            if (_disposed)
                return;

            _disposed = true;
        }

        foreach (FileSystemWatcher watcher in _watchers)
            watcher.Dispose();

        _timer.Dispose();
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        lock (_stateGate)
        {
            if (!_disposed)
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Rescan()
    {
        AssembliesChangedEventArgs? change = null;

        // One rescan at a time, so notifications arrive in order.
        lock (_scanGate)
        {
            lock (_stateGate)
            {
                if (_disposed)
                    return;
            }

            try
            {
                ScanResult current = _scanner.Scan();
                ScanResult previous = Current;
                ScanDiff diff = current.DiffFrom(previous);
                if (diff.HasChanges)
                {
                    lock (_stateGate)
                        _current = current;

                    change = new AssembliesChangedEventArgs(previous, current, diff);
                }
            }
            catch (Exception ex)
            {
                Error?.Invoke(this, new ErrorEventArgs(ex));
                return;
            }

            if (change is not null)
                Changed?.Invoke(this, change);
        }
    }
}
