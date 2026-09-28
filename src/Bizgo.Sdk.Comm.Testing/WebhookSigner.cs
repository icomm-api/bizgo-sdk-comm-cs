using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Bizgo.Testing
{
    /// <summary>A webhook request built for tests: headers and body.</summary>
    public sealed class SignedWebhook
    {
        internal SignedWebhook(IReadOnlyDictionary<string, string> headers, string body)
        {
            Headers = headers;
            Body = body;
        }

        /// <summary><c>X-IB-Timestamp</c>, <c>X-IB-Signature</c> and <c>Content-Type</c>.</summary>
        public IReadOnlyDictionary<string, string> Headers { get; }

        /// <summary>JSON body text.</summary>
        public string Body { get; }

        /// <summary>UTF-8 body bytes.</summary>
        public byte[] BodyBytes => Encoding.UTF8.GetBytes(Body);

        /// <summary>Header lookup for <c>WebhookReceiver</c> methods that take a <c>Func&lt;string, string?&gt;</c> (case-insensitive).</summary>
        /// <param name="name">Header name.</param>
        /// <returns>The value, or null.</returns>
        public string? Header(string name)
        {
            foreach (var entry in Headers)
            {
                if (string.Equals(entry.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return entry.Value;
                }
            }

            return null;
        }

        /// <inheritdoc/>
        public override string ToString() => "SignedWebhook(" + Encoding.UTF8.GetByteCount(Body).ToString(CultureInfo.InvariantCulture) + " bytes)";
    }

    /// <summary>Signs webhook requests exactly like Bizgo (SDK-DESIGN §7): hex <c>HMAC-SHA256(secret, X-IB-Timestamp)</c>, epoch milliseconds.</summary>
    public static class WebhookSigner
    {
        /// <summary>Builds a signed webhook request for a <see cref="WebhookReceiver"/> test.</summary>
        /// <param name="secret">The same test secret you give the receiver (never a real one).</param>
        /// <param name="payloadJson">Body JSON, for example <c>{"msgKey":"KEY001"}</c>.</param>
        /// <param name="timestamp">Send time; default now.</param>
        /// <returns>Headers and body.</returns>
        public static SignedWebhook Sign(string secret, string payloadJson, DateTimeOffset? timestamp = null)
        {
            if (string.IsNullOrEmpty(secret))
            {
                throw new ArgumentException("secret is empty", nameof(secret));
            }

            if (payloadJson is null)
            {
                throw new ArgumentNullException(nameof(payloadJson));
            }

            var ts = (timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
            return new SignedWebhook(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-IB-Timestamp"] = ts,
                    ["X-IB-Signature"] = Signature(secret, ts),
                    ["Content-Type"] = "application/json",
                },
                payloadJson);
        }

        /// <summary>A counsel (상담톡) webhook request with plain headers: counsel webhooks carry no signature (signatures apply only to report and MO webhooks).</summary>
        /// <param name="payloadJson">Body JSON.</param>
        /// <returns>Headers (content type only) and body.</returns>
        public static SignedWebhook WithoutSignature(string payloadJson)
        {
            if (payloadJson is null)
            {
                throw new ArgumentNullException(nameof(payloadJson));
            }

            return new SignedWebhook(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "application/json" }, payloadJson);
        }

        /// <summary>Lower-case hex HMAC-SHA256 of <paramref name="timestamp"/> with <paramref name="secret"/>.</summary>
        /// <param name="secret">Secret.</param>
        /// <param name="timestamp">Timestamp header value.</param>
        /// <returns>Signature header value.</returns>
        public static string Signature(string secret, string timestamp)
        {
            if (secret is null)
            {
                throw new ArgumentNullException(nameof(secret));
            }

            if (timestamp is null)
            {
                throw new ArgumentNullException(nameof(timestamp));
            }

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var digest = hmac.ComputeHash(Encoding.ASCII.GetBytes(timestamp));
            var sb = new StringBuilder(digest.Length * 2);
            foreach (var b in digest)
            {
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }

            return sb.ToString();
        }
    }
}
