using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Bizgo.Internal;
using Bizgo.Models;

namespace Bizgo
{
    /// <summary>
    /// Delivery reports (<c>/api/comm/v1/report/*</c>). Access as <c>client.Reports</c>.
    /// Pick one primary method per API key in the console: POLLING (this class) or WEBHOOK (<see cref="WebhookReceiver"/>).
    /// Use <see cref="InquiryAsync"/> to fill gaps.
    /// </summary>
    public sealed partial class ReportsResource
    {
        private const string PollingPath = "/api/comm/v1/report/polling";

        private readonly Transport _transport;

        internal ReportsResource(Transport transport)
        {
            _transport = transport;
        }

        /// <summary>
        /// Fetches the next batch of reports. <see cref="ReportBatch.IsEmpty"/> is true when there is nothing new.
        /// Call <see cref="AckAsync"/> with <see cref="ReportBatch.ReportId"/> after storing the batch, otherwise the same reports come
        /// again. Prefer <see cref="ConsumeAsync(Func{IReadOnlyList{Report}, CancellationToken, Task}, int?, CancellationToken)"/>, which does this for you.
        /// </summary>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The batch.</returns>
        public async Task<ReportBatch> PollAsync(CancellationToken cancellationToken = default)
        {
            var response = await _transport.SendAsync(BizgoOperation.GetReportPolling, PollingPath, null, null, RetryPolicy.Safe, 1, null, BizgoJsonContext.Default.ReportPollingResponse, cancellationToken).ConfigureAwait(false);
            var inner = response.Data?.Data;
            var reports = inner?.Report?.Where(r => r is not null).ToArray() ?? Array.Empty<Report>();
            return new ReportBatch(inner?.ReportId ?? string.Empty, reports);
        }

        /// <summary>Confirms receipt of a polled batch.</summary>
        /// <param name="reportId"><see cref="ReportBatch.ReportId"/>.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>A task.</returns>
        public async Task AckAsync(string reportId, CancellationToken cancellationToken = default)
        {
            var path = PollingPath + "/" + PathSegment.Escape(reportId, "reportId");
            await _transport.SendAsync(BizgoOperation.AckReportPolling, path, null, null, RetryPolicy.Safe, 1, null, BizgoJsonContext.Default.ApiResponse, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Polls until no reports are left, calling <paramref name="handler"/> for each batch and acknowledging it only after the handler
        /// completed without throwing. If the handler throws, the batch is not acknowledged and will be delivered again, so make the handler
        /// idempotent (for example upsert by <c>MsgKey</c>).
        /// </summary>
        /// <param name="handler">Stores one batch.</param>
        /// <param name="maxBatches">Stop after this many batches (default: until empty).</param>
        /// <param name="cancellationToken">Cancellation (also passed to the handler).</param>
        /// <returns>Number of reports handled.</returns>
        public async Task<int> ConsumeAsync(Func<IReadOnlyList<Report>, CancellationToken, Task> handler, int? maxBatches = null, CancellationToken cancellationToken = default)
        {
            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (maxBatches < 0)
            {
                throw new BizgoValidationException("maxBatches", "0 이상이어야 합니다");
            }

            var handled = 0;
            var batches = 0;
            while (maxBatches is null || batches < maxBatches)
            {
                var batch = await PollAsync(cancellationToken).ConfigureAwait(false);
                if (batch.IsEmpty)
                {
                    break;
                }

                await handler(batch.Reports, cancellationToken).ConfigureAwait(false);
                await AckAsync(batch.ReportId, cancellationToken).ConfigureAwait(false);
                handled += batch.Reports.Count;
                batches++;
            }

            return handled;
        }

        /// <summary>See <see cref="ConsumeAsync(Func{IReadOnlyList{Report}, CancellationToken, Task}, int?, CancellationToken)"/>.</summary>
        /// <param name="handler">Stores one batch.</param>
        /// <param name="maxBatches">Stop after this many batches.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>Number of reports handled.</returns>
        public Task<int> ConsumeAsync(Func<IReadOnlyList<Report>, Task> handler, int? maxBatches = null, CancellationToken cancellationToken = default)
        {
            if (handler is null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            return ConsumeAsync((reports, _) => handler(reports), maxBatches, cancellationToken);
        }

        /// <summary>Looks up the reports of one message (up to 30 days old).</summary>
        /// <param name="msgKey">Message key.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>Reports.</returns>
        public async Task<IReadOnlyList<Report>> InquiryAsync(string msgKey, CancellationToken cancellationToken = default)
        {
            var path = "/api/comm/v1/report/inquiry/" + PathSegment.Escape(msgKey, "msgKey");
            var response = await _transport.SendAsync(BizgoOperation.GetReportInquiry, path, null, null, RetryPolicy.Safe, 1, null, BizgoJsonContext.Default.ReportInquiryResponse, cancellationToken).ConfigureAwait(false);
            return response.Data?.Data?.Report?.Where(r => r is not null).ToArray() ?? Array.Empty<Report>();
        }

        /// <inheritdoc/>
        public override string ToString() => "ReportsResource";
    }
}
