using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Bizgo.Internal
{
    /// <summary>JSON round trip of <see cref="BizgoApiException"/> (SDK-DESIGN §12.10); written by hand so no reflection is needed.</summary>
    internal static class ExceptionJson
    {
        internal static string Write(BizgoApiException error)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WriteString("type", error.GetType().Name);
                writer.WriteNumber("httpStatus", error.HttpStatus);
                if (error.Code is not null)
                {
                    writer.WriteString("code", error.Code);
                }

                writer.WriteString("layer", error.Layer == BizgoErrorLayer.Gateway ? "gateway" : "service");
                writer.WriteString("serverMessage", error.ServerMessage);
                if (error.TrackingId is not null)
                {
                    writer.WriteString("trackingId", error.TrackingId);
                }

                if (error is BizgoRateLimitException rate && rate.RetryAfter is not null)
                {
                    writer.WriteNumber("retryAfterSeconds", rate.RetryAfter.Value.TotalSeconds);
                }

                if (error is BizgoDuplicateRequestException duplicate)
                {
                    writer.WriteBoolean("alreadyAccepted", duplicate.AlreadyAccepted);
                }

                if (error.Body is not null)
                {
                    writer.WritePropertyName("body");
                    error.Body.Value.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        internal static BizgoApiException Read(string json)
        {
            if (json is null)
            {
                throw new ArgumentNullException(nameof(json));
            }

            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = Transport.MaxJsonDepth });
                var root = document.RootElement;
                var type = root.GetProperty("type").GetString();
                var status = root.GetProperty("httpStatus").GetInt32();
                var code = root.TryGetProperty("code", out var c) ? c.GetString() : null;
                var layer = root.GetProperty("layer").GetString() == "gateway" ? BizgoErrorLayer.Gateway : BizgoErrorLayer.Service;
                var message = root.GetProperty("serverMessage").GetString() ?? string.Empty;
                var trackingId = root.TryGetProperty("trackingId", out var t) ? t.GetString() : null;
                JsonElement? body = root.TryGetProperty("body", out var b) ? b.Clone() : null;
                TimeSpan? retryAfter = root.TryGetProperty("retryAfterSeconds", out var r) ? TimeSpan.FromSeconds(r.GetDouble()) : null;
                var accepted = root.TryGetProperty("alreadyAccepted", out var a) && a.GetBoolean();
                switch (type)
                {
                    case nameof(BizgoBadRequestException):
                        return new BizgoBadRequestException(message, status, code, layer, trackingId, body);
                    case nameof(BizgoAuthenticationException):
                        return new BizgoAuthenticationException(message, status, code, layer, trackingId, body);
                    case nameof(BizgoPermissionDeniedException):
                        return new BizgoPermissionDeniedException(message, status, code, layer, trackingId, body);
                    case nameof(BizgoNotFoundException):
                        return new BizgoNotFoundException(message, status, code, layer, trackingId, body);
                    case nameof(BizgoDuplicateRequestException):
                        return new BizgoDuplicateRequestException(message, status, code, layer, trackingId, body, accepted);
                    case nameof(BizgoRateLimitException):
                        return new BizgoRateLimitException(message, status, code, layer, trackingId, body, retryAfter);
                    case nameof(BizgoInternalServerException):
                        return new BizgoInternalServerException(message, status, code, layer, trackingId, body);
                    case nameof(BizgoApiException):
                        return new BizgoApiException(message, status, code, layer, trackingId, body);
                    default:
                        throw new ArgumentException("알 수 없는 오류 형식입니다", nameof(json));
                }
            }
            catch (Exception e) when (e is JsonException || e is InvalidOperationException || e is FormatException || e is OverflowException || e is ArgumentOutOfRangeException || e is System.Collections.Generic.KeyNotFoundException)
            {
                throw new ArgumentException("BizgoApiException.ToJson()으로 만든 JSON이 아닙니다", nameof(json));
            }
        }
    }
}
