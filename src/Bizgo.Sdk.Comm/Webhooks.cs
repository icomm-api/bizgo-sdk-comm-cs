using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Bizgo.Internal;
using Bizgo.Models;

namespace Bizgo
{
    /// <summary>
    /// Verifies and parses Bizgo webhooks (delivery reports and MO messages).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The signature is <c>X-IB-Signature = HmacSHA256(secret, X-IB-Timestamp)</c>; the receiver verifies it together with the
    /// timestamp window. As general practice, also restrict the endpoint to the Bizgo webhook source IPs, serve it over HTTPS, and
    /// confirm important results with the inquiry APIs (<c>client.Reports.InquiryAsync</c>).
    /// </para>
    /// <para>Bizgo retries a webhook up to 3 times when it does not get <c>{"msgKey": ...}</c> back within 5 seconds, so deduplicate by <c>msgKey</c>.</para>
    /// <para>The signature output encoding (hex or base64) is not confirmed yet; both are accepted (hex in any case).</para>
    /// <para>The webhook secret is obtained by requesting it from Bizgo (it is delivered separately).</para>
    /// <para>
    /// Counsel (상담톡) webhooks carry no signature (signatures apply only to report and MO webhooks). Their parsers do not require or check
    /// <c>X-IB-Timestamp</c>/<c>X-IB-Signature</c> (ignored if present) and need no webhook secret: use the static
    /// <c>Webhooks.ParseCounsel*</c> methods (for example <see cref="ParseCounselMessage(byte[])"/>) or the <see cref="WebhookReceiver"/> counsel
    /// methods. The body size cap, JSON depth limit (64) and type checks still apply. Serve the endpoint over HTTPS, allow-list the Bizgo
    /// webhook source IPs, and deduplicate by <c>msgKey</c> where present.
    /// Counsel webhooks are answered with <see cref="CounselAckJson"/> (<c>{"code":"A000","result":"Success"}</c>), not <c>{"msgKey"}</c>.
    /// </para>
    /// </remarks>
    public static partial class Webhooks
    {
        /// <summary>Timestamp header name.</summary>
        public const string TimestampHeader = "X-IB-Timestamp";

        /// <summary>Signature header name.</summary>
        public const string SignatureHeader = "X-IB-Signature";

        /// <summary>Largest accepted webhook body (1MB).</summary>
        public const int MaxBodyBytes = 1024 * 1024;

        /// <summary>Default maximum age of a webhook timestamp (5 minutes), to limit replay.</summary>
        public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(300);

        /// <summary>Pass as tolerance to turn the timestamp age check off explicitly (not recommended: replayed requests are then accepted).</summary>
        public static readonly TimeSpan NoTolerance = System.Threading.Timeout.InfiniteTimeSpan;

        /// <summary>Verifies <c>X-IB-Signature</c>, or throws <see cref="BizgoWebhookVerificationException"/>.</summary>
        /// <param name="secret">Webhook secret (request it from Bizgo). Read it from an environment variable or a vault.</param>
        /// <param name="timestamp"><c>X-IB-Timestamp</c> header value (epoch milliseconds if 13+ digits, otherwise seconds).</param>
        /// <param name="signature"><c>X-IB-Signature</c> header value.</param>
        /// <param name="tolerance">Maximum age (either direction). Default <see cref="DefaultTolerance"/>.</param>
        /// <param name="now">Current time, for tests. Default: <see cref="DateTimeOffset.UtcNow"/>.</param>
        /// <exception cref="BizgoWebhookVerificationException">The signature is missing or wrong, or the timestamp is malformed or too old.</exception>
        /// <exception cref="BizgoConfigurationException"><paramref name="secret"/> is empty or whitespace, or <paramref name="tolerance"/> is not positive (use <see cref="NoTolerance"/> to turn the check off).</exception>
        public static void VerifySignature(string secret, string? timestamp, string? signature, TimeSpan? tolerance = null, DateTimeOffset? now = null)
        {
            Verify(Secret(secret), timestamp, signature, CheckTolerance(tolerance), now ?? DateTimeOffset.UtcNow);
        }

