using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bizgo.Internal;
using Bizgo.Models;

namespace Bizgo
{
    public sealed partial class SendResource
    {
        /// <summary>Largest chunk (recipients per request).</summary>
        public const int MaxChunkSize = 200;

        private const int MaxIdempotencyKeyLength = 200;

        /// <summary>
        /// Sends one message to any number of recipients, in chunks of up to 200 (<see cref="OmniAsync"/> per chunk), with at most
        /// <see cref="BulkSendOptions.Concurrency"/> requests in flight, paced by the client's send rate limit (one token per recipient).
        /// </summary>
        /// <remarks>
        /// <para>Every chunk is validated before the first one is sent: if one is invalid nothing is sent.</para>
        /// <para>A chunk that fails for any reason (API error, network error, unreadable response, cancellation) does not stop the others and
        /// never loses the results already received: see <see cref="BulkSendResult.Errors"/> (chunk number and recipient index range, never the
        /// numbers), <see cref="BulkSendResult.Failed"/> (rejected recipients) and <see cref="BulkSendResult.Duplicates"/> (recipients already
        /// accepted with the same idempotency key, code <c>A301</c>; a chunk whose recipients are all duplicates is not an error).</para>
        /// <para>With <see cref="BulkSendOptions.IdempotencyKeyPrefix"/> each chunk is sent with the key
        /// <c>&lt;prefix&gt;-&lt;chunkSize&gt;-&lt;startIndex&gt;-&lt;hash8&gt;</c> (hash8: first 8 hex digits of SHA-256 over the chunk's numbers
        /// joined by <c>\n</c>). Re-run with the <b>same list and the same chunk size</b> (within the key TTL) and chunks that were already
        /// accepted are not delivered twice: their recipients come back in <see cref="BulkSendResult.Duplicates"/>; to resend only failed chunks use the chunk numbers in
        /// <see cref="BulkSendResult.Errors"/>.</para>
        /// </remarks>
        /// <param name="to">Recipients (no limit).</param>
        /// <param name="messages">Channel messages in fallback order, as for <see cref="OmniAsync"/>.</param>
        /// <param name="options">Chunk size, concurrency, idempotency key prefix and the request fields shared by every chunk.</param>
        /// <param name="cancellationToken">Cancellation: chunks not yet sent are reported as errors; accepted results are kept.</param>
        /// <returns>The results of every chunk.</returns>
        /// <exception cref="BizgoValidationException">An argument or a chunk is invalid (nothing was sent).</exception>
        public async Task<BulkSendResult> BulkAsync(Recipients to, IEnumerable<IChannelMessage> messages, BulkSendOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (to is null)
            {
                throw new ArgumentNullException(nameof(to));
            }

            if (messages is null)
            {
                throw new ArgumentNullException(nameof(messages));
            }

            options ??= new BulkSendOptions();
            var chunks = BuildChunks(to.Destinations, messages.ToList(), options);
            var outcomes = new BulkOutcome[chunks.Count];
            using var gate = new SemaphoreSlim(options.Concurrency, options.Concurrency);
            var tasks = new List<Task>(chunks.Count);
            foreach (var chunk in chunks)
            {
                tasks.Add(RunChunkAsync(chunk, gate, outcomes, cancellationToken));
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
            return new BulkSendResult(outcomes);
        }

        /// <summary>The idempotency key of one chunk: <c>&lt;prefix&gt;-&lt;chunkSize&gt;-&lt;startIndex&gt;-&lt;hash8&gt;</c> (SDK-DESIGN §12.3).</summary>
        /// <param name="prefix">Your prefix.</param>
        /// <param name="chunkSize">Chunk size of the run.</param>
        /// <param name="startIndex">Index of the chunk's first recipient in the full list.</param>
        /// <param name="numbers">The chunk's phone numbers, in order.</param>
        /// <returns>The key.</returns>
        public static string BulkIdempotencyKey(string prefix, int chunkSize, int startIndex, IEnumerable<string> numbers)
        {
            if (prefix is null)
            {
                throw new ArgumentNullException(nameof(prefix));
            }

            if (numbers is null)
            {
                throw new ArgumentNullException(nameof(numbers));
            }

            var bytes = Encoding.UTF8.GetBytes(string.Join("\n", numbers));
#if NET5_0_OR_GREATER
            var digest = SHA256.HashData(bytes);
#else
            byte[] digest;
            using (var sha = SHA256.Create())
            {
                digest = sha.ComputeHash(bytes);
            }
#endif

            return prefix + "-" + chunkSize.ToString(CultureInfo.InvariantCulture) + "-" + startIndex.ToString(CultureInfo.InvariantCulture) + "-" + CryptoHelpers.ToHexLower(digest).Substring(0, 8);
        }

        private static List<BulkChunk> BuildChunks(IReadOnlyList<Destination> recipients, List<IChannelMessage> messages, BulkSendOptions options)
        {
            if (options.ChunkSize < 1 || options.ChunkSize > MaxChunkSize)
            {
                throw new BizgoValidationException("chunkSize", "1~200이어야 합니다");
            }

            if (options.Concurrency < 1)
            {
                throw new BizgoValidationException("concurrency", "1 이상이어야 합니다");
            }

            if (options.IdempotencyKeyPrefix is not null && options.IdempotencyKeyPrefix.Length == 0)
            {
                throw new BizgoValidationException("idempotencyKeyPrefix", "비어 있지 않은 문자열이어야 합니다");
            }

            if (recipients.Count == 0)
            {
                throw new BizgoValidationException("to", "수신자가 없습니다");
            }

            var chunks = new List<BulkChunk>();
            var errors = new List<BizgoValidationError>();
            for (int index = 0, start = 0; start < recipients.Count; index++, start += options.ChunkSize)
            {
                var count = Math.Min(options.ChunkSize, recipients.Count - start);
                var destinations = recipients.Skip(start).Take(count).ToList();
                string? key = null;
                if (options.IdempotencyKeyPrefix is not null)
                {
                    key = BulkIdempotencyKey(options.IdempotencyKeyPrefix, options.ChunkSize, start, destinations.Select(d => d?.To ?? string.Empty));
                    if (key.Length > MaxIdempotencyKeyLength)
                    {
                        throw new BizgoValidationException("idempotencyKeyPrefix", "너무 깁니다(생성된 멱등성 키가 200자를 넘음)");
                    }
                }

                SendOmniRequest request;
                try
                {
                    request = new SendOmniRequest
                    {
                        Destinations = destinations,
                        MessageFlow = messages.Select(MessageFlowItem.Of).ToList(),
                        Ref = options.Ref,
                        GroupKey = options.GroupKey,
                        PaymentCode = options.PaymentCode,
                        IdempotencyKey = key,
                        IdempotencyTtl = options.IdempotencyTtl,
                    };
                }
                catch (ArgumentException)
                {
                    throw new BizgoValidationException("messages", "지원하지 않는 채널 메시지이거나 null 항목이 있습니다");
                }

                var validator = new RequestValidator();
                request.ValidateInto(validator, string.Empty);
                foreach (var error in validator.Errors)
                {
                    var shifted = Shift(error.Path, start);
                    if (!errors.Any(e => e.Path == shifted && e.Reason == error.Reason))
                    {
                        errors.Add(new BizgoValidationError(shifted, error.Reason));
                    }
                }

                chunks.Add(new BulkChunk(index, start, count, request));
            }

            if (errors.Count > 0)
            {
                throw new BizgoValidationException(errors);
            }

            return chunks;
        }

        /// <summary><c>destinations[3].to</c> in a chunk starting at 200 becomes <c>destinations[203].to</c> (an index in your list).</summary>
        private static string Shift(string path, int start)
        {
            const string Prefix = "destinations[";
            if (!path.StartsWith(Prefix, StringComparison.Ordinal))
            {
                return path;
            }

            var end = path.IndexOf(']', Prefix.Length);
            if (end <= Prefix.Length)
            {
                return path;
            }

            var index = 0;
            for (var i = Prefix.Length; i < end; i++)
            {
                if (path[i] < '0' || path[i] > '9')
                {
                    return path;
                }

                index = (index * 10) + (path[i] - '0');
            }

            return Prefix + (index + start).ToString(CultureInfo.InvariantCulture) + path.Remove(0, end);
        }

        private async Task RunChunkAsync(BulkChunk chunk, SemaphoreSlim gate, BulkOutcome[] outcomes, CancellationToken cancellationToken)
        {
            try
            {
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException e)
            {
                outcomes[chunk.Index] = new BulkOutcome(chunk, null, e);
                return;
            }

            try
            {
                var result = await RequestAsync(chunk.Request, cancellationToken).ConfigureAwait(false);
                outcomes[chunk.Index] = new BulkOutcome(chunk, result, null);
            }
#pragma warning disable CA1031 // every per-chunk failure is reported; it must not lose the other chunks (SDK-DESIGN §12.2)
            catch (Exception e)
#pragma warning restore CA1031
            {
                outcomes[chunk.Index] = new BulkOutcome(chunk, null, e);
            }
            finally
            {
                gate.Release();
            }
        }

        internal sealed class BulkChunk
        {
            internal BulkChunk(int index, int start, int count, SendOmniRequest request)
            {
                Index = index;
                Start = start;
                Count = count;
                Request = request;
            }

            internal int Index { get; }

            internal int Start { get; }

            internal int Count { get; }

            internal SendOmniRequest Request { get; }
        }

        internal sealed class BulkOutcome
        {
            internal BulkOutcome(BulkChunk chunk, SendResult? result, Exception? error)
            {
                Chunk = chunk;
                Result = result;
                Error = error;
            }

            internal BulkChunk Chunk { get; }

            internal SendResult? Result { get; }

            internal Exception? Error { get; }
        }
    }

