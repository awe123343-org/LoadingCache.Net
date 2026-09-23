namespace LoadingCache.Tests;

public sealed class ReadRecordingStateTests
{
    [Test]
    [Arguments("flush")]
    [Arguments("cleanup")]
    [Arguments("snapshot")]
    public async Task WriteDrainActivatesReadRecordingAndClearRestoresColdStart(string drain)
    {
        using WindowTinyLfuEnginePolicy policy = CreatePolicy(maximum: 4);
        var first = CreateToken(1);
        var second = CreateToken(2);
        policy.OnPublish(first, 1);
        DrainWrites(policy, drain);
        policy.OnAccess(first);
        await Assert.That(policy.IsSketchInitialized).IsFalse();
        await Assert.That(policy.GetReadBufferStatistics().Enqueued).IsEqualTo(0);
        policy.OnPublish(second, 1);
        DrainWrites(policy, drain);
        policy.OnAccess(second);
        await Assert.That(policy.IsSketchInitialized).IsTrue();
        await Assert.That(policy.GetReadBufferStatistics().Enqueued).IsEqualTo(1);
        policy.Clear();
        var third = CreateToken(3);
        var fourth = CreateToken(4);
        policy.OnPublish(third, 1);
        DrainWrites(policy, drain);
        policy.OnAccess(first);
        policy.OnAccess(third);
        await Assert.That(policy.IsSketchInitialized).IsFalse();
        await Assert.That(policy.GetReadBufferStatistics().Enqueued).IsEqualTo(1);
        policy.OnPublish(fourth, 1);
        DrainWrites(policy, drain);
        policy.OnAccess(fourth);
        await Assert.That(policy.IsSketchInitialized).IsTrue();
        await Assert.That(policy.GetReadBufferStatistics().Enqueued).IsEqualTo(2);
        policy.CleanUp();
        await Assert.That(policy.GetReadBufferStatistics().Queued).IsEqualTo(0);
        await Assert
            .That(policy.Snapshot(hottest: true, limit: 4))
            .IsEquivalentTo([third.Entry, fourth.Entry]);
    }

    [Test]
    [Arguments(6)]
    [Arguments(8)]
    [Arguments(16)]
    public async Task ResizeActivatesReadsAndRemovalBelowThresholdDoesNotDisableThem(int maximum)
    {
        using WindowTinyLfuEnginePolicy policy = CreatePolicy(maximum: 8);
        var first = CreateToken(1);
        policy.OnPublish(first, 1);
        policy.FlushWrites();
        policy.OnAccess(first);
        await Assert.That(policy.IsSketchInitialized).IsFalse();
        await Assert.That(policy.GetReadBufferStatistics().Enqueued).IsEqualTo(0);
        // Same-size, smaller, and larger maxima all explicitly initialize the sketch,
        // even though one resident is below every selected half-capacity threshold.
        policy.SetMaximum(maximum, weighted: false);
        policy.OnAccess(first);
        await Assert.That(policy.IsSketchInitialized).IsTrue();
        await Assert.That(policy.GetReadBufferStatistics().Enqueued).IsEqualTo(1);
        policy.OnRemove(first);
        policy.FlushWrites();
        await Assert.That(policy.ResidentCount).IsEqualTo(0);
        var second = CreateToken(2);
        policy.OnPublish(second, 1);
        policy.FlushWrites();
        policy.OnAccess(second);
        await Assert.That(policy.IsSketchInitialized).IsTrue();
        await Assert.That(policy.GetReadBufferStatistics().Enqueued).IsEqualTo(2);
        policy.CleanUp();
        await Assert.That(policy.GetReadBufferStatistics().Queued).IsEqualTo(0);
        await Assert.That(policy.Snapshot(hottest: true, limit: 4)).IsEquivalentTo([second.Entry]);
    }

    private static void DrainWrites(WindowTinyLfuEnginePolicy policy, string drain)
    {
        switch (drain)
        {
            case "flush":
                policy.FlushWrites();
                break;
            case "cleanup":
                policy.CleanUp();
                break;
            case "snapshot":
                policy.Snapshot(hottest: true, limit: 4);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(drain));
        }
    }

    private static WindowTinyLfuEnginePolicy CreatePolicy(long maximum) =>
        new(
            maximum,
            maximumResidentCount: checked((int)maximum),
            static _ => { },
            requestMaintenance: static () => false,
            beforeMaintenance: null,
            beforeMaintenanceSignalClear: null,
            readStripeCount: 1,
            readStripeCapacity: 8,
            enableColdStart: true
        );

    private static WindowTinyLfuEnginePolicy.EngineEntryToken CreateToken(int value) =>
        new(value, unchecked((uint)value * 0x9E3779B9u));
}
