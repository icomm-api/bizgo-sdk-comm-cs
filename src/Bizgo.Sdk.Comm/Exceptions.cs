using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Bizgo.Internal;

// Exception hierarchy (SDK-DESIGN §5):
//
//   BizgoException
//   ├── BizgoConfigurationException        missing API key, insecure base URL, ...
//   ├── BizgoValidationException           request rejected by the SDK before it was sent
//   ├── BizgoConnectionException           network failure; a send may or may not have been accepted
//   │   └── BizgoTimeoutException
//   ├── BizgoApiException                  Bizgo answered with a failure code
//   │   ├── BizgoBadRequestException        400 / invalid field
//   │   ├── BizgoAuthenticationException    401 / A401, A100, A001, A002
//   │   ├── BizgoPermissionDeniedException  403 / A403, A110, A111
//   │   ├── BizgoNotFoundException          404
//   │   ├── BizgoDuplicateRequestException  service A301 (same idempotencyKey within its TTL)
//   │   ├── BizgoRateLimitException         429 / A020
//   │   └── BizgoInternalServerException    5xx
//   ├── BizgoInvalidResponseException      the response is not the documented envelope
//   └── BizgoWebhookVerificationException  webhook signature / timestamp / body check failed
//
// Messages never contain the API key, request bodies, query strings or phone numbers, and wrapped
// HTTP library exceptions are not kept as InnerException (their messages can contain URLs).
namespace Bizgo
{
#pragma warning disable CA1032 // the standard constructors are deliberately omitted: every exception needs its details