    /// <summary>Options of <see cref="SendResource.BulkAsync"/>.</summary>
    public sealed class BulkSendOptions
    {
        /// <summary>Recipients per request, 1-200. Default 200.</summary>
        public int ChunkSize { get; set; } = SendResource.MaxChunkSize;

        /// <summary>Requests in flight at once. Default 4.</summary>
        public int Concurrency { get; set; } = 4;

        /// <summary>
        /// When set, each chunk gets the idempotency key <c>&lt;prefix&gt;-&lt;chunkSize&gt;-&lt;startIndex&gt;-&lt;hash8&gt;</c>, which also makes
        /// timeouts and 5xx retryable. Re-run with the same list and chunk size to skip chunks already accepted. The key must fit in 200 characters.
        /// </summary>
        public string? IdempotencyKeyPrefix { get; set; }

        /// <summary>Seconds (0-86400) the idempotency keys are remembered. Null with <see cref="IdempotencyKeyPrefix"/> set sends <see cref="BizgoClient.DefaultIdempotencyTtl"/> (86400); 0 is sent as is.</summary>
        public int? IdempotencyTtl { get; set; }

        /// <summary>Your reference value, the same for every chunk.</summary>
        public string? Ref { get; set; }

        /// <summary>Message group key, the same for every chunk.</summary>
        public string? GroupKey { get; set; }

