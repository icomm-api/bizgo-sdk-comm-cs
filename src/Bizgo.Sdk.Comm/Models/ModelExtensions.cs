using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Bizgo.Internal;

namespace Bizgo.Models
{
    /// <summary>
    /// A channel message: <see cref="SmsMessage"/>, <see cref="MmsMessage"/> (LMS/MMS), <see cref="InternationalMessage"/>,
    /// <see cref="RcsMessage"/>, <see cref="AlimtalkMessage"/>, <see cref="BrandMessage"/> or <see cref="NaverTalkMessage"/>.
    /// The SDK wraps it into its <c>messageFlow</c> item (<c>{"sms": {...}}</c>) automatically.
    /// </summary>
    /// <remarks>Only the SDK's model classes implement this interface; other implementations are rejected.</remarks>
    public interface IChannelMessage
    {
        /// <summary><c>messageFlow</c> channel key (<c>sms</c>, <c>mms</c>, <c>alimtalk</c>, ...).</summary>
        string ChannelKey { get; }
    }

    public sealed partial class Destination
    {
        /// <summary>Creates an empty recipient.</summary>
        public Destination()
        {
        }

        /// <summary>Creates a recipient.</summary>
        /// <param name="to">Phone number (for example <c>01000000000</c>).</param>
        public Destination(string to)
        {
            To = to;
        }
    }

    public sealed partial class SendOmniRequest
    {
        /// <summary>
        /// Reads a request body written with API field names (for example from a queue). Unknown fields are rejected so that typos
        /// (<c>templatecode</c>) fail fast; the result is also validated.
        /// </summary>
        /// <param name="json">Request JSON.</param>
        /// <returns>The request.</returns>
        /// <exception cref="BizgoValidationException">The JSON is malformed, has unknown fields or breaks a constraint. The message never contains values.</exception>
        public static SendOmniRequest FromJson(string json)
        {
            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            SendOmniRequest? request;
            try
            {
                request = JsonSerializer.Deserialize(json, BizgoJsonContext.Default.SendOmniRequest);
            }
            catch (JsonException e)
            {
                // e.Path is a JSON path such as $.messageFlow[0].alimtalk; the exception message is not used (it can quote input)
                throw new BizgoValidationException(e.Path ?? string.Empty, "JSON을 요청 모델로 읽을 수 없습니다(형식이 틀렸거나 모델에 없는 필드)");
            }

            if (request is null)
            {
                throw new BizgoValidationException(string.Empty, "요청 본문이 null입니다");
            }

            request.Validate();
            return request;
        }

        /// <summary>Serializes with API field names. Only properties you set are written.</summary>
        /// <returns>JSON text.</returns>
        public string ToJson() => JsonSerializer.Serialize(this, BizgoJsonContext.Default.SendOmniRequest);
    }

    /// <summary>
    /// Acceptance result of a send request. Acceptance is not delivery: the final result arrives later as a report
    /// (polling, webhook or inquiry). Always check <see cref="Failed"/>: a request can succeed while some recipients were rejected.
    /// Recipients already accepted earlier with the same <c>idempotencyKey</c> (per-recipient code <c>A301</c>) are in
    /// <see cref="Duplicates"/>, not in <see cref="Failed"/> or <see cref="Succeeded"/>.
    /// </summary>
    public sealed class SendResult
    {
        /// <summary>Creates a result.</summary>
        /// <param name="destinations">Per-recipient results.</param>
        /// <param name="ref">The request <c>ref</c>.</param>
        /// <param name="trackingId"><c>common.infobankTrId</c>.</param>
        public SendResult(IReadOnlyList<SendDestinationResult> destinations, string? @ref, string? trackingId)
        {
            Destinations = destinations ?? Array.Empty<SendDestinationResult>();
            Ref = @ref;
            TrackingId = trackingId;
        }

        /// <summary>Per-recipient acceptance results, in request order.</summary>
        public IReadOnlyList<SendDestinationResult> Destinations { get; }

        /// <summary>The <c>ref</c> you sent with the request.</summary>
        public string? Ref { get; }

        /// <summary><c>common.infobankTrId</c> for support inquiries.</summary>
        public string? TrackingId { get; }

