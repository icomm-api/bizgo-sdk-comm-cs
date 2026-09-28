using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Bizgo.Internal
{
    /// <summary>URL path segments.</summary>
    internal static class PathSegment
    {
        /// <summary>Percent-encodes one path segment (<c>/</c> and <c>?</c> included) and rejects values that would change the path.</summary>
        /// <exception cref="BizgoValidationException">The value is empty, <c>.</c> or <c>..</c>.</exception>
        internal static string Escape(string? value, string name)
        {
            if (string.IsNullOrEmpty(value))
            {
                throw new BizgoValidationException(name, "빈 값은 경로에 쓸 수 없습니다");
            }

            if (value == "." || value == "..")
            {
                throw new BizgoValidationException(name, "'.'이나 '..'은 경로에 쓸 수 없습니다");
            }

            return Uri.EscapeDataString(value);
        }
    }

    /// <summary>Builds an escaped query string, skipping null values.</summary>
    internal sealed class QueryBuilder
    {
        private readonly List<string> _parts = new List<string>();

        internal QueryBuilder Add(string name, string? value)
        {
            if (value is not null)
            {
                _parts.Add(Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value));
            }

            return this;
        }

        internal QueryBuilder Add(string name, long? value)
        {
            return value is null ? this : Add(name, value.Value.ToString(CultureInfo.InvariantCulture));
        }

        internal QueryBuilder Add(string name, bool? value)
        {
            return value is null ? this : Add(name, value.Value ? "true" : "false");
        }

        /// <summary>A string array: comma-separated (<c>explode: false</c>) or one parameter per value (<c>explode: true</c>).</summary>
        internal QueryBuilder Add(string name, IEnumerable<string>? values, bool explode)
        {
            if (values is null)
            {
                return this;
            }

            if (!explode)
            {
                return Add(name, string.Join(",", values));
            }

            foreach (var value in values)
            {
                Add(name, value);
            }

            return this;
        }

        internal string? Build() => _parts.Count == 0 ? null : string.Join("&", _parts);
    }

    /// <summary>JSON request bodies.</summary>
    internal static class JsonBody
    {
        /// <summary>Serializes once; each call of the returned factory creates fresh content with the same bytes (for retries).</summary>
        internal static Func<HttpContent> Create<T>(T value, JsonTypeInfo<T> typeInfo)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
            return () =>
            {
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                return content;
            };
        }

        /// <summary>
        /// Like <see cref="Create{T}(T, JsonTypeInfo{T})"/> for a body with <c>idempotencyKey</c> and <c>idempotencyTtl</c>: when the key is
        /// set and the TTL is null, the serialized body gets <c>idempotencyTtl</c> = <see cref="BizgoClient.DefaultIdempotencyTtl"/>
        /// (Bizgo rejects a key without a TTL with A309). An explicit TTL, including 0, is kept; without a key nothing is added.
        /// The value itself is never changed.
        /// </summary>
        internal static Func<HttpContent> Create<T>(T value, JsonTypeInfo<T> typeInfo, string? idempotencyKey, int? idempotencyTtl)
        {
            if (Idempotency.EffectiveTtl(idempotencyKey, idempotencyTtl) == idempotencyTtl)
            {
                return Create(value, typeInfo);
            }

            var node = JsonSerializer.SerializeToNode(value, typeInfo) as JsonObject
                ?? throw new InvalidOperationException("request body is not a JSON object");
            if (!node.ContainsKey(Idempotency.TtlField))
            {
                node[Idempotency.TtlField] = BizgoClient.DefaultIdempotencyTtl;
            }

            var bytes = Encoding.UTF8.GetBytes(node.ToJsonString(typeInfo.Options));
            return () =>
            {
                var content = new ByteArrayContent(bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                return content;
            };
        }
    }

    /// <summary>The <c>idempotencyTtl</c> default rule shared by every send path (SDK-DESIGN: key without TTL gets 86400).</summary>
    internal static class Idempotency
    {
        internal const string TtlField = "idempotencyTtl";

        /// <summary>The TTL to send: <paramref name="ttl"/> when set (0 included), the default when only a key is set, otherwise null.</summary>
        internal static int? EffectiveTtl(string? key, int? ttl) => ttl ?? (key is null ? null : BizgoClient.DefaultIdempotencyTtl);
    }

    /// <summary>Korea Standard Time (UTC+9, no daylight saving time).</summary>
    internal static class Kst
    {
        internal static readonly TimeSpan Offset = TimeSpan.FromHours(9);

        /// <summary>Converts an absolute time to KST.</summary>
        internal static DateTimeOffset From(DateTimeOffset value) => value.ToOffset(Offset);

        /// <summary>
        /// <see cref="DateTimeKind.Utc"/> and <see cref="DateTimeKind.Local"/> values are converted to KST;
        /// <see cref="DateTimeKind.Unspecified"/> values are taken as KST wall-clock time.
        /// </summary>
        internal static DateTimeOffset From(DateTime value)
        {
            if (value.Kind == DateTimeKind.Unspecified)
            {
                return new DateTimeOffset(value, Offset);
            }

            return new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero).ToOffset(Offset);
        }
    }
}
