namespace LoadingCache;

internal static class LoadChainContext
{
    private static readonly AsyncLocal<Node?> CurrentContext = new();

    internal static Node? Current
    {
        get => CurrentContext.Value;
        set => CurrentContext.Value = value;
    }

    internal static bool Contains(object owner, object key)
    {
        for (Node? node = CurrentContext.Value; node is not null; node = node.Parent)
        {
            if (
                node.Owner.TryGetTarget(out object? candidate)
                && ReferenceEquals(candidate, owner)
                && ((ILoadingCacheKeyOwner)owner).KeysEqual(node.Key, key)
            )
            {
                return true;
            }
        }

        return false;
    }

    internal sealed class Node
    {
        internal Node(WeakReference<object> owner, object key, Node? parent)
        {
            Owner = owner;
            Key = key;
            Parent = parent;
        }

        internal WeakReference<object> Owner { get; }

        internal object Key { get; }

        internal Node? Parent { get; }
    }
}

internal interface ILoadingCacheKeyOwner
{
    bool KeysEqual(object first, object second);
}
