using System.Collections.Concurrent;

namespace Vht.Transport.Auth;

/// <summary>Rate-limiting policy for authentication attempts (ARCHITECTURE.md §6.1).</summary>
public sealed record RateLimitOptions
{
    /// <summary>Attempts an individual peer may make in a burst before throttling starts.</summary>
    public int PeerBurst { get; init; } = 5;

    /// <summary>Attempts per minute an individual peer earns back.</summary>
    public double PeerRefillPerMinute { get; init; } = 12;

    /// <summary>Attempts all peers together may make in a burst.</summary>
    public int GlobalBurst { get; init; } = 64;

    /// <summary>Attempts per minute the global bucket earns back.</summary>
    public double GlobalRefillPerMinute { get; init; } = 120;

    /// <summary>Consecutive failures from one peer that trigger an exponential lockout.</summary>
    public int LockoutAfterFailures { get; init; } = 5;

    /// <summary>First lockout duration; doubles with each further failure.</summary>
    public TimeSpan LockoutBase { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Ceiling for the exponential lockout.</summary>
    public TimeSpan LockoutMax { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long a peer's failure counter survives without a new failure.</summary>
    public TimeSpan FailureMemory { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Validates the policy.</summary>
    public void Validate()
    {
        if (PeerBurst < 1 || GlobalBurst < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(PeerBurst), "burst sizes must be at least 1");
        }

        if (PeerRefillPerMinute <= 0 || GlobalRefillPerMinute <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(PeerRefillPerMinute), "refill rates must be positive");
        }

        if (LockoutAfterFailures < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(LockoutAfterFailures));
        }

        if (LockoutBase <= TimeSpan.Zero || LockoutMax < LockoutBase)
        {
            throw new ArgumentOutOfRangeException(nameof(LockoutBase));
        }
    }
}

/// <summary>
/// Token bucket per peer plus a global bucket plus an exponential lockout, all sharing one
/// <see cref="TimeProvider"/> so behaviour is testable without sleeping.
/// </summary>
/// <remarks>
/// Three controls, because each covers a different attack: the per-peer bucket stops one host
/// hammering a password, the global bucket stops a botnet spreading the same attempt rate over many
/// source addresses, and the lockout turns a slow guess into an increasingly expensive one.
/// </remarks>
public sealed class AuthThrottle
{
    private readonly RateLimitOptions _options;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, PeerState> _peers = new(StringComparer.Ordinal);
    private readonly object _globalGate = new();

    private double _globalTokens;
    private long _globalLastRefillTicks;

    /// <summary>Creates a throttle.</summary>
    public AuthThrottle(RateLimitOptions options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _options = options;
        _time = timeProvider ?? TimeProvider.System;
        _globalTokens = options.GlobalBurst;
        _globalLastRefillTicks = _time.GetUtcNow().UtcTicks;
    }

    /// <summary>Number of peers currently tracked. Exposed for tests and diagnostics.</summary>
    public int TrackedPeers => _peers.Count;

