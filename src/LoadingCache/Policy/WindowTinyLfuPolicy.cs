namespace LoadingCache.Policy;

// Copyright 2014 Ben Manes. Portions of the admission, segment sizing, and jitter
// thresholds are adapted from Caffeine 3.2.4's BoundedLocalCache.java (Apache-2.0,
// commit 836b65c0a83e5d1641ded9c6de578654bc04b2e9):
// https://github.com/ben-manes/caffeine/blob/836b65c0a83e5d1641ded9c6de578654bc04b2e9/caffeine/src/main/java/com/github/benmanes/caffeine/cache/BoundedLocalCache.java
// Copyright 2026 Ben Manes. The adaptive hill-climber comparison references Caffeine's
// WindowClimber at commit d885a95eee51fdfe13f450fd9cba80f58f7e0def. This implementation
// retains the fixed stable formula and tiny-cache adjustments selected in ADR-0002.
// The node ownership, long-weight accounting, maintenance budget, and seeded jitter are
// original .NET implementation work. This is an algorithm adaptation, not a source-compatible
// port or a claim of upstream endorsement.
internal sealed class WindowTinyLfuPolicy<T>
    where T : notnull
{
    private const double HillClimberRestartThreshold = 0.05d;
    private const double HillClimberStepPercent = 0.0625d;
    private const double TinyHillClimberStepDecayRate = 0.995d;
    private const double HillClimberStepDecayRate = 0.98d;
    private const int AdmitHashDosThreshold = 6;
    private const int QueueTransferThreshold = 1_000;

    private readonly long _ownerId = PolicyOwnerIds.Next();
    private readonly PolicyDeque<T> _window = new(PolicyQueue.Window);
    private readonly PolicyDeque<T> _probation = new(PolicyQueue.Probation);
    private readonly PolicyDeque<T> _protected = new(PolicyQueue.Protected);
    private readonly FrequencySketch _sketch;
    private readonly bool _lazySketch;
    private readonly bool _adaptive;
    private readonly Random _jitter;
    private PolicyNode<T>? _candidateHead;
    private PolicyNode<T>? _candidateTail;
    private int? _maximumCount;
    private long _hitsInSample;
    private double _previousSampleHitRate;
    internal long Maximum { get; private set; }
    internal long WeightedSize { get; private set; }
    internal long WindowMaximum { get; private set; }
    internal long ProtectedMaximum { get; private set; }
    internal long MissesInSample { get; private set; }
    internal long Adjustment { get; private set; }
    internal double StepSize { get; private set; }

    internal WindowTinyLfuPolicy(
        long maximum,
        uint seed = 0x9E3779B9u,
        bool adaptive = true,
        int? maximumCount = null,
        bool lazySketch = false
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        if (maximumCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount),
                maximumCount,
                "The resident count must be positive."
            );
        }

        Maximum = maximum;
        WindowMaximum = InitialWindowMaximum(maximum);
        ProtectedMaximum = InitialProtectedMaximum(maximum - WindowMaximum);
        _adaptive = adaptive;
        _maximumCount = maximumCount;
        _lazySketch = lazySketch;
        _jitter = new Random(unchecked((int)(seed == 0 ? 0xA341316Cu : seed)));
        StepSize = maximum <= 512 ? StepMagnitude(maximum) : -StepMagnitude(maximum);
        _sketch = new FrequencySketch(seed);
        if (!lazySketch)
        {
            EnsureSketchInitialized();
        }
    }

    internal int ResidentCount => _window.Count + _probation.Count + _protected.Count;

    internal void AssertInvariants()
    {
        HashSet<PolicyNode<T>> nodes = [];
        _window.AssertInvariants(_ownerId, nodes);
        _probation.AssertInvariants(_ownerId, nodes);
        _protected.AssertInvariants(_ownerId, nodes);

        if (nodes.Count != ResidentCount)
        {
            throw new InvalidOperationException("Policy resident count is inconsistent.");
        }

        long weightedSize = nodes.Sum(static node => node.Weight);

        if (weightedSize != WeightedSize)
        {
            throw new InvalidOperationException("Policy weighted size is inconsistent.");
        }

        if (WeightedSize > Maximum)
        {
            throw new InvalidOperationException("Policy weighted size exceeds its maximum.");
        }

        if (_maximumCount is { } maximumCount && ResidentCount > maximumCount)
        {
            throw new InvalidOperationException("Policy resident count exceeds its maximum.");
        }
    }

    internal long MainMaximum => Maximum - WindowMaximum;

    internal long MainProtectedWeightedSize => _protected.WeightedSize;

    internal int ProtectedCount => _protected.Count;

    internal long SketchSampleSize => _sketch.SampleSize;

    internal long SketchSampleCount => _sketch.SampleCount;

    internal bool IsSketchInitialized => _sketch.IsInitialized;

    internal int Frequency(uint hash) => _sketch.Frequency(hash);

    internal IReadOnlyList<PolicyNode<T>> Snapshot(bool hottest, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        var result = new List<PolicyNode<T>>(Math.Min(limit, ResidentCount));
        if (hottest)
        {
            _protected.CopyOrdered(result, limit, newestFirst: true);
            _window.CopyOrdered(result, limit, newestFirst: true);
            _probation.CopyOrdered(result, limit, newestFirst: true);
        }
        else
        {
            _probation.CopyOrdered(result, limit, newestFirst: false);
            _window.CopyOrdered(result, limit, newestFirst: false);
            _protected.CopyOrdered(result, limit, newestFirst: false);
        }

        return result;
    }

    internal IReadOnlyList<PolicyNode<T>> Add(PolicyNode<T> node)
    {
        return AddCore(node, deferEviction: false);
    }

    internal IReadOnlyList<PolicyNode<T>> AddDeferred(PolicyNode<T> node)
    {
        return AddCore(node, deferEviction: true);
    }

    private IReadOnlyList<PolicyNode<T>> AddCore(PolicyNode<T> node, bool deferEviction)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.Claim(_ownerId);

        if (node.Weight > Maximum)
        {
            RecordMiss(node.Hash);
            node.Retire();
            return [node];
        }

        List<PolicyNode<T>>? evicted = null;
        if (WouldOverflow(node.Weight))
        {
            if (!TryEvictForOverflow(node, out evicted))
            {
                RecordMiss(node.Hash);
                node.Retire();
                (evicted ??= []).Add(node);
                return evicted;
            }
        }

        _window.AddLast(node);
        WeightedSize = checked(WeightedSize + node.Weight);
        EnsureSketchInitializedIfThresholdReached();
        RecordMiss(node.Hash);
        if (deferEviction)
        {
            return evicted is null ? Array.Empty<PolicyNode<T>>() : evicted;
        }

        IReadOnlyList<PolicyNode<T>> drained = DrainEvictions();
        return CombineEvictions(evicted, drained);
    }

    internal bool RecordAccess(PolicyNode<T> node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!IsOwnedLiveNode(node))
        {
            return false;
        }

        _sketch.Increment(node.Hash);
        _hitsInSample++;

        switch (node.Queue)
        {
            case PolicyQueue.Window:
                _window.MoveLast(node);
                break;
            case PolicyQueue.Probation:
                ClearCandidate(node);
                _probation.Remove(node);
                _protected.AddLast(node);
                int budget = QueueTransferThreshold;
                DemoteProtectedToMaximum(ref budget);
                break;
            case PolicyQueue.Protected:
                _protected.MoveLast(node);
                break;
            case PolicyQueue.None:
            default:
                return false;
        }

        return true;
    }

    internal void RecordMiss(uint hash)
    {
        _sketch.Increment(hash);
        MissesInSample++;
    }

    internal IReadOnlyList<PolicyNode<T>> Remove(PolicyNode<T> node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!IsOwnedLiveNode(node))
        {
            return [];
        }

        RetireNode(node);
        return [node];
    }

    internal IReadOnlyList<PolicyNode<T>> UpdateWeight(PolicyNode<T> node, long weight)
    {
        return UpdateWeightCore(node, weight, deferEviction: false);
    }

    internal IReadOnlyList<PolicyNode<T>> UpdateWeightDeferred(PolicyNode<T> node, long weight)
    {
        return UpdateWeightCore(node, weight, deferEviction: true);
    }

    private IReadOnlyList<PolicyNode<T>> UpdateWeightCore(
        PolicyNode<T> node,
        long weight,
        bool deferEviction
    )
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentOutOfRangeException.ThrowIfNegative(weight);
        if (!IsOwnedLiveNode(node))
        {
            return [];
        }

        long oldWeight = node.Weight;
        if (oldWeight == weight)
        {
            return [];
        }

        PolicyQueue queue = node.Queue;
        ClearCandidate(node);
        RemoveFromQueue(node);
        WeightedSize -= oldWeight;
        node.SetWeight(weight);

        List<PolicyNode<T>>? evicted = null;
        if (weight > Maximum)
        {
            node.Retire();
            return [node];
        }

        if (WouldOverflow(weight))
        {
            if (!TryEvictForOverflow(node, out evicted))
            {
                node.Retire();
                (evicted ??= []).Add(node);
                return evicted;
            }
        }

        AttachToQueue(node, queue);
        WeightedSize = checked(WeightedSize + weight);
        if (deferEviction)
        {
            return evicted is null ? Array.Empty<PolicyNode<T>>() : evicted;
        }

        IReadOnlyList<PolicyNode<T>> drained = DrainEvictions();
        return CombineEvictions(evicted, drained);
    }

    internal IReadOnlyList<PolicyNode<T>> EvictEntries(int budget = int.MaxValue)
    {
        return DrainEvictions(budget);
    }

    internal IReadOnlyList<PolicyNode<T>> SetMaximum(long maximum)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximum);
        EnsureSketchInitialized();
        if (maximum == Maximum)
        {
            return [];
        }

        long oldMaximum = Maximum;
        long oldMainMaximum = MainMaximum;
        long oldProtectedMaximum = ProtectedMaximum;
        long targetWindow = ScaleSegment(WindowMaximum, oldMaximum, maximum);
        long targetMain = maximum - targetWindow;
        long targetProtected =
            oldMainMaximum == 0
                ? InitialProtectedMaximum(targetMain)
                : ScaleSegment(oldProtectedMaximum, oldMainMaximum, targetMain);

        Maximum = maximum;
        WindowMaximum = targetWindow;
        ProtectedMaximum = Math.Min(targetProtected, targetMain);
        _sketch.EnsureCapacity(EstimatedEntryCount(maximum, _maximumCount));
        _sketch.ResetSampleCount();
        _hitsInSample = 0;
        MissesInSample = 0;
        _previousSampleHitRate = 0;
        StepSize = maximum <= 512 ? StepMagnitude(maximum) : -StepMagnitude(maximum);
        Adjustment = 0;
        int demotionBudget = int.MaxValue;
        DemoteProtectedToMaximum(ref demotionBudget);
        return DrainEvictions();
    }

    internal IReadOnlyList<PolicyNode<T>> SetMaximumCount(int? maximumCount)
    {
        if (maximumCount is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumCount),
                maximumCount,
                "The resident count must be positive."
            );
        }

        _maximumCount = maximumCount;
        return DrainEvictions();
    }

    internal IReadOnlyList<PolicyNode<T>> Maintain(int budget = QueueTransferThreshold)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        EnsureSketchInitializedIfThresholdReached();
        int remainingBudget = budget;
        DemoteProtectedToMaximum(ref remainingBudget);
        bool hadPendingAdjustment = _adaptive && Adjustment != 0;
        if (hadPendingAdjustment)
        {
            ApplyWindowAdjustment(ref remainingBudget);
        }

        if (
            !_adaptive
            || hadPendingAdjustment
            || Adjustment != 0
            || _hitsInSample + MissesInSample < SampleLimit()
        )
        {
            return DrainEvictions(remainingBudget);
        }

        DetermineAdjustment();
        ApplyWindowAdjustment(ref remainingBudget);
        return DrainEvictions(remainingBudget);
    }

    private IReadOnlyList<PolicyNode<T>> DrainEvictions(int budget = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(budget);
        if (budget == 0)
        {
            return [];
        }

        List<PolicyNode<T>>? evicted = null;
        int remainingBudget = budget;

        MoveWindowToMain(ref remainingBudget);
        if (!IsOverCapacity())
        {
            ClearCandidates(ref remainingBudget);
            return [];
        }

        while (IsOverCapacity() && remainingBudget > 0)
        {
            bool countPressure = _maximumCount is { } maximumCount && ResidentCount > maximumCount;
            PolicyNode<T>? candidate = FindCandidate(ref remainingBudget);
            if (candidate is not null && remainingBudget == 0)
            {
                break;
            }

            PolicyNode<T>? victim = FindVictim(candidate, countPressure, ref remainingBudget);
            if (remainingBudget == 0)
            {
                break;
            }

            if (candidate is null && victim is null)
            {
                break;
            }

            if (candidate is not null && (candidate.Weight > Maximum || victim is null))
            {
                RetireNode(candidate);
                (evicted ??= []).Add(candidate);
                remainingBudget--;
                continue;
            }

            if (candidate is null)
            {
                PolicyNode<T> selectedVictim = victim!;
                RetireNode(selectedVictim);
                (evicted ??= []).Add(selectedVictim);
                remainingBudget--;
                continue;
            }

            PolicyNode<T> candidateVictim = victim!;
            if (ShouldAdmit(candidate, candidateVictim))
            {
                ClearCandidate(candidate);
                RetireNode(candidateVictim);
                (evicted ??= []).Add(candidateVictim);
                remainingBudget--;
            }
            else
            {
                RetireNode(candidate);
                (evicted ??= []).Add(candidate);
                remainingBudget--;
            }
        }

        if (evicted is null)
        {
            return [];
        }

        IReadOnlyList<PolicyNode<T>> result = evicted;
        return result;
    }

    private static IReadOnlyList<PolicyNode<T>> CombineEvictions(
        List<PolicyNode<T>>? first,
        IReadOnlyList<PolicyNode<T>> second
    )
    {
        if (first is null)
        {
            return second;
        }

        if (second.Count != 0)
        {
            first.AddRange(second);
        }

        return first;
    }

    private static long InitialWindowMaximum(long maximum)
    {
        long main = maximum - DivideCeiling(maximum, 100);
        return maximum - main;
    }

    private static long InitialProtectedMaximum(long mainMaximum)
    {
        return mainMaximum - DivideCeiling(mainMaximum, 5);
    }

    private static long DivideCeiling(long value, long divisor)
    {
        return value == 0 ? 0 : (value - 1) / divisor + 1;
    }

    private static long ScaleSegment(long segment, long oldMaximum, long newMaximum)
    {
        if (newMaximum == 0)
        {
            return 0;
        }

        if (oldMaximum == 0)
        {
            return 0;
        }

        long scaled = (long)((UInt128)segment * (UInt128)newMaximum / (UInt128)oldMaximum);
        return Math.Clamp(scaled, 1, newMaximum);
    }

    private static double StepMagnitude(long maximum)
    {
        return Math.Max(maximum * HillClimberStepPercent, 2.0);
    }

    private long SampleLimit()
    {
        long sampleSize = _sketch.SampleSize;
        if (Maximum > 512)
        {
            return sampleSize;
        }

        double baseStep = Maximum / 16.0;
        double denominator = Math.Max(Math.Abs(StepSize), baseStep / 4.0);
        double ratio = denominator == 0 ? 1 : Math.Clamp(baseStep / denominator, 1, 4);
        double limit = sampleSize * ratio;
        return limit >= long.MaxValue ? long.MaxValue : Math.Max(1, (long)limit);
    }

    private static long EstimatedEntryCount(long maximum, int? maximumCount)
    {
        return maximumCount ?? Math.Min(maximum, 1_000_000L);
    }

    private void EnsureSketchInitializedIfThresholdReached()
    {
        if (
            _lazySketch
            && !_sketch.IsInitialized
            && ResidentCount != 0
            && ResidentCount >= Maximum >>> 1
        )
        {
            EnsureSketchInitialized();
        }
    }

    private void EnsureSketchInitialized()
    {
        _sketch.EnsureCapacity(EstimatedEntryCount(Maximum, _maximumCount));
    }

    private bool IsOverCapacity()
    {
        return WeightedSize > Maximum
            || (_maximumCount is { } maximumCount && ResidentCount > maximumCount);
    }

    private bool WouldOverflow(long weight)
    {
        return (UInt128)WeightedSize + (UInt128)weight > long.MaxValue;
    }

    private bool TryEvictForOverflow(PolicyNode<T> incoming, out List<PolicyNode<T>>? evicted)
    {
        List<PolicyNode<T>> selected = [];
        HashSet<PolicyNode<T>> excluded = [];
        int scanBudget = int.MaxValue;
        UInt128 total = (UInt128)WeightedSize + (UInt128)incoming.Weight;
        while (total > long.MaxValue)
        {
            PolicyNode<T>? victim = FindWeightVictim(ref scanBudget, excluded);
            if (victim is null || !ShouldAdmit(incoming, victim))
            {
                evicted = null;
                return false;
            }

            selected.Add(victim);
            excluded.Add(victim);
            total -= (UInt128)victim.Weight;
        }

        evicted = selected;
        foreach (PolicyNode<T> victim in selected)
        {
            RetireNode(victim);
        }

        return true;
    }

    private bool IsOwnedLiveNode(PolicyNode<T> node)
    {
        return node.IsAlive && node.OwnerId == _ownerId && node.Queue != PolicyQueue.None;
    }

    private void MoveWindowToMain(ref int budget)
    {
        while (_window.WeightedSize > WindowMaximum && budget > 0)
        {
            PolicyNode<T>? node = _window.FirstPositive(ref budget);
            if (node is null)
            {
                break;
            }

            if (budget == 0)
            {
                break;
            }

            _window.Remove(node);
            _probation.AddLast(node);
            MarkCandidate(node);
            budget--;
        }
    }

    private void DemoteProtectedToMaximum(ref int budget)
    {
        while (_protected.WeightedSize > ProtectedMaximum && budget > 0)
        {
            PolicyNode<T>? node = _protected.RemoveFirst();
            if (node is null)
            {
                break;
            }

            ClearCandidate(node);
            _probation.AddLast(node);
            budget--;
        }
    }

    private void ClearCandidates(ref int budget)
    {
        while (_candidateHead is not null)
        {
            if (!ConsumeWork(ref budget))
            {
                return;
            }

            ClearCandidate(_candidateHead);
        }
    }

    private PolicyNode<T>? FindCandidate(ref int budget)
    {
        if (_candidateHead is null || budget <= 0)
        {
            return null;
        }

        return _candidateHead;
    }

    private PolicyNode<T>? FindVictim(PolicyNode<T>? candidate, bool countPressure, ref int budget)
    {
        PolicyNode<T>? victim = countPressure
            ? _probation.FirstEligible(ref budget)
            : _probation.FirstPositiveNonCandidate(ref budget);
        if (victim is null && budget > 0)
        {
            victim = countPressure
                ? _protected.FirstEligible(ref budget)
                : _protected.FirstPositive(ref budget);
        }

        if (victim is null && budget > 0)
        {
            victim = countPressure
                ? _window.FirstEligible(ref budget)
                : _window.FirstPositive(ref budget);
        }

        return ReferenceEquals(victim, candidate) ? null : victim;
    }

    private PolicyNode<T>? FindWeightVictim(ref int budget, ISet<PolicyNode<T>> excluded)
    {
        PolicyNode<T>? victim = _probation.FirstPositiveNonCandidate(ref budget, excluded);
        if (victim is null && budget > 0)
        {
            victim = _probation.FirstPositive(ref budget, excluded);
        }

        if (victim is null && budget > 0)
        {
            victim = _protected.FirstPositiveNonCandidate(ref budget, excluded);
        }

        if (victim is null && budget > 0)
        {
            victim = _protected.FirstPositive(ref budget, excluded);
        }

        if (victim is null && budget > 0)
        {
            victim = _window.FirstPositiveNonCandidate(ref budget, excluded);
        }

        if (victim is null && budget > 0)
        {
            victim = _window.FirstPositive(ref budget, excluded);
        }

        return victim;
    }

    private static bool ConsumeWork(ref int budget)
    {
        if (budget <= 0)
        {
            return false;
        }

        budget--;
        return true;
    }

    private void MarkCandidate(PolicyNode<T> node)
    {
        if (node.IsCandidate)
        {
            return;
        }

        if (node.Queue == PolicyQueue.Probation)
        {
            _probation.MakeIneligible(node);
        }

        node.IsCandidate = true;
        node.CandidatePrevious = _candidateTail;
        node.CandidateNext = null;
        if (_candidateTail is null)
        {
            _candidateHead = node;
        }
        else
        {
            _candidateTail.CandidateNext = node;
        }

        _candidateTail = node;
    }

    private void ClearCandidate(PolicyNode<T> node)
    {
        if (!node.IsCandidate)
        {
            return;
        }

        PolicyNode<T>? previous = node.CandidatePrevious;
        PolicyNode<T>? next = node.CandidateNext;
        if (previous is null)
        {
            _candidateHead = next;
        }
        else
        {
            previous.CandidateNext = next;
        }

        if (next is null)
        {
            _candidateTail = previous;
        }
        else
        {
            next.CandidatePrevious = previous;
        }

        node.IsCandidate = false;
        node.CandidatePrevious = null;
        node.CandidateNext = null;
        if (node.Queue == PolicyQueue.Probation)
        {
            _probation.MakeEligible(node);
        }
    }

    private bool ShouldAdmit(PolicyNode<T> candidate, PolicyNode<T> victim)
    {
        int candidateFrequency = _sketch.Frequency(candidate.Hash);
        int victimFrequency = _sketch.Frequency(victim.Hash);
        if (candidateFrequency > victimFrequency)
        {
            return true;
        }

        if (candidateFrequency < AdmitHashDosThreshold)
        {
            return false;
        }

        return _jitter.Next(128) == 0;
    }

    private void RetireNode(PolicyNode<T> node)
    {
        ClearCandidate(node);
        RemoveFromQueue(node);
        WeightedSize -= node.Weight;
        node.Retire();
    }

    private void RemoveFromQueue(PolicyNode<T> node)
    {
        switch (node.Queue)
        {
            case PolicyQueue.Window:
                _window.Remove(node);
                break;
            case PolicyQueue.Probation:
                _probation.Remove(node);
                break;
            case PolicyQueue.Protected:
                _protected.Remove(node);
                break;
            case PolicyQueue.None:
                break;
            default:
                throw new InvalidOperationException("Unknown policy queue.");
        }
    }

    private void AttachToQueue(PolicyNode<T> node, PolicyQueue queue)
    {
        switch (queue)
        {
            case PolicyQueue.Window:
                _window.AddLast(node);
                break;
            case PolicyQueue.Probation:
                _probation.AddLast(node);
                break;
            case PolicyQueue.Protected:
                _protected.AddLast(node);
                break;
            case PolicyQueue.None:
                throw new InvalidOperationException("A live node must have a resident queue.");
            default:
                throw new InvalidOperationException("Unknown policy queue.");
        }
    }

    private void DetermineAdjustment()
    {
        if (!_sketch.IsInitialized)
        {
            _previousSampleHitRate = 0;
            _hitsInSample = 0;
            MissesInSample = 0;
            return;
        }

        long requestCount = _hitsInSample + MissesInSample;
        if (requestCount < SampleLimit())
        {
            return;
        }

        double hitRate = (double)_hitsInSample / requestCount;
        double hitRateChange = hitRate - _previousSampleHitRate;
        double amount = hitRateChange >= 0 ? StepSize : -StepSize;
        double nextMagnitude =
            Math.Abs(hitRateChange) >= HillClimberRestartThreshold
                ? StepMagnitude(Maximum)
                : Math.Abs(amount)
                    * (Maximum <= 512 ? TinyHillClimberStepDecayRate : HillClimberStepDecayRate);

        _previousSampleHitRate = hitRate;
        Adjustment = TruncateTowardZero(amount);
        StepSize = amount < 0 ? -nextMagnitude : nextMagnitude;
        _hitsInSample = 0;
        MissesInSample = 0;
    }

    private static long TruncateTowardZero(double value)
    {
        return value switch
        {
            >= long.MaxValue => long.MaxValue,
            <= long.MinValue => long.MinValue,
            _ => (long)value,
        };
    }

    private void ApplyWindowAdjustment(ref int budget)
    {
        long amount = Adjustment;
        Adjustment = 0;
        switch (amount)
        {
            case > 0:
                IncreaseWindow(amount, ref budget);
                break;
            case < 0:
                DecreaseWindow(-amount, ref budget);
                break;
        }
    }

    private void IncreaseWindow(long amount, ref int budget)
    {
        if (ProtectedMaximum == 0)
        {
            return;
        }

        long quota = Math.Min(amount, ProtectedMaximum);
        ProtectedMaximum -= quota;
        WindowMaximum += quota;
        DemoteProtectedToMaximum(ref budget);

        long remaining = quota;
        for (int i = 0; i < QueueTransferThreshold && remaining > 0 && budget > 0; i++)
        {
            PolicyNode<T>? node = _probation.FirstPositive(ref budget);
            bool fromProbation = node is not null;
            node ??= _protected.FirstPositive(ref budget);
            if (node is null || node.Weight > remaining)
            {
                break;
            }

            if (budget == 0)
            {
                break;
            }

            if (fromProbation)
            {
                ClearCandidate(node);
                _probation.Remove(node);
            }
            else
            {
                _protected.Remove(node);
            }

            _window.AddLast(node);
            remaining -= node.Weight;
            budget--;
        }

        if (remaining <= 0)
        {
            return;
        }

        WindowMaximum -= remaining;
        ProtectedMaximum += remaining;
        Adjustment = remaining;
    }

    private void DecreaseWindow(long amount, ref int budget)
    {
        long quota = Math.Min(amount, Math.Max(0, WindowMaximum - 1));
        if (quota == 0)
        {
            return;
        }

        WindowMaximum -= quota;
        ProtectedMaximum += quota;
        long remaining = quota;
        for (int i = 0; i < QueueTransferThreshold && remaining > 0 && budget > 0; i++)
        {
            PolicyNode<T>? node = _window.FirstPositive(ref budget);
            if (node is null || node.Weight > remaining)
            {
                break;
            }

            if (budget == 0)
            {
                break;
            }

            _window.Remove(node);
            _probation.AddLast(node);
            MarkCandidate(node);
            remaining -= node.Weight;
            budget--;
        }

        if (remaining > 0)
        {
            WindowMaximum += remaining;
            ProtectedMaximum -= remaining;
            Adjustment = -remaining;
        }
        DemoteProtectedToMaximum(ref budget);
    }

    private sealed class PolicyDeque<TValue>
        where TValue : notnull
    {
        private readonly PolicyQueue _queue;

        internal PolicyDeque(PolicyQueue queue)
        {
            _queue = queue;
        }

        private PolicyNode<TValue>? Head { get; set; }

        private PolicyNode<TValue>? Tail { get; set; }

        internal int Count { get; private set; }

        internal long WeightedSize { get; private set; }

        private PolicyNode<TValue>? PositiveHead { get; set; }

        private PolicyNode<TValue>? PositiveTail { get; set; }

        private PolicyNode<TValue>? EligibleHead { get; set; }

        private PolicyNode<TValue>? EligibleTail { get; set; }

        private PolicyNode<TValue>? EligiblePositiveHead { get; set; }

        private PolicyNode<TValue>? EligiblePositiveTail { get; set; }

        internal void CopyOrdered(List<PolicyNode<TValue>> destination, int limit, bool newestFirst)
        {
            PolicyNode<TValue>? node = newestFirst ? Tail : Head;
            while (node is not null && destination.Count < limit)
            {
                destination.Add(node);
                node = newestFirst ? node.Previous : node.Next;
            }
        }

        internal void AssertInvariants(long ownerId, HashSet<PolicyNode<TValue>> seen)
        {
            int count = 0;
            long weightedSize = 0;
            PolicyNode<TValue>? previous = null;
            for (PolicyNode<TValue>? node = Head; node is not null; node = node.Next)
            {
                if (
                    !seen.Add(node)
                    || !node.IsAlive
                    || node.OwnerId != ownerId
                    || node.Queue != _queue
                    || !ReferenceEquals(node.Previous, previous)
                    || (node.Next is not null && !ReferenceEquals(node.Next.Previous, node))
                )
                {
                    throw new InvalidOperationException("Policy deque links are inconsistent.");
                }

                count++;
                weightedSize = checked(weightedSize + node.Weight);
                previous = node;
            }

            if (!ReferenceEquals(Tail, previous) || count != Count || weightedSize != WeightedSize)
            {
                throw new InvalidOperationException("Policy deque accounting is inconsistent.");
            }
        }

        internal void AddLast(PolicyNode<TValue> node)
        {
            if (node.Queue != PolicyQueue.None || !node.IsAlive)
            {
                throw new InvalidOperationException("A node is already linked or retired.");
            }

            node.Queue = _queue;
            node.Previous = Tail;
            node.Next = null;
            if (Tail is null)
            {
                Head = node;
            }
            else
            {
                Tail.Next = node;
            }

            Tail = node;
            Count++;
            WeightedSize = checked(WeightedSize + node.Weight);
            if (node.Weight > 0)
            {
                AddPositiveLast(node);
            }

            if (node.IsCandidate)
            {
                return;
            }

            AddEligibleLast(node);
            if (node.Weight > 0)
            {
                AddEligiblePositiveLast(node);
            }
        }

        internal void Remove(PolicyNode<TValue> node)
        {
            if (node.Queue != _queue)
            {
                throw new InvalidOperationException("A node does not belong to this policy queue.");
            }

            PolicyNode<TValue>? previous = node.Previous;
            PolicyNode<TValue>? next = node.Next;
            if (previous is null)
            {
                Head = next;
            }
            else
            {
                previous.Next = next;
            }

            if (next is null)
            {
                Tail = previous;
            }
            else
            {
                next.Previous = previous;
            }

            if (!node.IsCandidate)
            {
                RemoveEligible(node);
                if (node.Weight > 0)
                {
                    RemoveEligiblePositive(node);
                }
            }

            if (node.Weight > 0)
            {
                RemovePositive(node);
            }

            node.Previous = null;
            node.Next = null;
            node.Queue = PolicyQueue.None;
            Count--;
            WeightedSize -= node.Weight;
        }

        internal void MoveLast(PolicyNode<TValue> node)
        {
            if (node.Queue != _queue)
            {
                throw new InvalidOperationException("A node does not belong to this policy queue.");
            }

            if (ReferenceEquals(node, Tail))
            {
                return;
            }

            PolicyNode<TValue>? previous = node.Previous;
            PolicyNode<TValue>? next = node.Next;
            if (previous is null)
            {
                Head = next;
            }
            else
            {
                previous.Next = next;
            }

            next!.Previous = previous;
            node.Previous = Tail;
            node.Next = null;
            Tail!.Next = node;
            Tail = node;
            if (node.Weight > 0)
            {
                RemovePositive(node);
                AddPositiveLast(node);
            }

            if (node.IsCandidate)
            {
                return;
            }

            RemoveEligible(node);
            AddEligibleLast(node);
            if (node.Weight == 0)
            {
                return;
            }

            RemoveEligiblePositive(node);
            AddEligiblePositiveLast(node);
        }

        internal PolicyNode<TValue>? RemoveFirst()
        {
            PolicyNode<TValue>? node = Head;
            if (node is not null)
            {
                Remove(node);
            }

            return node;
        }

        internal PolicyNode<TValue>? FirstPositive(
            ref int budget,
            ISet<PolicyNode<TValue>>? excluded = null
        )
        {
            if (PositiveHead is null || budget <= 0)
            {
                return null;
            }

            if (excluded is null || !excluded.Contains(PositiveHead))
            {
                return PositiveHead;
            }

            for (
                PolicyNode<TValue>? node = PositiveHead.PositiveNext;
                node is not null;
                node = node.PositiveNext
            )
            {
                if (!excluded.Contains(node))
                {
                    return node;
                }

                budget--;
                if (budget <= 0)
                {
                    return null;
                }
            }

            return null;
        }

        internal PolicyNode<TValue>? FirstPositiveNonCandidate(
            ref int budget,
            ISet<PolicyNode<TValue>>? excluded = null
        )
        {
            if (EligiblePositiveHead is null || budget <= 0)
            {
                return null;
            }

            if (excluded is null || !excluded.Contains(EligiblePositiveHead))
            {
                return EligiblePositiveHead;
            }

            for (
                PolicyNode<TValue>? node = EligiblePositiveHead.EligiblePositiveNext;
                node is not null;
                node = node.EligiblePositiveNext
            )
            {
                if (!excluded.Contains(node))
                {
                    return node;
                }

                budget--;
                if (budget <= 0)
                {
                    return null;
                }
            }

            return null;
        }

        internal PolicyNode<TValue>? FirstEligible(ref int budget)
        {
            return EligibleHead is null || budget <= 0 ? null : EligibleHead;
        }

        internal void MakeIneligible(PolicyNode<TValue> node)
        {
            if (node.IsCandidate)
            {
                return;
            }

            RemoveEligible(node);
            if (node.Weight > 0)
            {
                RemoveEligiblePositive(node);
            }
        }

        internal void MakeEligible(PolicyNode<TValue> node)
        {
            if (node.IsCandidate || node.Queue != _queue)
            {
                return;
            }

            AddEligibleLast(node);
            if (node.Weight > 0)
            {
                AddEligiblePositiveLast(node);
            }
        }

        private void AddPositiveLast(PolicyNode<TValue> node)
        {
            node.PositivePrevious = PositiveTail;
            node.PositiveNext = null;
            if (PositiveTail is null)
            {
                PositiveHead = node;
            }
            else
            {
                PositiveTail.PositiveNext = node;
            }

            PositiveTail = node;
        }

        private void RemovePositive(PolicyNode<TValue> node)
        {
            PolicyNode<TValue>? previous = node.PositivePrevious;
            PolicyNode<TValue>? next = node.PositiveNext;
            if (previous is null)
            {
                PositiveHead = next;
            }
            else
            {
                previous.PositiveNext = next;
            }

            if (next is null)
            {
                PositiveTail = previous;
            }
            else
            {
                next.PositivePrevious = previous;
            }

            node.PositivePrevious = null;
            node.PositiveNext = null;
        }

        private void AddEligibleLast(PolicyNode<TValue> node)
        {
            node.EligiblePrevious = EligibleTail;
            node.EligibleNext = null;
            if (EligibleTail is null)
            {
                EligibleHead = node;
            }
            else
            {
                EligibleTail.EligibleNext = node;
            }

            EligibleTail = node;
        }

        private void RemoveEligible(PolicyNode<TValue> node)
        {
            PolicyNode<TValue>? previous = node.EligiblePrevious;
            PolicyNode<TValue>? next = node.EligibleNext;
            if (previous is null)
            {
                EligibleHead = next;
            }
            else
            {
                previous.EligibleNext = next;
            }

            if (next is null)
            {
                EligibleTail = previous;
            }
            else
            {
                next.EligiblePrevious = previous;
            }

            node.EligiblePrevious = null;
            node.EligibleNext = null;
        }

        private void AddEligiblePositiveLast(PolicyNode<TValue> node)
        {
            node.EligiblePositivePrevious = EligiblePositiveTail;
            node.EligiblePositiveNext = null;
            if (EligiblePositiveTail is null)
            {
                EligiblePositiveHead = node;
            }
            else
            {
                EligiblePositiveTail.EligiblePositiveNext = node;
            }

            EligiblePositiveTail = node;
        }

        private void RemoveEligiblePositive(PolicyNode<TValue> node)
        {
            PolicyNode<TValue>? previous = node.EligiblePositivePrevious;
            PolicyNode<TValue>? next = node.EligiblePositiveNext;
            if (previous is null)
            {
                EligiblePositiveHead = next;
            }
            else
            {
                previous.EligiblePositiveNext = next;
            }

            if (next is null)
            {
                EligiblePositiveTail = previous;
            }
            else
            {
                next.EligiblePositivePrevious = previous;
            }

            node.EligiblePositivePrevious = null;
            node.EligiblePositiveNext = null;
        }
    }
}
