using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bizgo.Internal;
using Bizgo.Models;

namespace Bizgo
{
    /// <summary>Message sending (<c>POST /api/comm/v1/send/omni</c>). Access as <c>client.Send</c>.</summary>
    public sealed partial class SendResource
    {
        internal const string Path = "/api/comm/v1/send/omni";

        private readonly Transport _transport;

        internal SendResource(Transport transport)
        {
            _transport = transport;
        }

        /// <summary>Sends one message, with optional fallback channels, to up to 200 recipients.</summary>
        /// <param name="to">Recipient phone number(s) or <see cref="Destination"/> objects (for <c>ReplaceWords</c> substitution or a per-recipient <c>Ref</c>). Max 200.</param>
        /// <param name="messages">
        /// Channel messages in fallback order: if the first fails, Bizgo sends the next. Example:
        /// <c>new IChannelMessage[] { new AlimtalkMessage { ... }, new SmsMessage { ... } }</c>.
        /// </param>
        /// <param name="options"><c>Ref</c>, <c>GroupKey</c>, <c>PaymentCode</c>, <c>IdempotencyKey</c>, <c>IdempotencyTtl</c>.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>Acceptance result. Check <see cref="SendResult.Failed"/> for rejected recipients (<see cref="SendResult.Duplicates"/>: already accepted with the same idempotency key); final delivery results arrive later as reports.</returns>
        /// <exception cref="BizgoValidationException">The request is invalid (checked before anything is sent).</exception>
        /// <exception cref="BizgoApiException">Bizgo rejected the request.</exception>
        /// <exception cref="BizgoConnectionException">Network failure; the message may or may not have been accepted.</exception>
        public Task<SendResult> OmniAsync(Recipients to, IEnumerable<IChannelMessage> messages, SendOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (to is null)
            {
                throw new ArgumentNullException(nameof(to));
            }

            if (messages is null)
            {
                throw new ArgumentNullException(nameof(messages));
            }

            var request = new SendOmniRequest
            {
                Destinations = to.Destinations.ToList(),
                MessageFlow = messages.Select(MessageFlowItem.Of).ToList(),
                Ref = options?.Ref,
                GroupKey = options?.GroupKey,
                PaymentCode = options?.PaymentCode,
                IdempotencyKey = options?.IdempotencyKey,
                IdempotencyTtl = options?.IdempotencyTtl,
            };
            return RequestAsync(request, cancellationToken);
        }

        /// <summary>Sends a prepared request (for example one read with <see cref="SendOmniRequest.FromJson(string)"/>).</summary>
        /// <remarks>When <c>IdempotencyKey</c> is set and <c>IdempotencyTtl</c> is null, <see cref="BizgoClient.DefaultIdempotencyTtl"/> is sent; <paramref name="request"/> is not modified.</remarks>
        /// <param name="request">The request body.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>Acceptance result.</returns>
        /// <exception cref="BizgoValidationException">The request is invalid.</exception>
        /// <exception cref="BizgoApiException">Bizgo rejected the request.</exception>
        /// <exception cref="BizgoConnectionException">Network failure; the message may or may not have been accepted.</exception>
        public async Task<SendResult> RequestAsync(SendOmniRequest request, CancellationToken cancellationToken = default)
        {
            if (request is null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            request.Validate();

            // Without an idempotency key a retried send after a timeout could deliver twice.
            var retry = string.IsNullOrEmpty(request.IdempotencyKey) ? RetryPolicy.RateLimitOnly : RetryPolicy.Safe;
            // A key without a TTL gets BizgoClient.DefaultIdempotencyTtl in the body (A309 otherwise); the request object is not changed.
            var body = JsonBody.Create(request, BizgoJsonContext.Default.SendOmniRequest, request.IdempotencyKey, request.IdempotencyTtl);
            var response = await _transport.SendAsync(BizgoOperation.SendOmni, Path, null, body, retry, RateCost.Destinations(request.Destinations?.Count), null, BizgoJsonContext.Default.SendOmniResponse, cancellationToken).ConfigureAwait(false);
            var destinations = response.Data?.Data?.Destinations?.Where(d => d is not null).ToArray() ?? Array.Empty<SendDestinationResult>();
            return new SendResult(destinations, response.Data?.Ref, response.Common?.InfobankTrId);
        }

        /// <summary>Sends an SMS: text up to 90 bytes in EUC-KR (about 45 Korean characters).</summary>
        /// <param name="to">Recipient(s).</param>
        /// <param name="from">Sender number registered in the Bizgo console.</param>
        /// <param name="text">Message text.</param>
        /// <param name="options">Optional request fields (<c>Ref</c>, <c>IdempotencyKey</c>, ...).</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>Acceptance result.</returns>
        public Task<SendResult> SmsAsync(Recipients to, string from, string text, SendOptions? options = null, CancellationToken cancellationToken = default)
        {
            return OmniAsync(to, new IChannelMessage[] { new SmsMessage { From = from, Text = text } }, options, cancellationToken);
        }

        /// <summary>Sends an LMS (text up to 2,000 bytes in EUC-KR).</summary>
        /// <param name="to">Recipient(s).</param>
        /// <param name="from">Sender number registered in the Bizgo console.</param>
        /// <param name="text">Message text.</param>
        /// <param name="title">Optional title.</param>
        /// <param name="options">Optional request fields.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>Acceptance result.</returns>
        public Task<SendResult> LmsAsync(Recipients to, string from, string text, string? title = null, SendOptions? options = null, CancellationToken cancellationToken = default)
        {
            return OmniAsync(to, new IChannelMessage[] { new MmsMessage { From = from, Text = text, Title = title } }, options, cancellationToken);
        }

        /// <summary>Sends an MMS. Get <paramref name="fileKeys"/> (max 3) from <c>client.Files.UploadMmsAsync</c> first.</summary>
        /// <param name="to">Recipient(s).</param>
        /// <param name="from">Sender number registered in the Bizgo console.</param>
        /// <param name="text">Message text.</param>
        /// <param name="fileKeys">Uploaded image keys (1-3).</param>
        /// <param name="title">Optional title.</param>
        /// <param name="options">Optional request fields.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>Acceptance result.</returns>
        public Task<SendResult> MmsAsync(Recipients to, string from, string text, IEnumerable<string> fileKeys, string? title = null, SendOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (fileKeys is null)
            {
                throw new ArgumentNullException(nameof(fileKeys));
            }

            var message = new MmsMessage { From = from, Text = text, Title = title, FileKey = fileKeys.ToList() };
            return OmniAsync(to, new IChannelMessage[] { message }, options, cancellationToken);
        }

        /// <inheritdoc/>
        public override string ToString() => "SendResource";
    }
}
