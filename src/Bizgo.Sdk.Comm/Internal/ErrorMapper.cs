using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Bizgo.Internal
{
    /// <summary>
    /// Picks the exception class. Gateway (<c>common.authCode</c>) and service (<c>data.code</c>) codes use separate
    /// tables because the same code means different things (service <c>A401</c> is an invalid <c>paymentCode</c>, not an
    /// authentication failure; service <c>A301</c> is a duplicate idempotency key, gateway <c>A301</c> is "moved").
    /// </summary>
    internal static class ErrorMapper
    {
        internal const string DuplicateAfterRetryMessage =
            "이전 시도가 이미 접수된 것으로 보입니다(같은 idempotencyKey). 메시지는 다시 발송되지 않았으며, 접수 결과는 상태 조회 API로 확인하세요.";

        private enum Kind
        {
            Generic,
            BadRequest,
            Authentication,
            PermissionDenied,
            NotFound,
            Duplicate,
            RateLimit,
            InternalServer,
        }

        private static readonly Dictionary<string, Kind> Gateway = new Dictionary<string, Kind>(StringComparer.Ordinal)
        {
            ["A400"] = Kind.BadRequest,
            ["A401"] = Kind.Authentication,
            ["A403"] = Kind.PermissionDenied,
            ["A404"] = Kind.NotFound,
        };

        private static readonly Dictionary<string, Kind> Service = new Dictionary<string, Kind>(StringComparer.Ordinal)
        {
            ["A001"] = Kind.Authentication,
            ["A002"] = Kind.Authentication,
            ["A100"] = Kind.Authentication,
            ["A110"] = Kind.PermissionDenied,
            ["A111"] = Kind.PermissionDenied,
            ["A020"] = Kind.RateLimit,
            ["A301"] = Kind.Duplicate,
        };

        internal static BizgoApiException Create(
            int httpStatus,
            string? code,
            string message,
            BizgoErrorLayer layer,
            string? trackingId,
            JsonElement? body,
            TimeSpan? retryAfter)
        {
            switch (Classify(httpStatus, code, layer))
            {
                case Kind.BadRequest:
                    return new BizgoBadRequestException(message, httpStatus, code, layer, trackingId, body);
                case Kind.Authentication:
                    return new BizgoAuthenticationException(message, httpStatus, code, layer, trackingId, body);
                case Kind.PermissionDenied:
                    return new BizgoPermissionDeniedException(message, httpStatus, code, layer, trackingId, body);
                case Kind.NotFound:
                    return new BizgoNotFoundException(message, httpStatus, code, layer, trackingId, body);
                case Kind.Duplicate:
                    return new BizgoDuplicateRequestException(message, httpStatus, code, layer, trackingId, body);
                case Kind.RateLimit:
                    return new BizgoRateLimitException(message, httpStatus, code, layer, trackingId, body, retryAfter);
                case Kind.InternalServer:
                    return new BizgoInternalServerException(message, httpStatus, code, layer, trackingId, body);
                default:
                    return new BizgoApiException(message, httpStatus, code, layer, trackingId, body);
            }
        }

        /// <summary>The same error with the "previous attempt was already accepted" explanation and <c>AlreadyAccepted</c>.</summary>
        internal static BizgoDuplicateRequestException AfterRetry(BizgoDuplicateRequestException error)
        {
            return new BizgoDuplicateRequestException(DuplicateAfterRetryMessage, error.HttpStatus, error.Code, error.Layer, error.TrackingId, error.Body, alreadyAccepted: true);
        }

        private static Kind Classify(int httpStatus, string? code, BizgoErrorLayer layer)
        {
            var table = layer == BizgoErrorLayer.Gateway ? Gateway : Service;
            if (code is not null && table.TryGetValue(code, out var kind))
            {
                return kind;
            }

            if (layer == BizgoErrorLayer.Service && httpStatus < 400 && code is not null && ServiceCodes.TryGet(code, out var known) && known.HttpStatus > 0)
            {
                // failure reported inside an HTTP 200: use the status documented for the code
                httpStatus = known.HttpStatus;
            }

            switch (httpStatus)
            {
                case 400:
                    return Kind.BadRequest;
                case 401:
                    return Kind.Authentication;
                case 403:
                    return Kind.PermissionDenied;
                case 404:
                    return Kind.NotFound;
                case 429:
                    return Kind.RateLimit;
            }

            if (httpStatus >= 500)
            {
                return Kind.InternalServer;
            }

            return httpStatus >= 400 ? Kind.BadRequest : Kind.Generic;
        }
    }
}