        /// <summary>The response body counsel (상담톡) webhooks expect: <c>{"code": "A000", "result": "Success"}</c>.</summary>
        /// <returns>The acknowledgement.</returns>
        public static CounselWebhookAck CounselAck() => new CounselWebhookAck { Code = "A000", Result = "Success" };

        /// <summary>The counsel acknowledgement as JSON text, ready to write as an <c>application/json</c> response.</summary>
        /// <returns><c>{"code":"A000","result":"Success"}</c>.</returns>
        public static string CounselAckJson() => JsonSerializer.Serialize(CounselAck(), BizgoJsonContext.Default.CounselWebhookAck);

        internal static byte[] Secret(string secret)
        {
            if (secret is null || secret.Trim().Length == 0)
            {
                // a server configuration problem, not a bad request: the same exception everywhere (SDK-DESIGN §12.7)
                throw new BizgoConfigurationException("웹훅 secret이 비어 있거나 공백뿐입니다. 환경변수나 시크릿 저장소에서 읽었는지 확인하세요.");
            }

            return Encoding.UTF8.GetBytes(secret);
        }

        internal static TimeSpan CheckTolerance(TimeSpan? tolerance)
        {
            var value = tolerance ?? DefaultTolerance;
            if (value != NoTolerance && value <= TimeSpan.Zero)
            {
                throw new BizgoConfigurationException("웹훅 tolerance는 0보다 커야 합니다(검사를 끄려면 Webhooks.NoTolerance).");
            }

            return value;
        }

        /// <summary>Parses a report webhook body. Does not verify the signature; use <see cref="WebhookReceiver"/> for that.</summary>
        /// <param name="body">Raw UTF-8 body.</param>
        /// <returns>The report.</returns>
        /// <exception cref="BizgoWebhookVerificationException">The body is too large or not a JSON object.</exception>
        public static ReportWebhookPayload ParseReport(byte[] body) => Parse(body, BizgoJsonContext.Default.ReportWebhookPayload);

        /// <summary>Parses a report webhook body.</summary>
        /// <param name="body">Body text.</param>
        /// <returns>The report.</returns>
        public static ReportWebhookPayload ParseReport(string body) => Parse(Utf8(body), BizgoJsonContext.Default.ReportWebhookPayload);

        /// <summary>Parses an MO webhook body. Does not verify the signature; use <see cref="WebhookReceiver"/> for that.</summary>
        /// <param name="body">Raw UTF-8 body.</param>
        /// <returns>The MO message.</returns>
        public static MoWebhookPayload ParseMo(byte[] body) => Parse(body, BizgoJsonContext.Default.MoWebhookPayload);

        /// <summary>Parses an MO webhook body.</summary>
        /// <param name="body">Body text.</param>
        /// <returns>The MO message.</returns>
        public static MoWebhookPayload ParseMo(string body) => Parse(Utf8(body), BizgoJsonContext.Default.MoWebhookPayload);

        /// <summary>The response body Bizgo expects: <c>{"msgKey": "&lt;received msgKey&gt;"}</c>.</summary>
        /// <param name="msgKey">Received message key.</param>
        /// <returns>The acknowledgement.</returns>
        public static WebhookAck Ack(string msgKey) => new WebhookAck { MsgKey = msgKey };

        /// <summary>The acknowledgement as JSON text, ready to write as an <c>application/json</c> response.</summary>
        /// <param name="msgKey">Received message key.</param>
        /// <returns>JSON text.</returns>
        public static string AckJson(string msgKey) => JsonSerializer.Serialize(Ack(msgKey), BizgoJsonContext.Default.WebhookAck);

