namespace LoadingCache;

internal sealed class CachePolicyView<TKey, TValue> : ICachePolicy<TKey, TValue>
    where TKey : notnull
    where TValue : notnull
{
    internal CachePolicyView(
        IEvictionPolicy<TKey, TValue> eviction,
        Func<TKey, (bool Found, TValue Value)> quietLookup,
        Func<MemoryPressureStatistics?> memoryPressureStatistics,
        Func<TimeSpan?> getExpireAfterAccess,
        Action<TimeSpan> setExpireAfterAccess,
        Func<TKey, TimeSpan?> getAccessRemaining,
        Func<TKey, TimeSpan?> getAccessAge,
        Func<TimeSpan?> getExpireAfterWrite,
        Action<TimeSpan> setExpireAfterWrite,
        Func<TKey, TimeSpan?> getWriteRemaining,
        Func<TKey, TimeSpan?> getWriteAge,
        Func<TimeSpan?> getRefreshAfterWrite,
        Action<TimeSpan> setRefreshAfterWrite,
        Func<TKey, TimeSpan?> getRefreshRemaining,
        Func<TKey, TimeSpan?> getRefreshAge,
        IVariableExpirationPolicy<TKey, TValue>? variableExpiration
    )
    {
        Eviction = eviction;
        _quietLookup = quietLookup;
        _memoryPressureStatistics = memoryPressureStatistics;
        ExpireAfterAccess = getExpireAfterAccess() is not null
            ? new FixedExpirationView<TKey, TValue>(
                getExpireAfterAccess,
                setExpireAfterAccess,
                getAccessRemaining,
                getAccessAge
            )
            : null;
        ExpireAfterWrite = getExpireAfterWrite() is not null
            ? new FixedExpirationView<TKey, TValue>(
                getExpireAfterWrite,
                setExpireAfterWrite,
                getWriteRemaining,
                getWriteAge
            )
            : null;
        RefreshAfterWrite = getRefreshAfterWrite() is not null
            ? new FixedExpirationView<TKey, TValue>(
                getRefreshAfterWrite,
                setRefreshAfterWrite,
                getRefreshRemaining,
                getRefreshAge
            )
            : null;
        VariableExpiration = variableExpiration;
    }

    public IEvictionPolicy<TKey, TValue>? Eviction { get; }

    public IFixedExpirationPolicy<TKey, TValue>? ExpireAfterAccess { get; }

    public IFixedExpirationPolicy<TKey, TValue>? ExpireAfterWrite { get; }

    public IFixedExpirationPolicy<TKey, TValue>? RefreshAfterWrite { get; }

    public IVariableExpirationPolicy<TKey, TValue>? VariableExpiration { get; }

    private readonly Func<TKey, (bool Found, TValue Value)> _quietLookup;
    private readonly Func<MemoryPressureStatistics?> _memoryPressureStatistics;

    public MemoryPressureStatistics? MemoryPressureStatistics => _memoryPressureStatistics();

    public bool TryGetQuietly(
        TKey key,
        [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out TValue value
    )
    {
        (bool found, TValue result) = _quietLookup(key);
        value = result;
        return found;
    }

    private sealed class FixedExpirationView<TPolicyKey, TPolicyValue>
        : IFixedExpirationPolicy<TPolicyKey, TPolicyValue>
        where TPolicyKey : notnull
        where TPolicyValue : notnull
    {
        private readonly Func<TimeSpan?> _getDuration;
        private readonly Action<TimeSpan> _setDuration;
        private readonly Func<TPolicyKey, TimeSpan?> _getRemaining;
        private readonly Func<TPolicyKey, TimeSpan?> _getAge;

        internal FixedExpirationView(
            Func<TimeSpan?> getDuration,
            Action<TimeSpan> setDuration,
            Func<TPolicyKey, TimeSpan?> getRemaining,
            Func<TPolicyKey, TimeSpan?> getAge
        )
        {
            _getDuration = getDuration;
            _setDuration = setDuration;
            _getRemaining = getRemaining;
            _getAge = getAge;
        }

        public TimeSpan Duration => _getDuration() ?? TimeSpan.MaxValue;

        public TimeSpan? GetExpiresAfter(TPolicyKey key) => _getRemaining(key);

        public void SetDuration(TimeSpan duration) => _setDuration(duration);

        public void SetExpiresAfter(TimeSpan duration) => _setDuration(duration);

        public TimeSpan? AgeOf(TPolicyKey key) => _getAge(key);
    }

    internal sealed class VariableExpirationView<TPolicyKey, TPolicyValue>
        : IVariableExpirationPolicy<TPolicyKey, TPolicyValue>
        where TPolicyKey : notnull
        where TPolicyValue : notnull
    {
        private readonly Func<TPolicyKey, TimeSpan?> _getRemaining;
        private readonly Func<TPolicyKey, TimeSpan, bool> _setRemaining;
        private readonly Func<TPolicyKey, TimeSpan?> _getAge;
        private readonly Action<TPolicyKey, TPolicyValue, TimeSpan> _put;

        internal VariableExpirationView(
            Func<TPolicyKey, TimeSpan?> getRemaining,
            Func<TPolicyKey, TimeSpan, bool> setRemaining,
            Func<TPolicyKey, TimeSpan?> getAge,
            Action<TPolicyKey, TPolicyValue, TimeSpan> put
        )
        {
            _getRemaining = getRemaining;
            _setRemaining = setRemaining;
            _getAge = getAge;
            _put = put;
        }

        public TimeSpan? GetExpiresAfter(TPolicyKey key) => _getRemaining(key);

        public bool SetExpiresAfter(TPolicyKey key, TimeSpan duration)
        {
            return _setRemaining(key, duration);
        }

        public TimeSpan? AgeOf(TPolicyKey key) => _getAge(key);

        public void Put(TPolicyKey key, TPolicyValue value, TimeSpan duration) =>
            _put(key, value, duration);
    }
}
