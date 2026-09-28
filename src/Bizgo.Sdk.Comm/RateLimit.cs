using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Bizgo
{
    /// <summary>
    /// Client-side rate limits (SDK-DESIGN §11.2). Each client has two token buckets:
    /// <list type="bullet">
    /// <item><description><b>send</b>: operations marked <c>x-sdk-rate: send</c> in the spec (<c>sendOmni</c> and every <c>client.Send</c> method,
    /// <c>createReservation</c>, <c>addReservationRecipients</c>, <c>createBrandMessageGroupSend</c>, <c>sendCounselPlain</c>,
    /// <c>sendCounselRich</c>). One token per destination (minimum 1), default 200 per second.</description></item>
    /// <item><description><b>other</b>: every other API, one token per request, default 5 per second.</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>Buckets start full (capacity = per-second rate, like the server's token bucket). Every HTTP attempt, retries included, waits
    /// (without blocking a thread) until its tokens are available. A request that costs more than the capacity waits for a full bucket and
    /// leaves the rest as debt, so it never deadlocks and the average rate holds.</para>
    /// <para>The Bizgo limit is per account. These buckets only pace <b>one client in one process</b>: several processes or servers that
    /// share a key need their own coordination. HTTP 429 is still retried.</para>
    /// </remarks>
    public sealed class BizgoRateLimit
    {
        /// <summary>Messages (destinations) per second for send operations. Default 200.</summary>
        public double SendPerSecond { get; set; } = 200;

        /// <summary>Requests per second for every other operation. Default 5.</summary>
        public double OtherPerSecond { get; set; } = 5;

        /// <inheritdoc/>
        public override string ToString() => "BizgoRateLimit(Send=" + SendPerSecond.ToString(CultureInfo.InvariantCulture) + "/s, Other=" + OtherPerSecond.ToString(CultureInfo.InvariantCulture) + "/s)";

        internal void Check()
        {
            if (!(SendPerSecond > 0) || double.IsInfinity(SendPerSecond) || !(OtherPerSecond > 0) || double.IsInfinity(OtherPerSecond))
            {
                throw new BizgoConfigurationException("RateLimit의 SendPerSecond/OtherPerSecond는 0보다 큰 유한한 수여야 합니다. 끄려면 RateLimit = null로 두세요.");
            }
        }
    }
}

namespace Bizgo.Internal
{
    /// <summary>A token bucket: holds up to <c>rate</c> tokens, refilled continuously. Thread-safe; callers reserve in call order.</summary>
    internal sealed class TokenBucket
    {
        private readonly object _gate = new object();
        private readonly Func<TimeSpan> _clock;
        private double _tokens;
        private TimeSpan _updated;

        internal TokenBucket(double rate, Func<TimeSpan> clock)
        {
            Rate = rate;
            _clock = clock;
            _tokens = rate;
            _updated = clock();
        }

        internal double Rate { get; }

        internal double Capacity => Rate;

        /// <summary>Takes <paramref name="cost"/> tokens and returns how long the caller must wait before sending.</summary>
        internal TimeSpan Reserve(double cost)
        {
            cost = Math.Max(cost, 1.0);
            lock (_gate)
            {
                var now = _clock();
                _tokens = Math.Min(Capacity, _tokens + ((now - _updated).TotalSeconds * Rate));
                _updated = now;
                var need = Math.Min(cost, Capacity);
                var wait = Math.Max(0.0, (need - _tokens) / Rate);
                _tokens -= cost;
                return TimeSpan.FromSeconds(wait);
            }
        }
    }

    /// <summary>The two buckets of one client.</summary>
    internal sealed class RateLimiter
    {
        private static readonly Stopwatch Monotonic = Stopwatch.StartNew();

        internal RateLimiter(BizgoRateLimit limits, Func<TimeSpan>? clock = null)
        {
            var now = clock ?? (() => Monotonic.Elapsed);
            Send = new TokenBucket(limits.SendPerSecond, now);
            Other = new TokenBucket(limits.OtherPerSecond, now);
        }

        internal TokenBucket Send { get; }

        internal TokenBucket Other { get; }

        /// <summary>Waits by the clock. Replaced in tests.</summary>
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (delay, ct) => Task.Delay(delay, ct);

        internal Task WaitAsync(BizgoRateBucket bucket, int cost, CancellationToken cancellationToken)
        {
            var wait = (bucket == BizgoRateBucket.Send ? Send : Other).Reserve(cost);
            return wait > TimeSpan.Zero ? Delay(wait, cancellationToken) : Task.CompletedTask;
        }
    }
}