        /// <summary>Department code for billing, the same for every chunk.</summary>
        public string? PaymentCode { get; set; }

        /// <inheritdoc/>
        public override string ToString() => "BulkSendOptions(ChunkSize=" + ChunkSize.ToString(CultureInfo.InvariantCulture) + ", Concurrency=" + Concurrency.ToString(CultureInfo.InvariantCulture) + ", IdempotencyKeyPrefix=" + (IdempotencyKeyPrefix is null ? "-" : "set") + ")";
    }

    /// <summary>One chunk of a bulk send that did not get a result.</summary>
    public sealed class BulkChunkError
    {
        internal BulkChunkError(int chunkIndex, int start, int count, Exception error)
        {
            ChunkIndex = chunkIndex;
            Start = start;
            Count = count;
            Error = error;
        }

        /// <summary>Chunk number, starting at 0.</summary>
        public int ChunkIndex { get; }

        /// <summary>Index of the chunk's first recipient in your list.</summary>
        public int Start { get; }

        /// <summary>Number of recipients in the chunk (indexes <see cref="Start"/> to <see cref="Start"/> + <see cref="Count"/> - 1).</summary>
        public int Count { get; }

        /// <summary>
        /// Why the chunk failed. After a <see cref="BizgoConnectionException"/> or <see cref="BizgoInvalidResponseException"/> the chunk may
        /// still have been accepted: check with the status APIs before resending without an idempotency key.
        /// </summary>
        public Exception Error { get; }