    /// <summary>Base class of every exception thrown by the SDK.</summary>
    public class BizgoException : Exception
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="message">Message without secrets or personal data.</param>
        public BizgoException(string message)
            : base(message)
        {
        }
    }

    /// <summary>The client is configured incorrectly (for example the API key is missing or the base URL is not https).</summary>
    public class BizgoConfigurationException : BizgoException
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="message">Message.</param>
        public BizgoConfigurationException(string message)
            : base(message)
        {
        }
    }

    /// <summary>One validation problem: the field path (for example <c>messageFlow[0].sms.text</c>) and the reason.</summary>
    public sealed class BizgoValidationError
    {
        /// <summary>Creates a validation problem.</summary>
        /// <param name="path">Field path, or an empty string for the object itself.</param>
        /// <param name="reason">Reason (Korean). Never contains the value.</param>
        public BizgoValidationError(string path, string reason)
        {
            Path = path ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        /// <summary>Field path in API names, for example <c>destinations[3].to</c>.</summary>
        public string Path { get; }

        /// <summary>Why the value was rejected. The value itself is never included.</summary>
        public string Reason { get; }

        /// <inheritdoc/>
        public override string ToString() => Path.Length == 0 ? Reason : Path + ": " + Reason;
    }

    /// <summary>
    /// The request was rejected by the SDK before anything was sent: a required field is missing, a value is too long
    /// (for example SMS text over 90 bytes in EUC-KR), has an unsupported character, or an argument is out of range.
    /// </summary>
    /// <remarks>The message lists field paths and reasons only; input values (phone numbers, text) are never included.</remarks>
    public class BizgoValidationException : BizgoException
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="errors">The problems.</param>
        public BizgoValidationException(IReadOnlyList<BizgoValidationError> errors)
            : base(Format(errors))
        {
            Errors = errors ?? Array.Empty<BizgoValidationError>();
        }

        /// <summary>Creates the exception with one problem.</summary>
        /// <param name="path">Field or parameter name.</param>
        /// <param name="reason">Reason.</param>
        public BizgoValidationException(string path, string reason)
            : this(new[] { new BizgoValidationError(path, reason) })
        {
        }

        /// <summary>Every problem found.</summary>
        public IReadOnlyList<BizgoValidationError> Errors { get; }

        private static string Format(IReadOnlyList<BizgoValidationError>? errors)
        {
            if (errors is null || errors.Count == 0)
            {
                return "요청이 올바르지 않습니다";
            }

            const int Shown = 10;
            var text = "요청이 올바르지 않습니다: " + string.Join("; ", errors.Take(Shown).Select(e => e.ToString()));
            return errors.Count > Shown ? text + " (외 " + (errors.Count - Shown) + "건)" : text;
        }
    }

    /// <summary>
    /// The request failed before a response arrived (DNS, TLS, connection reset, ...).
    /// </summary>
    /// <remarks>
    /// For a send request the message may or may not have been accepted. Re-sending can cause a duplicate unless you set
    /// <see cref="SendOptions.IdempotencyKey"/>; check with <c>client.Messages.StatusAsync</c> when in doubt.
    /// </remarks>
    public class BizgoConnectionException : BizgoException
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="message">Message.</param>
        public BizgoConnectionException(string message)
            : base(message)
        {
        }
    }

    /// <summary>The request timed out (<see cref="BizgoClientOptions.Timeout"/>).</summary>
    public class BizgoTimeoutException : BizgoConnectionException
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="message">Message.</param>
        public BizgoTimeoutException(string message)
            : base(message)
        {
        }
    }

    /// <summary>Which part of Bizgo reported the failure.</summary>
    public enum BizgoErrorLayer
    {
        /// <summary>API gateway (<c>common.authCode</c>): authentication, permission, request format.</summary>
        Gateway,

        /// <summary>Product API (<c>data.code</c>). The same code can mean something else than in the gateway layer (for example <c>A301</c>).</summary>
        Service,
    }

    /// <summary>Bizgo answered with a failure.</summary>
    public class BizgoApiException : BizgoException
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="serverMessage"><c>authResult</c> / <c>result</c> text from the server.</param>
        /// <param name="httpStatus">HTTP status code.</param>
        /// <param name="code"><c>common.authCode</c> or <c>data.code</c>.</param>
        /// <param name="layer">Layer that reported the code.</param>
        /// <param name="trackingId"><c>common.infobankTrId</c>.</param>
        /// <param name="body">Parsed response body.</param>
        public BizgoApiException(string serverMessage, int httpStatus, string? code, BizgoErrorLayer layer, string? trackingId = null, JsonElement? body = null)
            : base(Format(serverMessage, httpStatus, code, layer, trackingId))
        {
            ServerMessage = serverMessage ?? string.Empty;
            HttpStatus = httpStatus;
            Code = code;
            Layer = layer;
            TrackingId = trackingId;
            Body = body;
            Description = Describe(code, layer);
        }

        /// <summary>HTTP status code.</summary>
        public int HttpStatus { get; }

        /// <summary><c>common.authCode</c> (gateway) or <c>data.code</c> (service), for example <c>A401</c>. Null when the body had no code.</summary>
        public string? Code { get; }

        /// <summary>Layer that reported <see cref="Code"/>. The same code means different things per layer.</summary>
        public BizgoErrorLayer Layer { get; }

        /// <summary>Korean description of a service-layer <see cref="Code"/> from the Bizgo error-code table, if known.</summary>
        public string? Description { get; }

        /// <summary><c>common.infobankTrId</c>. Give this to Bizgo support when asking about a request.</summary>
        public string? TrackingId { get; }

        /// <summary><c>authResult</c> / <c>result</c> text from the server (or an SDK explanation).</summary>
        public string ServerMessage { get; }

        /// <summary>The parsed response body. It can contain phone numbers: do not log it as is.</summary>
        public JsonElement? Body { get; }

        /// <summary>
        /// Serializes the error (type, status, code, layer, server message, tracking id, <c>Retry-After</c>, <c>AlreadyAccepted</c> and body) as
        /// JSON, for example to hand it to another process or a job queue. This is the supported way: the exceptions are not
        /// <c>[Serializable]</c>, because <c>BinaryFormatter</c> is obsolete and insecure in .NET. The body can contain phone numbers: treat the
        /// JSON like the response itself.
        /// </summary>
        /// <returns>JSON text; read it back with <see cref="FromJson(string)"/>.</returns>
        public string ToJson() => ExceptionJson.Write(this);

        /// <summary>Recreates an error written by <see cref="ToJson"/> (same exception type and properties).</summary>
        /// <param name="json">Text from <see cref="ToJson"/>.</param>
        /// <returns>The error.</returns>
        /// <exception cref="ArgumentException">The text is not an error written by <see cref="ToJson"/>.</exception>
        public static BizgoApiException FromJson(string json) => ExceptionJson.Read(json);

        internal static string? Describe(string? code, BizgoErrorLayer layer)
        {
            return layer == BizgoErrorLayer.Service && code is not null && ServiceCodes.TryGet(code, out var known) ? known.Description : null;
        }

        private static string Format(string? serverMessage, int httpStatus, string? code, BizgoErrorLayer layer, string? trackingId)
        {
            var parts = new List<string>
            {
                "HTTP " + httpStatus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                (layer == BizgoErrorLayer.Gateway ? "gateway" : "service") + " code=" + (code ?? "-"),
            };
            if (!string.IsNullOrEmpty(serverMessage))
            {
                parts.Add(serverMessage!);
            }

            var description = Describe(code, layer);
            if (!string.IsNullOrEmpty(description) && description != serverMessage)
            {
                parts.Add(description!);
            }

            if (!string.IsNullOrEmpty(trackingId))
            {
                parts.Add("infobankTrId=" + trackingId);
            }

            return string.Join(" | ", parts);
        }
    }

    /// <summary>The request was rejected as invalid (HTTP 400, invalid field codes such as <c>A306</c>).</summary>
    public class BizgoBadRequestException : BizgoApiException
    {
        /// <inheritdoc cref="BizgoApiException(string, int, string?, BizgoErrorLayer, string?, JsonElement?)"/>
        public BizgoBadRequestException(string serverMessage, int httpStatus, string? code, BizgoErrorLayer layer, string? trackingId = null, JsonElement? body = null)
            : base(serverMessage, httpStatus, code, layer, trackingId, body)
        {
        }
    }

    /// <summary>The API key is missing, wrong or not allowed from this IP. Check the key and that the calling server's IP is registered.</summary>
    public class BizgoAuthenticationException : BizgoApiException
    {
        /// <inheritdoc cref="BizgoApiException(string, int, string?, BizgoErrorLayer, string?, JsonElement?)"/>
        public BizgoAuthenticationException(string serverMessage, int httpStatus, string? code, BizgoErrorLayer layer, string? trackingId = null, JsonElement? body = null)
            : base(serverMessage, httpStatus, code, layer, trackingId, body)
        {
        }
    }

    /// <summary>The key is valid but not allowed to do this (blocked account, ...).</summary>
    public class BizgoPermissionDeniedException : BizgoApiException
    {
        /// <inheritdoc cref="BizgoApiException(string, int, string?, BizgoErrorLayer, string?, JsonElement?)"/>
        public BizgoPermissionDeniedException(string serverMessage, int httpStatus, string? code, BizgoErrorLayer layer, string? trackingId = null, JsonElement? body = null)
            : base(serverMessage, httpStatus, code, layer, trackingId, body)
        {
        }
    }

    /// <summary>The resource does not exist (HTTP 404).</summary>
    public class BizgoNotFoundException : BizgoApiException
    {
        /// <inheritdoc cref="BizgoApiException(string, int, string?, BizgoErrorLayer, string?, JsonElement?)"/>
        public BizgoNotFoundException(string serverMessage, int httpStatus, string? code, BizgoErrorLayer layer, string? trackingId = null, JsonElement? body = null)
            : base(serverMessage, httpStatus, code, layer, trackingId, body)
        {
        }
    }

    /// <summary>A request with the same <c>idempotencyKey</c> was already accepted (service code <c>A301</c>). The message was not sent again.</summary>
    public class BizgoDuplicateRequestException : BizgoApiException
    {
        /// <inheritdoc cref="BizgoApiException(string, int, string?, BizgoErrorLayer, string?, JsonElement?)"/>
        public BizgoDuplicateRequestException(string serverMessage, int httpStatus, string? code, BizgoErrorLayer layer, string? trackingId = null, JsonElement? body = null)
            : this(serverMessage, httpStatus, code, layer, trackingId, body, alreadyAccepted: false)
        {
        }

        /// <summary>Creates the exception.</summary>
        /// <param name="serverMessage">Server text (or the SDK explanation).</param>
        /// <param name="httpStatus">HTTP status.</param>
        /// <param name="code">Code.</param>
        /// <param name="layer">Layer.</param>
        /// <param name="trackingId">Tracking id.</param>
        /// <param name="body">Body.</param>
        /// <param name="alreadyAccepted">The SDK retried this request, so an earlier attempt was probably accepted.</param>
        public BizgoDuplicateRequestException(string serverMessage, int httpStatus, string? code, BizgoErrorLayer layer, string? trackingId, JsonElement? body, bool alreadyAccepted)
            : base(serverMessage, httpStatus, code, layer, trackingId, body)
        {
            AlreadyAccepted = alreadyAccepted;
        }

        /// <summary>
        /// True when the SDK had retried the request (after a timeout, connection error, 5xx or 429): an earlier attempt was then most likely
        /// accepted, so the message was sent once. Check with the status APIs.
        /// </summary>
        public bool AlreadyAccepted { get; }
    }

    /// <summary>Too many requests (HTTP 429 / <c>A020</c>). Default limits: 200 TPS for send APIs, 5 TPS for the others.</summary>
    public class BizgoRateLimitException : BizgoApiException
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="serverMessage">Server text.</param>
        /// <param name="httpStatus">HTTP status.</param>
        /// <param name="code">Code.</param>
        /// <param name="layer">Layer.</param>
        /// <param name="trackingId">Tracking id.</param>
        /// <param name="body">Body.</param>
        /// <param name="retryAfter">Value of the <c>Retry-After</c> header, if any.</param>
        public BizgoRateLimitException(string serverMessage, int httpStatus, string? code, BizgoErrorLayer layer, string? trackingId = null, JsonElement? body = null, TimeSpan? retryAfter = null)
            : base(serverMessage, httpStatus, code, layer, trackingId, body)
        {
            RetryAfter = retryAfter;
        }

        /// <summary>Time from the <c>Retry-After</c> header, if the server sent one.</summary>
        public TimeSpan? RetryAfter { get; }
    }

    /// <summary>Bizgo had an internal error (HTTP 5xx).</summary>
    public class BizgoInternalServerException : BizgoApiException
    {
        /// <inheritdoc cref="BizgoApiException(string, int, string?, BizgoErrorLayer, string?, JsonElement?)"/>
        public BizgoInternalServerException(string serverMessage, int httpStatus, string? code, BizgoErrorLayer layer, string? trackingId = null, JsonElement? body = null)
            : base(serverMessage, httpStatus, code, layer, trackingId, body)
        {
        }
    }

    /// <summary>
    /// The response could not be read as the documented <c>{common, data}</c> envelope (not JSON, wrong shape, too large or too deep),
    /// or was a redirect. For a send the request may still have been accepted: check with the status APIs.
    /// </summary>
    public class BizgoInvalidResponseException : BizgoException
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="message">Message.</param>
        /// <param name="httpStatus">HTTP status, if a response arrived.</param>
        public BizgoInvalidResponseException(string message, int? httpStatus = null)
            : this(message, httpStatus, null, null)
        {
        }

        /// <summary>Creates the exception.</summary>
        /// <param name="message">Message.</param>
        /// <param name="httpStatus">HTTP status, if a response arrived.</param>
        /// <param name="trackingId"><c>common.infobankTrId</c>, if the body had one.</param>
        /// <param name="body">The parsed body, if it was JSON.</param>
        public BizgoInvalidResponseException(string message, int? httpStatus, string? trackingId, JsonElement? body)
            : base(message)
        {
            HttpStatus = httpStatus;
            TrackingId = trackingId;
            Body = body;
        }

        /// <summary>HTTP status, if a response arrived.</summary>
        public int? HttpStatus { get; }

        /// <summary><c>common.infobankTrId</c>, if the body had one.</summary>
        public string? TrackingId { get; }

        /// <summary>The parsed body, when it was a JSON object of an unexpected shape. It can contain phone numbers: do not log it as is.</summary>
        public JsonElement? Body { get; }
    }

    /// <summary>A webhook request failed signature, timestamp or body checks. Answer it with HTTP 401.</summary>
    public class BizgoWebhookVerificationException : BizgoException
    {
        /// <summary>Creates the exception.</summary>
        /// <param name="message">Message.</param>
        public BizgoWebhookVerificationException(string message)
            : base(message)
        {
        }
    }

#pragma warning restore CA1032
}
