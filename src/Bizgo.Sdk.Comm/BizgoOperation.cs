using System;
using System.Collections.Generic;
using System.Linq;

namespace Bizgo
{
    /// <summary>Retry policy of an operation (SDK-DESIGN §6, <c>x-sdk-retry</c>).</summary>
    public enum BizgoRetryPolicy
    {
        /// <summary>Read-only or idempotent: retried on HTTP 429, 500/502/503/504 and connection errors.</summary>
        Safe,

        /// <summary>Could run twice (create, send without idempotency key, upload): retried only on HTTP 429.</summary>
        RateLimitOnly,
    }

    /// <summary>Client-side token bucket an operation uses (SDK-DESIGN §11.2).</summary>
    public enum BizgoRateBucket
    {
        /// <summary>Send APIs (<c>x-sdk-rate: send</c>): default 200 messages per second, one token per destination.</summary>
        Send,

        /// <summary>Every other API: default 5 requests per second.</summary>
        Other,
    }

    /// <summary>
    /// Metadata of one API operation from the spec: id, <c>resource.method</c> name, HTTP method and <b>path template</b>.
    /// Hooks, OpenTelemetry spans and <c>Bizgo.Sdk.Comm.Testing</c> use it. It never holds request values.
    /// </summary>
    public sealed partial class BizgoOperation
    {
        private BizgoOperation(string id, string name, string httpMethod, string pathTemplate, BizgoRetryPolicy retry, BizgoRateBucket rate, string? pagination, string member, string? enumerator, bool handWritten)
        {
            Id = id;
            Name = name;
            HttpMethod = httpMethod;
            PathTemplate = pathTemplate;
            Retry = retry;
            RateBucket = rate;
            Pagination = pagination;
            Member = member;
            Enumerator = enumerator;
            HandWritten = handWritten;
            Segments = pathTemplate.Split('/');
        }

        /// <summary>Every operation in the spec, in spec order.</summary>
        public static IReadOnlyList<BizgoOperation> All => AllOperations;

        /// <summary><c>operationId</c>, for example <c>listAlimtalkTemplates</c>.</summary>
        public string Id { get; }

        /// <summary><c>&lt;x-sdk-resource&gt;.&lt;x-sdk-method&gt;</c>, for example <c>alimtalk.templates.list</c>.</summary>
        public string Name { get; }

        /// <summary>HTTP method (<c>GET</c>, <c>POST</c>, <c>PUT</c>, <c>DELETE</c>).</summary>
        public string HttpMethod { get; }

        /// <summary>Path template, for example <c>/api/comm/v1/report/inquiry/{msgKey}</c>.</summary>
        public string PathTemplate { get; }

        /// <summary>Retry policy from the spec. <c>sendOmni</c> uses <see cref="BizgoRetryPolicy.Safe"/> when an idempotency key is set.</summary>
        public BizgoRetryPolicy Retry { get; }

        /// <summary>Client-side rate bucket.</summary>
        public BizgoRateBucket RateBucket { get; }

        /// <summary><c>cursor</c>, <c>page</c>, <c>offset</c>, or null when the operation is not paginated.</summary>
        public string? Pagination { get; }

        /// <summary>SDK member from the client, for example <c>Alimtalk.Templates.ListAsync</c>.</summary>
        internal string Member { get; }

        /// <summary>SDK member that walks every page, if any.</summary>
        internal string? Enumerator { get; }

        /// <summary>Implemented by a hand-written convenience method.</summary>
        internal bool HandWritten { get; }

        private string[] Segments { get; }

        /// <summary>Finds an operation by <c>operationId</c>.</summary>
        /// <param name="operationId">For example <c>sendOmni</c>.</param>
        /// <returns>The operation, or null.</returns>
        public static BizgoOperation? Find(string operationId) => AllOperations.FirstOrDefault(o => string.Equals(o.Id, operationId, StringComparison.Ordinal));

        /// <summary>Finds the operation for a request (literal segments win over <c>{parameter}</c> segments).</summary>
        /// <param name="httpMethod">HTTP method.</param>
        /// <param name="path">Escaped request path without query, for example <c>/api/comm/v1/report/inquiry/KEY</c>.</param>
        /// <returns>The operation, or null.</returns>
        public static BizgoOperation? Match(string httpMethod, string path)
        {
            if (httpMethod is null || path is null)
            {
                return null;
            }

            var segments = path.Split('/');
            BizgoOperation? best = null;
            var bestLiterals = -1;
            foreach (var op in AllOperations)
            {
                if (!string.Equals(op.HttpMethod, httpMethod, StringComparison.OrdinalIgnoreCase) || op.Segments.Length != segments.Length)
                {
                    continue;
                }

                var literals = 0;
                var ok = true;
                for (var i = 0; i < segments.Length && ok; i++)
                {
                    var template = op.Segments[i];
                    if (template.StartsWith("{", StringComparison.Ordinal))
                    {
                        ok = segments[i].Length > 0;
                    }
                    else
                    {
                        ok = string.Equals(template, segments[i], StringComparison.Ordinal);
                        literals++;
                    }
                }

                if (ok && literals > bestLiterals)
                {
                    best = op;
                    bestLiterals = literals;
                }
            }

            return best;
        }

        /// <inheritdoc/>
        public override string ToString() => Name + " (" + HttpMethod + " " + PathTemplate + ")";
    }
}