        /// <summary>Recipients accepted with code <c>A000</c>.</summary>
        public IReadOnlyList<SendDestinationResult> Succeeded => Destinations.Where(d => d.Code == "A000").ToArray();

        /// <summary>
        /// Recipients rejected at acceptance (code other than <c>A000</c> and <c>A301</c>). They will not receive the message.
        /// Retrying with the same <c>idempotencyKey</c> is safe: recipients that were already accepted come back in <see cref="Duplicates"/>.
        /// </summary>
        public IReadOnlyList<SendDestinationResult> Failed => Destinations.Where(d => d.Code != "A000" && d.Code != DuplicateCode).ToArray();

        /// <summary>
        /// Recipients with code <c>A301</c>: already accepted by an earlier request with the same <c>idempotencyKey</c> (within its TTL).
        /// They were not accepted again by this request (not in <see cref="Succeeded"/>, no new <c>msgKey</c>) and are not rejections.
        /// Look them up with the message keys from the earlier request.
        /// </summary>
        public IReadOnlyList<SendDestinationResult> Duplicates => Destinations.Where(d => d.Code == DuplicateCode).ToArray();

        /// <summary>Message keys of accepted recipients, for report and status lookups.</summary>
        public IReadOnlyList<string> MsgKeys => Succeeded.Where(d => !string.IsNullOrEmpty(d.MsgKey)).Select(d => d.MsgKey!).ToArray();

        /// <inheritdoc/>
        public override string ToString() => "SendResult(Succeeded=" + Succeeded.Count + ", Failed=" + Failed.Count + ", Duplicates=" + Duplicates.Count + ")";

        internal const string DuplicateCode = "A301";
    }

    /// <summary>One batch from report polling. Call <c>client.Reports.AckAsync(batch.ReportId)</c> after you stored it.</summary>
    public sealed class ReportBatch
    {
        /// <summary>Creates a batch.</summary>
        /// <param name="reportId">Report id to acknowledge.</param>
        /// <param name="reports">Reports.</param>
        public ReportBatch(string reportId, IReadOnlyList<Report> reports)
        {
            ReportId = reportId ?? string.Empty;
            Reports = reports ?? Array.Empty<Report>();
        }

        /// <summary>Id for <c>AckAsync</c>. Empty when there are no reports.</summary>
        public string ReportId { get; }

        /// <summary>Reports in this batch.</summary>
        public IReadOnlyList<Report> Reports { get; }

        /// <summary>True when there was nothing new.</summary>
        public bool IsEmpty => Reports.Count == 0;
    }

    /// <summary>One page of send history.</summary>
    public sealed class MessagePage
    {
        /// <summary>Creates a page.</summary>
        /// <param name="messages">Messages.</param>
        /// <param name="lastSeq">Cursor for the next page.</param>
        /// <param name="hasNext">Whether there is a next page.</param>
        public MessagePage(IReadOnlyList<MessageStatus> messages, long? lastSeq, bool hasNext)
        {
            Messages = messages ?? Array.Empty<MessageStatus>();
            LastSeq = lastSeq;
            HasNext = hasNext;
        }

        /// <summary>Messages on this page.</summary>
        public IReadOnlyList<MessageStatus> Messages { get; }

        /// <summary>Cursor for the next page (<c>lastSeq</c>).</summary>
        public long? LastSeq { get; }

        /// <summary>Whether there is a next page.</summary>
        public bool HasNext { get; }
    }

    /// <summary>One page of MO (inbound) history.</summary>
    public sealed class MoPage
    {
        /// <summary>Creates a page.</summary>
        /// <param name="messages">Messages.</param>
        /// <param name="lastSeq">Cursor for the next page.</param>
        /// <param name="hasNext">Whether there is a next page.</param>
        public MoPage(IReadOnlyList<MoMessage> messages, long? lastSeq, bool hasNext)
        {
            Messages = messages ?? Array.Empty<MoMessage>();
            LastSeq = lastSeq;
            HasNext = hasNext;
        }

        /// <summary>Messages on this page.</summary>
        public IReadOnlyList<MoMessage> Messages { get; }

        /// <summary>Cursor for the next page (<c>lastSeq</c>).</summary>
        public long? LastSeq { get; }

        /// <summary>Whether there is a next page.</summary>
        public bool HasNext { get; }
    }
}
