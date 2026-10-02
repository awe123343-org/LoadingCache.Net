using System.Reflection;
using FluentAssertions;

namespace LoadingCache.Tests;

public sealed class SyncFlightCompletionTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(10);

    [Test]
    public void FlightsWithIdenticalKeysAndGenerationsRemainDistinct()
    {
        object first = new FlightProbe().Identity;
        object second = new FlightProbe().Identity;

        first.Equals(first).Should().BeTrue();
        first.Equals(second).Should().BeFalse();
        new HashSet<object> { first, second }
            .Should()
            .HaveCount(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TaskViewIsStableBeforeAndAfterSuccessfulCompletion(bool requestTaskFirst)
    {
        var flight = new FlightProbe();
        Task<string>? earlyTask = requestTaskFirst ? flight.Task : null;

        flight.Set("value");

        flight.Wait().Should().Be("value");
        Task<string> task = earlyTask ?? flight.Task;
        flight.Task.Should().BeSameAs(task);
        (await task.WaitAsync(Watchdog)).Should().Be("value");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task TaskAndMonitorWaitersObserveTheSameFailure(bool requestTaskFirst)
    {
        var flight = new FlightProbe();
        var failure = new InvalidOperationException("loader failed");
        Task<string>? earlyTask = requestTaskFirst ? flight.Task : null;

        flight.Set(failure);

        var waitFailure = FluentActions
            .Invoking(() => flight.Wait())
            .Should()
            .Throw<TargetInvocationException>();
        waitFailure.Which.InnerException.Should().BeSameAs(failure);
        Task<string> task = earlyTask ?? flight.Task;
        flight.Task.Should().BeSameAs(task);
        var taskFailure = await FluentActions
            .Awaiting(() => task.WaitAsync(Watchdog))
            .Should()
            .ThrowExactlyAsync<InvalidOperationException>();
        taskFailure.Which.Should().BeSameAs(failure);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DisposalCompletesTaskViewsCreatedBeforeOrAfterIt(bool requestTaskFirst)
    {
        var flight = new FlightProbe();
        Task<string>? earlyTask = requestTaskFirst ? flight.Task : null;

        flight.Dispose();

        Task<string> task = earlyTask ?? flight.Task;
        flight.Task.Should().BeSameAs(task);
        await FluentActions
            .Awaiting(() => task.WaitAsync(Watchdog))
            .Should()
            .ThrowExactlyAsync<ObjectDisposedException>();
    }

    [Test]
    public async Task ConcurrentTaskRequestsShareOneCompletion()
    {
        var flight = new FlightProbe();
        using var start = new ManualResetEventSlim();
        Task<object>[] requests = Enumerable
            .Range(0, 8)
            .Select(_ =>
                Task.Factory.StartNew(
                    () =>
                    {
                        start.Wait(Watchdog).Should().BeTrue();
                        return (object)flight.Task;
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default
                )
            )
            .ToArray();

        start.Set();
        flight.Set("value");
        object[] tasks = await Task.WhenAll(requests).WaitAsync(Watchdog);
        tasks.Should().OnlyContain(task => ReferenceEquals(task, flight.Task));
        (await flight.Task.WaitAsync(Watchdog)).Should().Be("value");
    }

    [Test]
    public async Task SynchronousLoaderReentrancyFlowsThroughTaskRun()
    {
        ILoadingCache<int, string> cache = null!;
        cache = CacheBuilder
            .Create<int, string>()
            .MaximumSize(4)
            .BuildLoading(key => Task.Run(() => cache.Get(key)).GetAwaiter().GetResult());
        using (cache)
        {
            Task<string> load = Task.Factory.StartNew(
                () => cache.Get(1),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default
            );
            await FluentActions
                .Awaiting(() => load.WaitAsync(Watchdog))
                .Should()
                .ThrowExactlyAsync<LoadingCacheReentrancyException>();
        }
    }

    // Exercise both completion/request orders directly, without relying on which worker
    // wins the public refresh method's queue-before-task-view race.
    private sealed class FlightProbe
    {
        private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly Type FlightType = typeof(CacheEngine<,>)
            .GetNestedType("SyncFlight", BindingFlags.NonPublic)!
            .MakeGenericType(typeof(int), typeof(string));
        private readonly object _flight = Activator.CreateInstance(
            FlightType,
            Members,
            binder: null,
            args: [1, 1L, 1L, (Func<int, string>)(static _ => "unused")],
            culture: null
        )!;

        internal object Identity => _flight;

        internal Task<string> Task =>
            (
                (TaskCompletionSource<string>)
                    FlightType.GetProperty("Completion", Members)!.GetValue(_flight)!
            ).Task;

        internal string Wait() =>
            (string)FlightType.GetMethod("Wait", Members)!.Invoke(_flight, null)!;

        internal void Set(string value) => Set(typeof(string), value);

        internal void Set(Exception exception) => Set(typeof(Exception), exception);

        internal void Dispose() =>
            FlightType.GetMethod("SetDisposed", Members)!.Invoke(_flight, null);

        private void Set(Type argumentType, object value) =>
            FlightType
                .GetMethod("Set", Members, null, [argumentType], null)!
                .Invoke(_flight, [value]);
    }
}
