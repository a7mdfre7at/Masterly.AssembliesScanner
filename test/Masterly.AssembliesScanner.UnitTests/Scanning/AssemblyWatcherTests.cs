namespace Masterly.AssembliesScanner.UnitTests;

public class AssemblyWatcherTests : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private static Task<AssembliesChangedEventArgs> NextChange(AssemblyWatcher watcher, Func<AssembliesChangedEventArgs, bool> wanted)
    {
        var completion = new TaskCompletionSource<AssembliesChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        watcher.Changed += (_, change) =>
        {
            if (wanted(change))
                completion.TrySetResult(change);
        };
        return completion.Task;
    }

    [Fact]
    public async Task Watch_RaisesChanged_WhenAnAssemblyAppearsAndDisappears()
    {
        Fixture.Compile(_temp.Path, "Watched.Existing", "public class A { }");
        using AssemblyWatcher watcher = Assemblies.Scan().FromDirectory(_temp.Path).Include("Watched.*").Watch(TimeSpan.FromMilliseconds(100));
        watcher.Current.Select(a => a.Name).ShouldBe(["Watched.Existing"]);

        Task<AssembliesChangedEventArgs> added = NextChange(watcher, c => c.Diff.Added.Count > 0);
        string path = Fixture.Compile(_temp.Path, "Watched.New", "public class B { }");
        Fixture.Compile(_temp.Path, "Ignored.ByPattern", "public class C { }");
        AssembliesChangedEventArgs addition = await added.WaitAsync(Timeout);

        addition.Diff.Added.Select(a => a.Name).ShouldBe(["Watched.New"]);
        addition.Previous.Count().ShouldBe(1);
        addition.Current.Count().ShouldBe(2);
        watcher.Current.Count().ShouldBe(2);

        Task<AssembliesChangedEventArgs> removed = NextChange(watcher, c => c.Diff.Removed.Count > 0);
        File.Delete(path);
        AssembliesChangedEventArgs removal = await removed.WaitAsync(Timeout);

        removal.Diff.Removed.Select(a => a.Name).ShouldBe(["Watched.New"]);
        watcher.Current.Select(a => a.Name).ShouldBe(["Watched.Existing"]);
    }

    [Fact]
    public async Task Watch_ReportsAReplacedBuildAsChanged()
    {
        Fixture.Compile(_temp.Path, "Rebuilt", """[assembly: System.Reflection.AssemblyVersion("1.0.0.0")] public class A { }""");
        using AssemblyWatcher watcher = Assemblies.Scan().FromDirectory(_temp.Path).Watch(TimeSpan.FromMilliseconds(100));

        Task<AssembliesChangedEventArgs> changed = NextChange(watcher, c => c.Diff.Changed.Count > 0);
        Fixture.Compile(_temp.Path, "Rebuilt", """[assembly: System.Reflection.AssemblyVersion("2.0.0.0")] public class A { } public class B { }""");
        AssembliesChangedEventArgs change = await changed.WaitAsync(Timeout);

        change.Diff.Changed.ShouldHaveSingleItem().Version.ShouldBe(new Version(2, 0, 0, 0));
    }

    [Fact]
    public void Refresh_RescansImmediately()
    {
        using AssemblyWatcher watcher = Assemblies.Scan().FromDirectory(_temp.Path).Watch(TimeSpan.FromHours(1));
        AssembliesChangedEventArgs? seen = null;
        watcher.Changed += (_, change) => seen = change;

        Fixture.Compile(_temp.Path, "Refreshed", "public class A { }");
        watcher.Refresh();

        seen.ShouldNotBeNull();
        seen.Diff.Added.Select(a => a.Name).ShouldBe(["Refreshed"]);
    }

    [Fact]
    public void Watch_NeedsADirectory()
    {
        Action act = () => Assemblies.Scan().FromLoadedAssemblies().Watch();

        Should.Throw<InvalidOperationException>(act);
    }

    [Fact]
    public void Watch_WithNoSource_WatchesTheBaseDirectory()
    {
        using AssemblyWatcher watcher = Assemblies.Scan("Masterly.AssembliesScanner").Watch();

        watcher.Current.ShouldHaveSingleItem().Name.ShouldBe("Masterly.AssembliesScanner");
    }

    [Fact]
    public void Watch_RejectsADirectoryThatDoesNotExist()
    {
        Action act = () => Assemblies.Scan().FromDirectory(Path.Join(_temp.Path, "missing")).Watch();

        Should.Throw<ArgumentException>(act);
    }

    [Fact]
    public void Refresh_UpdatesCurrent_EvenWhenNobodyListens()
    {
        using AssemblyWatcher watcher = Assemblies.Scan().FromDirectory(_temp.Path).Watch(TimeSpan.FromHours(1));

        Fixture.Compile(_temp.Path, "Unobserved", "public class A { }");
        watcher.Refresh();
        ScanResult afterChange = watcher.Current;
        watcher.Refresh();

        afterChange.Select(a => a.Name).ShouldBe(["Unobserved"]);
        watcher.Current.ShouldBeSameAs(afterChange);
    }

    [Fact]
    public void Refresh_AfterDispose_DoesNothing()
    {
        using AssemblyWatcher watcher = Assemblies.Scan().FromDirectory(_temp.Path).Watch(TimeSpan.FromHours(1));
        bool raised = false;
        watcher.Changed += (_, _) => raised = true;

        watcher.Dispose();
        watcher.Dispose();
        Fixture.Compile(_temp.Path, "TooLate", "public class A { }");
        watcher.Refresh();

        raised.ShouldBeFalse();
        watcher.Current.ShouldBeEmpty();
    }

    [Fact]
    public void Refresh_ReportsAFailedRescan_ThroughTheErrorEvent_AndKeepsTheLastResult()
    {
        bool fail = false;
        using AssemblyWatcher watcher = Assemblies.Scan()
            .FromDirectory(_temp.Path)
            .Where(_ =>
            {
                if (fail)
                    throw new InvalidOperationException("rescan failed");

                return true;
            }, "fails on demand")
            .Watch(TimeSpan.FromHours(1));
        Fixture.Compile(_temp.Path, "Trigger", "public class A { }");
        fail = true;

        // Without a subscriber the failure is swallowed.
        watcher.Refresh();

        Exception? error = null;
        bool changed = false;
        watcher.Error += (_, e) => error = e.GetException();
        watcher.Changed += (_, _) => changed = true;
        watcher.Refresh();

        error.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("rescan failed");
        changed.ShouldBeFalse();
        watcher.Current.ShouldBeEmpty();
    }
}
