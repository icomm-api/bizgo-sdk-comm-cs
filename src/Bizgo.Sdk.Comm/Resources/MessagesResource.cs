using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Bizgo.Internal;
using Bizgo.Models;

namespace Bizgo
{
    /// <summary>
    /// Message status, history and statistics (<c>/api/comm/v1/message/*</c>). Access as <c>client.Messages</c>.
    /// These are non-send APIs, limited to 5 requests per second by default.
    /// </summary>
    public sealed partial class MessagesResource
    {
        private readonly Transport _transport;

        internal MessagesResource(Transport transport)
        {
            _transport = transport;
        }

        /// <summary>Acceptance/send/report status of one message. With fallback there is one entry per channel tried.</summary>
        /// <param name="msgKey">Message key.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>Status entries.</returns>
        public async Task<IReadOnlyList<MessageStatus>> StatusAsync(string msgKey, CancellationToken cancellationToken = default)
        {
            var path = "/api/comm/v1/message/inquiry/msgKey/" + PathSegment.Escape(msgKey, "msgKey");
            return Statuses(await Get(BizgoOperation.GetMessageStatusByMsgKey, path, null, BizgoJsonContext.Default.MessageStatusListResponse, cancellationToken).ConfigureAwait(false));
        }

        /// <summary>Status of every message in one broadcast request. <paramref name="requestId"/> is a message key without its last 3 characters.</summary>
        /// <param name="requestId">Request id.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>Status entries.</returns>
        public async Task<IReadOnlyList<MessageStatus>> StatusByRequestIdAsync(string requestId, CancellationToken cancellationToken = default)
        {
            var path = "/api/comm/v1/message/inquiry/requestId/" + PathSegment.Escape(requestId, "requestId");
            return Statuses(await Get(BizgoOperation.GetMessageStatusByRequestId, path, null, BizgoJsonContext.Default.MessageStatusListResponse, cancellationToken).ConfigureAwait(false));
        }

        /// <summary>Daily acceptance and report counts.</summary>
        /// <param name="startDate">First day (<see cref="DateTime"/>, <see cref="DateTimeOffset"/>, <c>DateOnly</c> or <c>"YYYYMMDD"</c>).</param>
        /// <param name="endDate">Last day (optional).</param>
        /// <param name="serviceType"><c>SMS</c>, <c>MMS</c>, <c>RCS</c>, <c>ALIMTALK</c> or <c>BRANDMESSAGE</c>.</param>
        /// <param name="groupKey">Message group key.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>One entry per day.</returns>
        public async Task<IReadOnlyList<MessageStatistics>> StatisticsAsync(BizgoDate startDate, BizgoDate? endDate = null, string? serviceType = null, string? groupKey = null, CancellationToken cancellationToken = default)
        {
            var query = new QueryBuilder()
                .Add("startDate", startDate.ToQuery("startDate"))
                .Add("endDate", endDate?.ToQuery("endDate"))
                .Add("serviceType", serviceType)
                .Add("groupKey", groupKey)
                .Build();
            var response = await Get(BizgoOperation.GetMessageStatistics, "/api/comm/v1/message/statistics", query, BizgoJsonContext.Default.MessageStatisticsResponse, cancellationToken).ConfigureAwait(false);
            return response.Data?.Data?.Statistics?.Where(s => s is not null).ToArray() ?? Array.Empty<MessageStatistics>();
        }

        /// <summary>One page of send history from <paramref name="requestTime"/> (KST). Use <see cref="EnumerateHistoryAsync"/> to walk all pages.</summary>
        /// <param name="requestTime">Start time; converted to KST <c>yyyy-MM-ddTHH:mm:ss</c>.</param>
        /// <param name="serviceTypes">Channel types (<c>SMS</c>, <c>MMS</c>, <c>RCS</c>, <c>ALIMTALK</c>, <c>BRANDMESSAGE</c>); sent comma-separated.</param>
        /// <param name="groupKey">Message group key.</param>
        /// <param name="lastSeq">Cursor from the previous page.</param>
        /// <param name="limit">Page size, 1-1000 (server default 100).</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The page.</returns>
        public async Task<MessagePage> HistoryAsync(BizgoDateTime requestTime, IEnumerable<string>? serviceTypes = null, string? groupKey = null, long? lastSeq = null, int? limit = null, CancellationToken cancellationToken = default)
        {
            var query = new QueryBuilder()
                .Add("requestTime", requestTime.Require("requestTime", offset: false))
                .Add("serviceType", serviceTypes is null ? null : string.Join(",", serviceTypes))
                .Add("groupKey", groupKey)
                .Add("lastSeq", lastSeq)
                .Add("limit", Limit(limit))
                .Build();
            var response = await Get(BizgoOperation.GetMessageHistory, "/api/comm/v1/message/history", query, BizgoJsonContext.Default.MessageStatusListResponse, cancellationToken).ConfigureAwait(false);
            var inner = response.Data?.Data;
            return inner is null
                ? new MessagePage(Array.Empty<MessageStatus>(), null, false)
                : new MessagePage(inner.Messages?.Where(m => m is not null).ToArray() ?? Array.Empty<MessageStatus>(), inner.LastSeq, inner.HasNext ?? false);
        }