        internal static void Verify(byte[] secret, string? timestamp, string? signature, TimeSpan tolerance, DateTimeOffset now)
        {
            if (secret.Length == 0)
            {
                throw new BizgoWebhookVerificationException("웹훅 secret이 비어 있습니다");
            }

            if (string.IsNullOrEmpty(signature))
            {
                throw new BizgoWebhookVerificationException("X-IB-Signature 헤더가 없습니다");
            }

            var ts = timestamp ?? string.Empty; // not trimmed: " 123 " is rejected (SDK-DESIGN §12.7)
            var sentAt = TimestampSeconds(ts);
            var age = Math.Abs(now.ToUnixTimeMilliseconds() / 1000.0 - sentAt);
            if (tolerance != NoTolerance && age > tolerance.TotalSeconds)
            {
                throw new BizgoWebhookVerificationException("X-IB-Timestamp가 허용 범위(" + tolerance.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "초)를 벗어났습니다");
            }

            byte[] digest;
            using (var hmac = new HMACSHA256(secret))
            {
                digest = hmac.ComputeHash(Encoding.ASCII.GetBytes(ts));
            }

            var received = signature!.Trim();

            var expectedHex = Encoding.ASCII.GetBytes(CryptoHelpers.ToHexLower(digest));
            var receivedHex = Encoding.ASCII.GetBytes(AsciiLower(received));
            var ok = CryptoHelpers.FixedTimeEquals(receivedHex, expectedHex);
            ok |= CryptoHelpers.FixedTimeEquals(Base64OrEmpty(received), digest);
            if (!ok)
            {
                throw new BizgoWebhookVerificationException("서명이 일치하지 않습니다");
            }
        }

        internal static string? Header(IEnumerable<KeyValuePair<string, string>> headers, string name)
        {
            foreach (var entry in headers)
            {
                if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }

            return null;
        }

        private static double TimestampSeconds(string timestamp)
        {
            // 1-16 ASCII digits (SDK-DESIGN §12.7): epoch ms has 13, so 16 leaves room without overflow tricks
            if (timestamp.Length == 0 || timestamp.Length > 16)
            {
                throw new BizgoWebhookVerificationException("X-IB-Timestamp가 없거나 형식이 맞지 않습니다");
            }

            foreach (var c in timestamp)
            {
                if (c < '0' || c > '9')
                {
                    throw new BizgoWebhookVerificationException("X-IB-Timestamp가 숫자가 아닙니다");
                }
            }

            var value = long.Parse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture);
            return timestamp.Length >= 13 ? value / 1000.0 : value; // epoch ms (documented example) or seconds
        }

        private static byte[] Base64OrEmpty(string value)
        {
            try
            {
                return Convert.FromBase64String(value);
            }
            catch (FormatException)
            {
                return Array.Empty<byte>();
            }
        }

        private static string AsciiLower(string value)
        {
            var chars = value.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                var c = chars[i];
                if (c >= 'A' && c <= 'Z')
                {
                    chars[i] = (char)(c + 32);
                }
                else if (c > 0x7E)
                {
                    chars[i] = '?';
                }
            }

