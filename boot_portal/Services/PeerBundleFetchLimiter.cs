namespace boot_portal.Services;

public sealed class PeerBundleFetchLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly object _sync = new();
    private readonly Dictionary<string, Queue<DateTime>> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public PeerBundleFetchLimiter(int limit, TimeSpan window)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window));
        }

        _limit = limit;
        _window = window;
    }

    public bool TryAcquire(string peerEndpoint, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(peerEndpoint))
        {
            return false;
        }

        lock (_sync)
        {
            if (!_attempts.TryGetValue(peerEndpoint, out Queue<DateTime>? attempts))
            {
                attempts = new Queue<DateTime>();
                _attempts[peerEndpoint] = attempts;
            }

            DateTime cutoff = nowUtc - _window;
            while (attempts.TryPeek(out DateTime attempt) && attempt <= cutoff)
            {
                attempts.Dequeue();
            }

            if (attempts.Count >= _limit)
            {
                return false;
            }

            attempts.Enqueue(nowUtc);
            return true;
        }
    }
}