        /// <summary>
        /// All send history from <paramref name="requestTime"/>, following <c>lastSeq</c> across pages. Stops when <c>hasNext</c> is false,
        /// <c>lastSeq</c> is missing, or the cursor does not move.
        /// </summary>
        /// <param name="requestTime">Start time (KST).</param>
        /// <param name="serviceTypes">Channel types.</param>
        /// <param name="groupKey">Message group key.</param>
        /// <param name="limit">Page size, 1-1000.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>Messages, page by page.</returns>
        public async IAsyncEnumerable<MessageStatus> EnumerateHistoryAsync(BizgoDateTime requestTime, IEnumerable<string>? serviceTypes = null, string? groupKey = null, int? limit = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var types = serviceTypes?.ToArray();
            long? lastSeq = null;
            while (true)
            {
                var page = await HistoryAsync(requestTime, types, groupKey, lastSeq, limit, cancellationToken).ConfigureAwait(false);
                foreach (var message in page.Messages)
                {
                    yield return message;
                }

                if (!page.HasNext || page.LastSeq is null || page.LastSeq == lastSeq)
                {
                    yield break;
                }

                lastSeq = page.LastSeq;
            }
        }

        /// <summary>Looks up an MO (inbound) message by key. The same key can have several entries.</summary>
        /// <param name="msgKey">MO message key.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>Messages.</returns>
        public async Task<IReadOnlyList<MoMessage>> MoAsync(string msgKey, CancellationToken cancellationToken = default)
        {
            var path = "/api/comm/v1/message/inquiry/mo/msgKey/" + PathSegment.Escape(msgKey, "msgKey");
            var response = await Get(BizgoOperation.GetMoByMsgKey, path, null, BizgoJsonContext.Default.MoMessageListResponse, cancellationToken).ConfigureAwait(false);
            return response.Data?.Data?.Messages?.Where(m => m is not null).ToArray() ?? Array.Empty<MoMessage>();
        }

        /// <summary>One page of MO history, newest first, from <paramref name="occurredTime"/> (sent with the <c>+09:00</c> offset).</summary>
        /// <param name="occurredTime">Start time.</param>
        /// <param name="from">Filter by sender number.</param>
        /// <param name="to">Filter by MO number.</param>
        /// <param name="lastSeq">Cursor from the previous page.</param>
        /// <param name="limit">Page size, 1-1000.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The page.</returns>
        public async Task<MoPage> MoHistoryAsync(BizgoDateTime occurredTime, string? from = null, string? to = null, long? lastSeq = null, int? limit = null, CancellationToken cancellationToken = default)
        {
            var query = new QueryBuilder()
                .Add("occurredTime", occurredTime.Require("occurredTime", offset: true))
                .Add("from", from)
                .Add("to", to)
                .Add("lastSeq", lastSeq)
                .Add("limit", Limit(limit))
                .Build();
            var response = await Get(BizgoOperation.GetMoHistory, "/api/comm/v1/message/history/mo", query, BizgoJsonContext.Default.MoMessageListResponse, cancellationToken).ConfigureAwait(false);
            var inner = response.Data?.Data;
            return inner is null
                ? new MoPage(Array.Empty<MoMessage>(), null, false)
                : new MoPage(inner.Messages?.Where(m => m is not null).ToArray() ?? Array.Empty<MoMessage>(), inner.LastSeq, inner.HasNext ?? false);
        }

        /// <summary>All MO history pages from <paramref name="occurredTime"/>.</summary>
        /// <param name="occurredTime">Start time.</param>
        /// <param name="from">Filter by sender number.</param>
        /// <param name="to">Filter by MO number.</param>
        /// <param name="limit">Page size, 1-1000.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>Messages, page by page.</returns>
        public async IAsyncEnumerable<MoMessage> EnumerateMoHistoryAsync(BizgoDateTime occurredTime, string? from = null, string? to = null, int? limit = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            long? lastSeq = null;
            while (true)
            {
                var page = await MoHistoryAsync(occurredTime, from, to, lastSeq, limit, cancellationToken).ConfigureAwait(false);
                foreach (var message in page.Messages)
                {
                    yield return message;
                }

                if (!page.HasNext || page.LastSeq is null || page.LastSeq == lastSeq)
                {
                    yield break;
                }

                lastSeq = page.LastSeq;
            }
        }

        /// <inheritdoc/>
        public override string ToString() => "MessagesResource";

        private static MessageStatus[] Statuses(MessageStatusListResponse response)
        {
            return response.Data?.Data?.Messages?.Where(m => m is not null).ToArray() ?? Array.Empty<MessageStatus>();
        }

        private static long? Limit(int? limit)
        {
            if (limit is not null && (limit < 1 || limit > 1000))
            {
                throw new BizgoValidationException("limit", "limit은 1~1000입니다");
            }

            return limit;
        }

        private Task<T> Get<T>(BizgoOperation operation, string path, string? query, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        {
            return _transport.SendAsync(operation, path, query, null, RetryPolicy.Safe, 1, null, typeInfo, cancellationToken);
        }
    }
}
