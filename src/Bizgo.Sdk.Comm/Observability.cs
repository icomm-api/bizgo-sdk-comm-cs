using System;
using System.Globalization;

namespace Bizgo
{
    /// <summary>
    /// Request callbacks for metrics and tracing (SDK-DESIGN §11.4). They receive only the operation, the HTTP method, the
    /// <b>path template</b>, the status, the Bizgo code/layer, the attempt number and the duration: never bodies, query strings, header
    /// values, path values, phone numbers or the API key. An exception thrown by a callback is swallowed and never changes the result.
    /// </summary>
    /// <remarks>
    /// Callbacks run synchronously on the request path: keep them fast and do not pass <c>async</c> lambdas (an <c>async void</c> callback's
    /// exception cannot be caught by the SDK and would crash the process). Hand work to a queue instead.
    /// </remarks>
    /// <example>
    /// <code>
    /// Hooks = new BizgoHooks
    /// {
    ///     OnResponse = e =&gt; metrics.Record(e.Operation.Name, e.StatusCode, e.Duration),
    /// }
    /// </code>
    /// </example>
    public sealed class BizgoHooks
    {
        /// <summary>Called before each HTTP attempt (after rate limiting).</summary>
        public Action<BizgoRequestEvent>? OnRequest { get; set; }

        /// <summary>Called after each HTTP attempt: with the parsed outcome for the final attempt, or with <see cref="BizgoResponseEvent.WillRetry"/> set.</summary>
        public Action<BizgoResponseEvent>? OnResponse { get; set; }

        /// <inheritdoc/>
        public override string ToString() => "BizgoHooks";
    }

    /// <summary>An HTTP attempt is about to be sent.</summary>
    public class BizgoRequestEvent
    {
        internal BizgoRequestEvent(BizgoOperation operation, int attempt)
        {
            Operation = operation;
            Attempt = attempt;
        }

        /// <summary>The operation (id, <c>resource.method</c>, HTTP method, path template).</summary>
        public BizgoOperation Operation { get; }

        /// <summary><c>operationId</c>.</summary>
        public string OperationId => Operation.Id;

        /// <summary>HTTP method.</summary>
        public string HttpMethod => Operation.HttpMethod;

        /// <summary>Path template, for example <c>/api/comm/v1/report/inquiry/{msgKey}</c> (never the real values).</summary>
        public string PathTemplate => Operation.PathTemplate;

        /// <summary>Attempt number, starting at 1.</summary>
        public int Attempt { get; }

        /// <inheritdoc/>
        public override string ToString() => Operation.Name + " attempt " + Attempt.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>An HTTP attempt finished.</summary>
    public sealed class BizgoResponseEvent : BizgoRequestEvent
    {
        internal BizgoResponseEvent(BizgoOperation operation, int attempt, int? statusCode, string? code, BizgoErrorLayer? layer, string? errorType, TimeSpan duration, bool willRetry)
            : base(operation, attempt)
        {
            StatusCode = statusCode;
            Code = code;
            Layer = layer;
            ErrorType = errorType;
            Duration = duration;
            WillRetry = willRetry;
        }

        /// <summary>HTTP status, or null when no response arrived (connection error, timeout).</summary>
        public int? StatusCode { get; }

        /// <summary>Bizgo code (<c>A000</c> on success, the gateway or service code on failure), if known.</summary>
        public string? Code { get; }

        /// <summary>Layer of a failure code; null on success.</summary>
        public BizgoErrorLayer? Layer { get; }

        /// <summary>Exception type name when the attempt failed (for example <c>BizgoRateLimitException</c>); null on success.</summary>
        public string? ErrorType { get; }

        /// <summary>True on success (a parse failure is not a success).</summary>
        public bool Succeeded => ErrorType is null;

        /// <summary>Duration of this attempt.</summary>
        public TimeSpan Duration { get; }

        /// <summary>The SDK retries after this attempt.</summary>
        public bool WillRetry { get; }

        /// <inheritdoc/>
        public override string ToString() => base.ToString() + " -> " + (StatusCode?.ToString(CultureInfo.InvariantCulture) ?? ErrorType ?? "-");
    }
}

namespace Bizgo.Internal
{
    /// <summary>
    /// OpenTelemetry-compatible tracing via <c>System.Diagnostics.ActivitySource("Bizgo")</c> (SDK-DESIGN §11.4). No extra package: on
    /// net8.0 the type is in the BCL; the netstandard2.0 build does not emit activities (it would need the
    /// <c>System.Diagnostics.DiagnosticSource</c> package; use <see cref="BizgoHooks"/> there).
    /// Span <c>bizgo &lt;resource&gt;.&lt;method&gt;</c> with <c>http.request.method</c>, <c>url.template</c>, <c>http.response.status_code</c>,
    /// <c>bizgo.code</c>, <c>bizgo.layer</c>, <c>bizgo.retry_count</c> and <c>bizgo.operation_id</c>. Never values, bodies or queries.
    /// </summary>
    internal static class Telemetry
    {
        internal const string SourceName = "Bizgo";

#if NET8_0_OR_GREATER
        internal static readonly System.Diagnostics.ActivitySource Source = new System.Diagnostics.ActivitySource(SourceName, typeof(Telemetry).Assembly.GetName().Version?.ToString());

        internal static System.Diagnostics.Activity? Start(BizgoOperation operation)
        {
            var activity = Source.StartActivity("bizgo " + operation.Name, System.Diagnostics.ActivityKind.Client);
            if (activity is null)
            {
                return null;
            }

            activity.SetTag("http.request.method", operation.HttpMethod);
            activity.SetTag("url.template", operation.PathTemplate);
            activity.SetTag("bizgo.operation_id", operation.Id);
            return activity;
        }

        internal static void Stop(System.Diagnostics.Activity? activity, int? status, string? code, BizgoErrorLayer? layer, int retries, string? errorType)
        {
            if (activity is null)
            {
                return;
            }

            if (status is not null)
            {
                activity.SetTag("http.response.status_code", status.Value);
            }

            if (code is not null)
            {
                activity.SetTag("bizgo.code", code);
            }

            if (layer is not null)
            {
                activity.SetTag("bizgo.layer", layer == BizgoErrorLayer.Gateway ? "gateway" : "service");
            }

            activity.SetTag("bizgo.retry_count", retries);
            if (errorType is not null)
            {
                activity.SetTag("error.type", errorType);
                activity.SetStatus(System.Diagnostics.ActivityStatusCode.Error);
            }

            activity.Dispose();
        }
#else
        internal static object? Start(BizgoOperation operation)
        {
            _ = operation;
            return null;
        }

        internal static void Stop(object? activity, int? status, string? code, BizgoErrorLayer? layer, int retries, string? errorType)
        {
            _ = activity;
            _ = status;
            _ = code;
            _ = layer;
            _ = retries;
            _ = errorType;
        }
#endif
    }
}
