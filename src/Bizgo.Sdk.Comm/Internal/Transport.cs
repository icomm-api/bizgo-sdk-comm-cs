using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Bizgo.Internal
{
    /// <summary>Which failures may be retried for a request.</summary>
    internal enum RetryPolicy
    {
        /// <summary>Read-only or idempotent calls: retry on 429, 500/502/503/504 and connection errors.</summary>
        Safe,

        /// <summary>
        /// Calls that could create a duplicate (send without idempotency key, uploads): retry only on 429, which the gateway
        /// returns before the request is processed.
        /// </summary>
        RateLimitOnly,
    }

    /// <summary>
    /// HTTP transport: headers, rate limiting, retries, envelope parsing, error mapping, hooks and tracing.
    /// Nothing here logs or throws request bodies, header values or query strings: they can contain the API key and
    /// phone numbers. HTTP library exceptions are not chained (their messages can contain the full URL).
    /// </summary>
    internal sealed class Transport : IDisposable
    {
        /// <summary>Largest (decompressed) response body the SDK reads (SDK-DESIGN §12.8).</summary>
        internal const int MaxResponseBytes = 16 * 1024 * 1024;

        /// <summary>Largest JSON nesting depth accepted in responses and webhooks (SDK-DESIGN §12.8).</summary>
        internal const int MaxJsonDepth = 64;

        internal const string MayHaveBeenAccepted = " 요청은 접수됐을 수 있으니 상태 조회(client.Messages.StatusAsync 등)로 확인하세요.";

        private const double MaxBackoffSeconds = 8.0;
        private const double MaxRetryAfterSeconds = 60.0;

        private static readonly JsonDocumentOptions DocumentOptions = new JsonDocumentOptions { MaxDepth = MaxJsonDepth };

        private readonly ClientConfig _config;
        private readonly HttpClient _http;
        private int _disposed;

        internal Transport(ClientConfig config, HttpMessageHandler? handler)
        {
            _config = config;

            // The SDK always owns the HttpClient: its timeout is Config.Timeout per attempt, and no default headers exist.
            if (handler is not null)
            {
                CheckHandler(handler);
                _http = new HttpClient(handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            }
            else
            {
                _http = new HttpClient(CreateHandler(config), disposeHandler: true) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            }

            if (config.RateLimit is not null)
            {
                RateLimiter = new RateLimiter(config.RateLimit);
            }
        }

        /// <summary>Waits between attempts. Replaced in tests.</summary>
        internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = (delay, ct) => Task.Delay(delay, ct);

        /// <summary>Jitter factor source (0.75..1.25). Replaced in tests.</summary>
        internal Func<double> Jitter { get; set; } = ThreadSafeRandom.Jitter;

        /// <summary>Client-side rate limiter, or null when turned off. Replaced in tests.</summary>
        internal RateLimiter? RateLimiter { get; set; }

        internal ClientConfig Config => _config;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _http.Dispose();
            }
        }

        /// <summary>Sends a request and returns the parsed body, or throws the matching <see cref="BizgoException"/>.</summary>
        /// <param name="operation">The spec operation (rate bucket, hooks and tracing report only its template, never values).</param>
        /// <param name="path">Escaped path starting with <c>/</c>, without query. It is the only request detail that is logged.</param>
        /// <param name="query">Escaped query string without <c>?</c>, or null.</param>
        /// <param name="content">Creates the body for each attempt (bodies are buffered so retries resend the same bytes).</param>
        /// <param name="retry">Retry policy.</param>
        /// <param name="cost">Tokens of the operation's rate bucket per attempt.</param>
        /// <param name="headers">Header parameters of the operation (never logged), or null.</param>
        /// <param name="typeInfo">Response type.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        internal async Task<T> SendAsync<T>(
            BizgoOperation operation,
            string path,
            string? query,
            Func<HttpContent>? content,
            RetryPolicy retry,
            int cost,
            IReadOnlyList<KeyValuePair<string, string>>? headers,
            JsonTypeInfo<T> typeInfo,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                throw new ObjectDisposedException(nameof(BizgoClient));
            }

            var url = new Uri(_config.BaseUrl + path + (string.IsNullOrEmpty(query) ? string.Empty : "?" + query), UriKind.Absolute);
            var method = new HttpMethod(operation.HttpMethod);
            var activity = Telemetry.Start(operation);
            int? lastStatus = null;
            string? lastCode = null;
            BizgoErrorLayer? lastLayer = null;
            string? errorType = null;
            var attempt = 0;
            try
            {
                for (; ; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var last = attempt >= _config.MaxRetries;
                    var limiter = RateLimiter;
                    if (limiter is not null)
                    {
                        // before every attempt, retries included (SDK-DESIGN §11.2)
                        await limiter.WaitAsync(operation.RateBucket, cost, cancellationToken).ConfigureAwait(false);
                    }

                    Hook(new BizgoRequestEvent(operation, attempt + 1));
                    var watch = Stopwatch.StartNew();
                    Response response;
                    try
                    {
                        response = await SendOnceAsync(method, url, content, headers, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        errorType = nameof(OperationCanceledException);
                        Hook(new BizgoResponseEvent(operation, attempt + 1, null, null, null, errorType, watch.Elapsed, false));
                        throw new OperationCanceledException(cancellationToken);
                    }
                    catch (AttemptFailedException failure)
                    {
                        Log(method, path, failure.Kind, watch, attempt);
                        var willRetry = !last && retry == RetryPolicy.Safe;
                        var error = failure.ToPublic(_config.Timeout);
                        Hook(new BizgoResponseEvent(operation, attempt + 1, null, null, null, error.GetType().Name, watch.Elapsed, willRetry));
                        if (!willRetry)
                        {
                            errorType = error.GetType().Name;
                            throw error;
                        }

                        await Delay(Backoff(attempt, null), cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    catch (BizgoException error)
                    {
                        // redirects, foreign origins and oversized bodies: never retried (SDK-DESIGN §12.17)
                        errorType = error.GetType().Name;
                        lastStatus = (error as BizgoInvalidResponseException)?.HttpStatus;
                        Log(method, path, lastStatus?.ToString(CultureInfo.InvariantCulture) ?? errorType, watch, attempt);
                        Hook(new BizgoResponseEvent(operation, attempt + 1, lastStatus, null, null, errorType, watch.Elapsed, false));
                        throw;
                    }

                    lastStatus = response.Status;
                    Log(method, path, response.Status.ToString(CultureInfo.InvariantCulture), watch, attempt);
                    if (!last && ShouldRetry(retry, response.Status))
                    {
                        Hook(new BizgoResponseEvent(operation, attempt + 1, response.Status, null, null, "HTTP " + response.Status.ToString(CultureInfo.InvariantCulture), watch.Elapsed, true));
                        await Delay(Backoff(attempt, response.RetryAfter), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        var result = Parse(response, typeInfo, operation.RateBucket == BizgoRateBucket.Send);
                        lastCode = "A000";
                        Hook(new BizgoResponseEvent(operation, attempt + 1, response.Status, lastCode, null, null, watch.Elapsed, false));
                        return result;
                    }
                    catch (BizgoException error)
                    {
                        var api = error as BizgoApiException;
                        lastCode = api?.Code;
                        lastLayer = api?.Layer;
                        errorType = error.GetType().Name;
                        Hook(new BizgoResponseEvent(operation, attempt + 1, response.Status, lastCode, lastLayer, errorType, watch.Elapsed, false));
                        if (error is BizgoDuplicateRequestException duplicate && attempt > 0)
                        {
                            // an earlier attempt (timeout, 5xx or 429) may have been accepted (SDK-DESIGN §12.4)
                            throw ErrorMapper.AfterRetry(duplicate);
                        }

                        throw;
                    }
                }
            }
            catch (Exception e) when (errorType is null)
            {
                errorType = e.GetType().Name;
                throw;
            }
            finally
            {
                Telemetry.Stop(activity, lastStatus, lastCode, lastLayer, attempt, errorType);
            }
        }

        internal static bool ShouldRetry(RetryPolicy policy, int status)
        {
            if (status == 429)
            {
                return true;
            }

            return policy == RetryPolicy.Safe && (status == 500 || status == 502 || status == 503 || status == 504);
        }

        internal TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
        {
            if (retryAfter is not null)
            {
                return retryAfter.Value;
            }

            var seconds = Math.Min(0.5 * Math.Pow(2, attempt), MaxBackoffSeconds) * Jitter();
            return TimeSpan.FromSeconds(seconds);
        }

        internal static TimeSpan? ParseRetryAfter(HttpResponseMessage response)
        {
            if (!response.Headers.TryGetValues("Retry-After", out var values))
            {
                return null;
            }

            return ParseRetryAfter(values.FirstOrDefault());
        }

        /// <summary>Only finite values from 0 are used (capped at 60 s); anything else falls back to the default backoff (SDK-DESIGN §12.9).</summary>
        internal static TimeSpan? ParseRetryAfter(string? value)
        {
            var raw = value?.Trim();
            if (string.IsNullOrEmpty(raw))
            {
                return null;
            }

            double seconds;
            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                seconds = parsed;
            }
            else if (DateTimeOffset.TryParseExact(raw, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
            {
                seconds = Math.Max((date - DateTimeOffset.UtcNow).TotalSeconds, 0.0);
            }
            else
            {
                return null;
            }

            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            {
                return null;
            }

            return TimeSpan.FromSeconds(Math.Min(seconds, MaxRetryAfterSeconds));
        }

        internal static T Parse<T>(Response response, JsonTypeInfo<T> typeInfo, bool send)
        {
            var status = response.Status;
            var statusText = " (HTTP " + status.ToString(CultureInfo.InvariantCulture) + ")";
            JsonDocument? document = null;
            try
            {
                try
                {
                    document = response.Body.Length == 0 ? null : JsonDocument.Parse(response.Body, DocumentOptions);
                }
                catch (JsonException)
                {
                    document = null;
                }

                if (document is null || document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    if (status >= 400)
                    {
                        throw ErrorMapper.Create(status, null, "응답 본문이 JSON이 아닙니다" + statusText, BizgoErrorLayer.Gateway, null, null, response.RetryAfter);
                    }

                    throw new BizgoInvalidResponseException("응답 본문이 JSON 객체가 아닙니다" + statusText + "." + (send ? MayHaveBeenAccepted : string.Empty), status);
                }

                var root = document.RootElement;
                var common = Property(root, "common");
                var data = Property(root, "data");
                if (common?.ValueKind != JsonValueKind.Object)
                {
                    common = null;
                }

                if (data?.ValueKind != JsonValueKind.Object)
                {
                    data = null;
                }

                var trackingId = common is null ? null : Code(Property(common.Value, "infobankTrId"));
                var authCode = common is null ? null : Code(Property(common.Value, "authCode"));
                var serviceCode = data is null ? null : Code(Property(data.Value, "code"));

                if (authCode is not null && authCode != "A000")
                {
                    throw ErrorMapper.Create(status, authCode, Text(Property(common!.Value, "authResult")), BizgoErrorLayer.Gateway, trackingId, root.Clone(), response.RetryAfter);
                }

                if (serviceCode is not null && serviceCode != "A000")
                {
                    throw ErrorMapper.Create(status, serviceCode, Text(Property(data!.Value, "result")), BizgoErrorLayer.Service, trackingId, root.Clone(), response.RetryAfter);
                }

                if (status >= 400)
                {
                    throw ErrorMapper.Create(status, null, "요청이 실패했습니다", BizgoErrorLayer.Gateway, trackingId, root.Clone(), response.RetryAfter);
                }

                T? result;
                try
                {
                    result = root.Deserialize(typeInfo);
                }
                catch (Exception e) when (e is JsonException || e is InvalidOperationException || e is NotSupportedException || e is FormatException || e is OverflowException)
                {
                    // the serializer message can quote response values: not used, not chained (SDK-DESIGN §12.1)
                    throw new BizgoInvalidResponseException("응답 본문이 문서화된 형식과 다릅니다" + statusText + "." + (send ? MayHaveBeenAccepted : string.Empty), status, trackingId, root.Clone());
                }

                return result ?? throw new BizgoInvalidResponseException("응답 본문이 비어 있습니다" + statusText + "." + (send ? MayHaveBeenAccepted : string.Empty), status, trackingId, root.Clone());
            }
            finally
            {
                document?.Dispose();
            }
        }

#if NET5_0_OR_GREATER
        private static SocketsHttpHandler CreateHandler(ClientConfig config)
        {
            return new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = config.ConnectTimeout,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
            };
        }
#else
        private static HttpClientHandler CreateHandler(ClientConfig config)
        {
            _ = config;
            return new HttpClientHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                UseCookies = false,
            };
        }
#endif

        /// <summary>
        /// Checks a user handler and its <see cref="DelegatingHandler"/> chain (SDK-DESIGN §12.6):
        /// <list type="bullet">
        /// <item><description>no own authentication (<c>Credentials</c>, <c>UseDefaultCredentials</c>, <c>PreAuthenticate</c>): after a 401 the
        /// handler would resend the request, so a send could be delivered twice, with other credentials;</description></item>
        /// <item><description><c>AllowAutoRedirect = false</c>: a 307/308 would resend the body (phone numbers) to another host;</description></item>
        /// <item><description><c>UseCookies = false</c>: server cookies would be stored and sent back with every request.</description></item>
        /// </list>
        /// Other handler types cannot be inspected; the SDK still refuses responses from outside the base URL.
        /// </summary>
        private static void CheckHandler(HttpMessageHandler handler)
        {
            for (var current = handler; current is not null; current = (current as DelegatingHandler)?.InnerHandler)
            {
                bool credentials, redirects, cookies;
                switch (current)
                {
                    case HttpClientHandler h:
                        credentials = h.Credentials is not null || h.UseDefaultCredentials || h.PreAuthenticate;
                        redirects = h.AllowAutoRedirect;
                        cookies = h.UseCookies;
                        break;
#if NET5_0_OR_GREATER
                    case SocketsHttpHandler s:
                        credentials = s.Credentials is not null || s.PreAuthenticate;
                        redirects = s.AllowAutoRedirect;
                        cookies = s.UseCookies;
                        break;
#endif
                    default:
                        continue;
                }

                if (credentials)
                {
                    throw new BizgoConfigurationException("HttpMessageHandler에 Credentials/UseDefaultCredentials/PreAuthenticate를 설정하지 마세요. 인증은 API Key로만 하며, 핸들러 자체 인증은 401 뒤 요청(발송 포함)을 다른 인증 정보로 다시 보냅니다.");
                }

                if (redirects)
                {
                    throw new BizgoConfigurationException("HttpMessageHandler의 AllowAutoRedirect를 false로 설정하세요. 리다이렉트를 따르면 요청 본문(전화번호 포함)이 다른 주소로 다시 전송될 수 있습니다.");
                }

                if (cookies)
                {
                    throw new BizgoConfigurationException("HttpMessageHandler의 UseCookies를 false로 설정하세요. SDK는 쿠키를 쓰지 않으며, 서버 쿠키가 저장돼 모든 요청에 다시 붙게 됩니다.");
                }
            }
        }

        private static string Text(JsonElement? element)
        {
            if (element is null)
            {
                return string.Empty;
            }

            var value = element.Value;
            switch (value.ValueKind)
            {
                case JsonValueKind.String:
                    return value.GetString() ?? string.Empty;
                case JsonValueKind.Null:
                case JsonValueKind.Undefined:
                    return string.Empty;
                default:
                    return value.GetRawText();
            }
        }

        private static JsonElement? Property(JsonElement obj, string name)
        {
            return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) ? value : null;
        }

        private static string? Code(JsonElement? element)
        {
            if (element is null || element.Value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return Text(element);
        }

        private async Task<Response> SendOnceAsync(HttpMethod method, Uri url, Func<HttpContent>? content, IReadOnlyList<KeyValuePair<string, string>>? headers, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (_config.Timeout != System.Threading.Timeout.InfiniteTimeSpan)
            {
                timeout.CancelAfter(_config.Timeout);
            }

            using var request = new HttpRequestMessage(method, url);
            if (headers is not null)
            {
                foreach (var header in headers)
                {
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            // set last and on every request, so header parameters cannot replace them (SDK-DESIGN §2.1, §12.6)
            request.Headers.Remove("Authorization");
            request.Headers.TryAddWithoutValidation("Authorization", _config.ApiKey);
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Remove("User-Agent");
            request.Headers.TryAddWithoutValidation("User-Agent", _config.UserAgent);
            request.Headers.Remove(Identification.ClientHeaderName);
            request.Headers.TryAddWithoutValidation(Identification.ClientHeaderName, Identification.ClientHeader);
            if (content is not null)
            {
                request.Content = content();
            }

            try
            {
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                CheckOrigin(response);
                if (status >= 300 && status < 400)
                {
                    // Never follow or retry redirects: the Authorization header must not leave the base URL.
                    throw new BizgoInvalidResponseException("리다이렉트 응답(HTTP " + status.ToString(CultureInfo.InvariantCulture) + ")은 따르지 않고 재시도하지 않습니다. BaseUrl을 확인하세요.", status);
                }

                var body = await ReadBodyAsync(response, timeout.Token).ConfigureAwait(false);
                return new Response(status, body, ParseRetryAfter(response));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AttemptFailedException("Timeout", timedOut: true);
            }
            catch (HttpRequestException e)
            {
                throw new AttemptFailedException(Describe(e), timedOut: false);
            }
            catch (IOException e)
            {
                throw new AttemptFailedException(e.GetType().Name, timedOut: false);
            }
        }

        private static string Describe(HttpRequestException e)
        {
#if NET8_0_OR_GREATER
            return "HttpRequestException/" + e.HttpRequestError;
#else
            return e.InnerException is null ? "HttpRequestException" : "HttpRequestException/" + e.InnerException.GetType().Name;
#endif
        }

        private void CheckOrigin(HttpResponseMessage response)
        {
            var final = response.RequestMessage?.RequestUri;
            if (final is null)
            {
                return;
            }

            if (!string.Equals(final.Scheme, _config.BaseUri.Scheme, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(final.Host, _config.BaseUri.Host, StringComparison.OrdinalIgnoreCase)
                || final.Port != _config.BaseUri.Port)
            {
                // an injected handler followed a redirect to another origin
                // the original 3xx status is not visible once a handler followed it; report that instead of the final status
                throw new BizgoInvalidResponseException("핸들러가 리다이렉트(3xx)를 따라가 BaseUrl 밖의 응답을 받았습니다. 요청 본문이 다른 주소로 전송됐을 수 있습니다. 핸들러의 AllowAutoRedirect를 끄세요.", null);
            }
        }

        private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            var status = (int)response.StatusCode;
            var declared = response.Content.Headers.ContentLength;
            if (declared > MaxResponseBytes)
            {
                throw new BizgoInvalidResponseException("응답 본문이 너무 큽니다(16MB 초과, HTTP " + status.ToString(CultureInfo.InvariantCulture) + ")", status);
            }

#if NET5_0_OR_GREATER
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
#else
            var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
#endif
            using (stream)
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                while (true)
                {
#if NET5_0_OR_GREATER
                    var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
#else
                    var read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
#endif
                    if (read == 0)
                    {
                        break;
                    }

                    if (buffer.Length + read > MaxResponseBytes)
                    {
                        // counted after decompression (AutomaticDecompression), so a small compressed bomb is stopped too
                        throw new BizgoInvalidResponseException("응답 본문이 너무 큽니다(압축 해제 후 16MB 초과, HTTP " + status.ToString(CultureInfo.InvariantCulture) + ")", status);
                    }

                    buffer.Write(chunk, 0, read);
                }

                return buffer.ToArray();
            }
        }

        private void Hook(BizgoRequestEvent e)
        {
            try
            {
                if (e is BizgoResponseEvent end)
                {
                    _config.OnResponse?.Invoke(end);
                }
                else
                {
                    _config.OnRequest?.Invoke(e);
                }
            }
#pragma warning disable CA1031 // a failing hook must not change the API result (SDK-DESIGN §12.15)
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        private void Log(HttpMethod method, string path, string status, Stopwatch watch, int attempt)
        {
            var log = _config.Log;
            if (log is null)
            {
                return;
            }

            // path only: query strings can carry phone numbers (MO history filters)
            var line = method.Method + " " + path + " -> " + status + " (" + watch.Elapsed.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture) + " ms, attempt " + (attempt + 1).ToString(CultureInfo.InvariantCulture) + ")";
            try
            {
                log(line);
            }
#pragma warning disable CA1031 // a failing log sink must not fail the API call
            catch (Exception)
#pragma warning restore CA1031
            {
            }
        }

        internal sealed class Response
        {
            internal Response(int status, byte[] body, TimeSpan? retryAfter)
            {
                Status = status;
                Body = body;
                RetryAfter = retryAfter;
            }

            internal int Status { get; }

            internal byte[] Body { get; }

            internal TimeSpan? RetryAfter { get; }
        }

        /// <summary>One attempt failed before a response arrived. Carries no URL, header or body.</summary>
#pragma warning disable CA1032, CA1064 // private signalling exception
        private sealed class AttemptFailedException : Exception
#pragma warning restore CA1032, CA1064
        {
            internal AttemptFailedException(string kind, bool timedOut)
                : base(kind)
            {
                Kind = kind;
                TimedOut = timedOut;
            }

            internal string Kind { get; }

            internal bool TimedOut { get; }

            internal BizgoConnectionException ToPublic(TimeSpan timeout)
            {
                if (TimedOut)
                {
                    return new BizgoTimeoutException("요청 시간이 초과됐습니다 (" + timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + "초)");
                }

                return new BizgoConnectionException("서버에 연결하지 못했습니다 (" + Kind + ")");
            }
        }
    }
}