        /// <inheritdoc/>
        public override string ToString() => "BulkChunkError(Chunk=" + ChunkIndex.ToString(CultureInfo.InvariantCulture) + ", Recipients=" + Start.ToString(CultureInfo.InvariantCulture) + ".." + (Start + Count - 1).ToString(CultureInfo.InvariantCulture) + ", Error=" + Error.GetType().Name + ")";
    }

    /// <summary>Result of <see cref="SendResource.BulkAsync"/>. Check <see cref="Errors"/> (failed chunks) and <see cref="Failed"/> (rejected recipients); <see cref="Duplicates"/> were already accepted earlier.</summary>
    public sealed class BulkSendResult
    {
        internal BulkSendResult(IReadOnlyList<SendResource.BulkOutcome> outcomes)
        {
            Results = outcomes.Select(o => o.Result).ToArray();
            Errors = outcomes.Where(o => o.Error is not null).Select(o => new BulkChunkError(o.Chunk.Index, o.Chunk.Start, o.Chunk.Count, o.Error!)).ToArray();
        }

        /// <summary>Per chunk, in order: the result, or null when the chunk failed (see <see cref="Errors"/>).</summary>
        public IReadOnlyList<SendResult?> Results { get; }

        /// <summary>Chunks without a result.</summary>
        public IReadOnlyList<BulkChunkError> Errors { get; }

        /// <summary>Recipients accepted with <c>A000</c>, across every chunk.</summary>
        public IReadOnlyList<SendDestinationResult> Succeeded => Results.Where(r => r is not null).SelectMany(r => r!.Succeeded).ToArray();

        /// <summary>Recipients rejected at acceptance, across the chunks that were answered (failed chunks are in <see cref="Errors"/>).</summary>
        public IReadOnlyList<SendDestinationResult> Failed => Results.Where(r => r is not null).SelectMany(r => r!.Failed).ToArray();

        /// <summary>
        /// Recipients already accepted by an earlier request with the same idempotency key (code <c>A301</c>), across the chunks that were answered.
        /// Not in <see cref="Succeeded"/> or <see cref="Failed"/>.
        /// </summary>
        public IReadOnlyList<SendDestinationResult> Duplicates => Results.Where(r => r is not null).SelectMany(r => r!.Duplicates).ToArray();

        /// <summary>Message keys of every accepted recipient.</summary>
        public IReadOnlyList<string> MsgKeys => Results.Where(r => r is not null).SelectMany(r => r!.MsgKeys).ToArray();

        /// <summary>True when every chunk was answered and no recipient was rejected (duplicates do not count as rejections).</summary>
        public bool IsOk => Errors.Count == 0 && Failed.Count == 0;

        /// <inheritdoc/>
        public override string ToString() => "BulkSendResult(Chunks=" + Results.Count.ToString(CultureInfo.InvariantCulture) + ", Errors=" + Errors.Count.ToString(CultureInfo.InvariantCulture) + ", Succeeded=" + Succeeded.Count.ToString(CultureInfo.InvariantCulture) + ", Failed=" + Failed.Count.ToString(CultureInfo.InvariantCulture) + ", Duplicates=" + Duplicates.Count.ToString(CultureInfo.InvariantCulture) + ")";
    }
}