            return new string(chars);
        }

        private static byte[] Utf8(string body)
        {
            if (body is null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if (body.Length > MaxBodyBytes)
            {
                throw new BizgoWebhookVerificationException("웹훅 본문이 너무 큽니다");
            }

            return Encoding.UTF8.GetBytes(body);
        }

        private static T Parse<T>(byte[] body, JsonTypeInfo<T> typeInfo)
            where T : class
        {
            if (body is null)
            {
                throw new ArgumentNullException(nameof(body));
            }

            if (body.Length > MaxBodyBytes)
            {
                throw new BizgoWebhookVerificationException("웹훅 본문이 너무 큽니다");
            }

            try
            {
                using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = Transport.MaxJsonDepth });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    throw new BizgoWebhookVerificationException("웹훅 본문이 JSON 객체가 아닙니다");
                }

                return document.RootElement.Deserialize(typeInfo) ?? throw new BizgoWebhookVerificationException("웹훅 본문이 비어 있습니다");
            }
            catch (Exception e) when (e is JsonException || e is InvalidOperationException || e is NotSupportedException || e is FormatException || e is OverflowException || e is ArgumentException)
            {
                // malformed JSON, nesting deeper than 64, or a field of the wrong shape: answer 4xx (SDK-DESIGN §12.7)
                throw new BizgoWebhookVerificationException("웹훅 본문이 JSON이 아니거나 형식이 다릅니다");
            }
        }
    }

    /// <summary>
    /// Verifies and parses webhook requests with one secret. Thread-safe; create one per secret and reuse it.
    /// </summary>
    /// <example>
    /// <code>
    /// var receiver = new WebhookReceiver(Environment.GetEnvironmentVariable("BIZGO_WEBHOOK_SECRET")!);
    /// // ASP.NET Core minimal API:
    /// app.MapPost("/bizgo/report", async (HttpRequest request) =>
    /// {
    ///     using var buffer = new MemoryStream();
    ///     await request.Body.CopyToAsync(buffer);
    ///     try
    ///     {
    ///         var report = receiver.Report(name => request.Headers[name], buffer.ToArray());
    ///         // enqueue; deduplicate by report.MsgKey
    ///         return Results.Text(Webhooks.AckJson(report.MsgKey!), "application/json");
    ///     }
    ///     catch (BizgoWebhookVerificationException)
    ///     {
    ///         return Results.StatusCode(401);
    ///     }
    /// });
    /// </code>
    /// </example>
    public sealed partial class WebhookReceiver
    {
        private readonly byte[] _secret;
        private readonly TimeSpan _tolerance;
        private readonly Func<DateTimeOffset> _clock;

        /// <summary>Creates a receiver.</summary>
        /// <param name="secret">Webhook secret (request it from Bizgo). Read it from an environment variable or a vault.</param>
        /// <param name="tolerance">Maximum timestamp age. Default <see cref="Webhooks.DefaultTolerance"/>.</param>
        /// <exception cref="BizgoConfigurationException"><paramref name="secret"/> is empty or whitespace, or <paramref name="tolerance"/> is not positive.</exception>
        public WebhookReceiver(string secret, TimeSpan? tolerance = null)
            : this(secret, new WebhookReceiverOptions { Tolerance = tolerance ?? Webhooks.DefaultTolerance })
        {
        }

        /// <summary>Creates a receiver.</summary>
        /// <param name="secret">Webhook secret (request it from Bizgo). Read it from an environment variable or a vault.</param>
        /// <param name="options">Receiver settings (timestamp tolerance).</param>
        /// <exception cref="BizgoConfigurationException"><paramref name="secret"/> is empty or whitespace, or the tolerance is not positive.</exception>
        public WebhookReceiver(string secret, WebhookReceiverOptions options)
            : this(options, secret, () => DateTimeOffset.UtcNow)
        {
        }

        internal WebhookReceiver(string secret, TimeSpan? tolerance, Func<DateTimeOffset> clock)
            : this(new WebhookReceiverOptions { Tolerance = tolerance ?? Webhooks.DefaultTolerance }, secret, clock)
        {
        }

        internal WebhookReceiver(WebhookReceiverOptions options, string secret, Func<DateTimeOffset> clock)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            _secret = Webhooks.Secret(secret);
            _tolerance = Webhooks.CheckTolerance(options.Tolerance);
            _clock = clock;
        }

        /// <summary>Verifies the signature headers of a request.</summary>
        /// <param name="header">Returns a header value by name (for example <c>name =&gt; request.Headers[name]</c>), or null.</param>
        /// <exception cref="BizgoWebhookVerificationException">Verification failed.</exception>
        public void Verify(Func<string, string?> header)
        {
            if (header is null)
            {
                throw new ArgumentNullException(nameof(header));
            }

            Webhooks.Verify(_secret, header(Webhooks.TimestampHeader), header(Webhooks.SignatureHeader), _tolerance, _clock());
        }

        /// <summary>Verifies the signature headers of a request (header names are case-insensitive).</summary>
        /// <param name="headers">Request headers.</param>
        /// <exception cref="BizgoWebhookVerificationException">Verification failed.</exception>
        public void Verify(IEnumerable<KeyValuePair<string, string>> headers)
        {
            if (headers is null)
            {
                throw new ArgumentNullException(nameof(headers));
            }

            Verify(name => Webhooks.Header(headers, name));
        }

        /// <summary>Verifies, then parses a delivery report webhook.</summary>
        /// <param name="header">Header lookup.</param>
        /// <param name="body">Raw body.</param>
        /// <returns>The report.</returns>
        /// <exception cref="BizgoWebhookVerificationException">Verification or parsing failed; answer with HTTP 401.</exception>
        public ReportWebhookPayload Report(Func<string, string?> header, byte[] body)
        {
            Verify(header);
            return Webhooks.ParseReport(body);
        }

        /// <summary>Verifies, then parses a delivery report webhook.</summary>
        /// <param name="headers">Request headers (case-insensitive names).</param>
        /// <param name="body">Raw body.</param>
        /// <returns>The report.</returns>
        public ReportWebhookPayload Report(IEnumerable<KeyValuePair<string, string>> headers, byte[] body)
        {
            Verify(headers);
            return Webhooks.ParseReport(body);
        }

        /// <summary>Verifies, then parses a delivery report webhook.</summary>
        /// <param name="headers">Request headers (case-insensitive names).</param>
        /// <param name="body">Body text.</param>
        /// <returns>The report.</returns>
        public ReportWebhookPayload Report(IEnumerable<KeyValuePair<string, string>> headers, string body)
        {
            Verify(headers);
            return Webhooks.ParseReport(body);
        }

        /// <summary>Verifies, then parses an MO webhook.</summary>
        /// <param name="header">Header lookup.</param>
        /// <param name="body">Raw body.</param>
        /// <returns>The MO message.</returns>
        public MoWebhookPayload Mo(Func<string, string?> header, byte[] body)
        {
            Verify(header);
            return Webhooks.ParseMo(body);
        }

        /// <summary>Verifies, then parses an MO webhook.</summary>
        /// <param name="headers">Request headers (case-insensitive names).</param>
        /// <param name="body">Raw body.</param>
        /// <returns>The MO message.</returns>
        public MoWebhookPayload Mo(IEnumerable<KeyValuePair<string, string>> headers, byte[] body)
        {
            Verify(headers);
            return Webhooks.ParseMo(body);
        }

        /// <summary>Verifies, then parses an MO webhook.</summary>
        /// <param name="headers">Request headers (case-insensitive names).</param>
        /// <param name="body">Body text.</param>
        /// <returns>The MO message.</returns>
        public MoWebhookPayload Mo(IEnumerable<KeyValuePair<string, string>> headers, string body)
        {
            Verify(headers);
            return Webhooks.ParseMo(body);
        }

        /// <summary>See <see cref="Webhooks.CounselAck"/>.</summary>
        /// <returns>The acknowledgement.</returns>
        public static CounselWebhookAck CounselAck() => Webhooks.CounselAck();

        /// <summary>See <see cref="Webhooks.Ack(string)"/>.</summary>
        /// <param name="msgKey">Received message key.</param>
        /// <returns>The acknowledgement.</returns>
        public static WebhookAck Ack(string msgKey) => Webhooks.Ack(msgKey);

        /// <inheritdoc/>
        public override string ToString() => "WebhookReceiver(Tolerance=" + (_tolerance == Webhooks.NoTolerance ? "off" : _tolerance.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s") + ")";
    }

    /// <summary>Settings of a <see cref="WebhookReceiver"/>.</summary>
    public sealed class WebhookReceiverOptions
    {
        /// <summary>Maximum timestamp age (either direction). Default <see cref="Webhooks.DefaultTolerance"/>; must be positive, or <see cref="Webhooks.NoTolerance"/>.</summary>
        public TimeSpan Tolerance { get; set; } = Webhooks.DefaultTolerance;

        /// <inheritdoc/>
        public override string ToString() => "WebhookReceiverOptions(Tolerance=" + Tolerance.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s)";
    }
}