    /// <summary>
    /// Takes one attempt token for <paramref name="peerKey"/>. Returns false when throttled or locked
    /// out, with <paramref name="retryAfter"/> set to the earliest plausible retry.
    /// </summary>
    public bool TryAcquire(string peerKey, out TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(peerKey);
        var now = _time.GetUtcNow();
        var state = _peers.GetOrAdd(peerKey, _ => new PeerState
        {
            Tokens = _options.PeerBurst,
            LastRefillTicks = now.UtcTicks,
            LastFailureTicks = 0,
            ConsecutiveFailures = 0,
        });

        lock (state)
        {
            if (state.LockedUntilTicks > now.UtcTicks)
            {
                retryAfter = new TimeSpan(state.LockedUntilTicks - now.UtcTicks);
                return false;
            }

            if (state.LastFailureTicks != 0 && now.UtcTicks - state.LastFailureTicks > _options.FailureMemory.Ticks)
            {
                // The peer has been quiet long enough that its history is no longer meaningful.
                state.ConsecutiveFailures = 0;
                state.LastFailureTicks = 0;
            }

            Refill(state, now, _options.PeerBurst, _options.PeerRefillPerMinute);

            if (state.Tokens < 1)
            {
                var missing = 1 - state.Tokens;
                retryAfter = TimeSpan.FromTicks((long)Math.Ceiling(missing * TicksPerToken(_options.PeerRefillPerMinute)));
                return false;
            }

            if (!TryTakeGlobal(now, out var globalRetry))
            {
                retryAfter = globalRetry;
                return false;
            }

            state.Tokens -= 1;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    /// <summary>Records a failed authentication for <paramref name="peerKey"/> and applies the lockout rule.</summary>
    public void RecordFailure(string peerKey)
    {
        ArgumentNullException.ThrowIfNull(peerKey);
        var now = _time.GetUtcNow();
        var state = _peers.GetOrAdd(peerKey, _ => new PeerState
        {
            Tokens = _options.PeerBurst,
            LastRefillTicks = now.UtcTicks,
        });

        lock (state)
        {
            state.LastFailureTicks = now.UtcTicks;
            state.ConsecutiveFailures++;

            if (state.ConsecutiveFailures >= _options.LockoutAfterFailures)
            {
                var over = state.ConsecutiveFailures - _options.LockoutAfterFailures;
                var factor = Math.Pow(2, Math.Min(over, 20));
                var duration = TimeSpan.FromTicks((long)Math.Min(
                    _options.LockoutBase.Ticks * factor,
                    _options.LockoutMax.Ticks));
                state.LockedUntilTicks = now.UtcTicks + duration.Ticks;
            }
        }
    }

    /// <summary>Clears a peer's failure history after a successful authentication.</summary>
    public void RecordSuccess(string peerKey)
    {
        ArgumentNullException.ThrowIfNull(peerKey);
        if (_peers.TryGetValue(peerKey, out var state))
        {
            lock (state)
            {
                state.ConsecutiveFailures = 0;
                state.LastFailureTicks = 0;
                state.LockedUntilTicks = 0;
            }
        }
    }

    /// <summary>Closes the outcome of one attempt: success resets, failure arms the lockout.</summary>
    public void RecordOutcome(string peerKey, bool success)
    {
        if (success)
        {
            RecordSuccess(peerKey);
        }
        else
        {
            RecordFailure(peerKey);
        }
    }

    /// <summary>Consecutive failures recorded for a peer.</summary>
    public int ConsecutiveFailures(string peerKey) =>
        _peers.TryGetValue(peerKey, out var state) ? Volatile.Read(ref state.ConsecutiveFailures) : 0;

    private static long TicksPerToken(double refillPerMinute) =>
        (long)Math.Ceiling(TimeSpan.TicksPerMinute / refillPerMinute);

    private static void Refill(PeerState state, DateTimeOffset now, double burst, double refillPerMinute)
    {
        var elapsed = now.UtcTicks - state.LastRefillTicks;
        if (elapsed <= 0)
        {
            return;
        }

        var rate = refillPerMinute;
        state.Tokens = Math.Min(burst, state.Tokens + (elapsed / (double)TimeSpan.TicksPerMinute * rate));
        state.LastRefillTicks = now.UtcTicks;
    }

    private bool TryTakeGlobal(DateTimeOffset now, out TimeSpan retryAfter)
    {
        lock (_globalGate)
        {
            var elapsed = now.UtcTicks - _globalLastRefillTicks;
            if (elapsed > 0)
            {
                _globalTokens = Math.Min(
                    _options.GlobalBurst,
                    _globalTokens + (elapsed / (double)TimeSpan.TicksPerMinute * _options.GlobalRefillPerMinute));
                _globalLastRefillTicks = now.UtcTicks;
            }

            if (_globalTokens < 1)
            {
                var missing = 1 - _globalTokens;
                retryAfter = TimeSpan.FromTicks((long)Math.Ceiling(missing * TicksPerToken(_options.GlobalRefillPerMinute)));
                return false;
            }

            _globalTokens -= 1;
            retryAfter = TimeSpan.Zero;
            return true;
        }
    }

    private sealed class PeerState
    {
        public double Tokens;
        public long LastRefillTicks;
        public long LockedUntilTicks;
        public long LastFailureTicks;
        public int ConsecutiveFailures;
    }
}
