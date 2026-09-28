using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Bizgo.Tests
{
    public class ErrorsAndSecurityTests
    {
        private const string Stats = "/api/comm/v1/message/statistics";
        private const string MoHistory = "/api/comm/v1/message/history/mo";

        [Fact]
        public async Task Gateway_auth_failure_without_data()
        {
            // observed on sandbox: invalid key -> HTTP 401, common.authCode=A401, no data
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, Stats, "{\"common\":{\"authCode\":\"A401\",\"authResult\":\"Unauthorized\",\"infobankTrId\":\"TR-1\"}}", HttpStatusCode.Unauthorized);

            var error = await Assert.ThrowsAsync<BizgoAuthenticationException>(() => t.Client.Messages.StatisticsAsync("20260101"));

            Assert.Equal(401, error.HttpStatus);
            Assert.Equal("A401", error.Code);
            Assert.Equal(BizgoErrorLayer.Gateway, error.Layer);
            Assert.Equal("TR-1", error.TrackingId);
            Assert.Null(error.Description);
            Assert.Equal("HTTP 401 | gateway code=A401 | Unauthorized | infobankTrId=TR-1", error.Message);
            Assert.DoesNotContain(Placeholders.ApiKey, error.ToString(), StringComparison.Ordinal);
            Assert.Equal(1, t.Handler.Count(HttpMethod.Get, Stats)); // 401 is not retried
        }

        [Fact]
        public async Task Service_error_code_on_http_200_uses_documented_status()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, Stats, Responses.Envelope(code: "A306"));

            var error = await Assert.ThrowsAsync<BizgoBadRequestException>(() => t.Client.Messages.StatisticsAsync("20260101"));

            Assert.Equal(BizgoErrorLayer.Service, error.Layer);
            Assert.Equal(200, error.HttpStatus);
            Assert.Equal("유효하지 않거나 비어있는 필드 (필드명 : to)", error.Description);
            Assert.Contains("필드명 : to", error.Message, StringComparison.Ordinal);
            Assert.StartsWith("HTTP 200 | service code=A306 | Failed | ", error.Message, StringComparison.Ordinal);
            Assert.NotNull(error.Body);
        }

        [Fact]
        public async Task Same_code_means_different_things_per_layer()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, Stats, Responses.Envelope(code: "A401"), HttpStatusCode.BadRequest); // service A401 = invalid paymentCode
            await Assert.ThrowsAsync<BizgoBadRequestException>(() => t.Client.Messages.StatisticsAsync("20260101"));

            t.Handler.OnJson(HttpMethod.Get, Stats, "{\"common\":{\"authCode\":\"A401\",\"authResult\":\"Unauthorized\"}}", HttpStatusCode.Unauthorized);
            await Assert.ThrowsAsync<BizgoAuthenticationException>(() => t.Client.Messages.StatisticsAsync("20260101"));

            t.Handler.OnJson(HttpMethod.Get, Stats, Responses.Envelope(code: "A100"));
            await Assert.ThrowsAsync<BizgoAuthenticationException>(() => t.Client.Messages.StatisticsAsync("20260101")); // service A100

            t.Handler.OnJson(HttpMethod.Get, Stats, Responses.Envelope(code: "A110"));
            await Assert.ThrowsAsync<BizgoPermissionDeniedException>(() => t.Client.Messages.StatisticsAsync("20260101"));
        }

        [Fact]
        public async Task Not_found_status()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, Stats, "{\"common\":{\"authCode\":\"A404\",\"authResult\":\"Not Found\"}}", HttpStatusCode.NotFound);
            await Assert.ThrowsAsync<BizgoNotFoundException>(() => t.Client.Messages.StatisticsAsync("20260101"));
        }

        [Fact]
        public async Task Http_error_with_success_codes_is_still_an_error()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, Stats, Responses.Envelope(), HttpStatusCode.Forbidden);
            var error = await Assert.ThrowsAsync<BizgoPermissionDeniedException>(() => t.Client.Messages.StatisticsAsync("20260101"));
            Assert.Equal(BizgoErrorLayer.Gateway, error.Layer);
            Assert.Null(error.Code);
        }

        [Fact]
        public async Task Server_error_with_html_body_is_retried_then_raised()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Get, Stats, _ => Responses.Text("<html>bad gateway</html>", HttpStatusCode.BadGateway));

            var error = await Assert.ThrowsAsync<BizgoInternalServerException>(() => t.Client.Messages.StatisticsAsync("20260101"));

            Assert.Equal(3, t.Handler.Count(HttpMethod.Get, Stats));
            Assert.Contains("JSON이 아닙니다", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Success_status_with_non_json_body()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Get, Stats, _ => Responses.Text("ok", HttpStatusCode.OK));
            await Assert.ThrowsAsync<BizgoInvalidResponseException>(() => t.Client.Messages.StatisticsAsync("20260101"));
        }

        [Fact]
        public async Task Wrong_response_shape_is_an_invalid_response()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, Stats, Responses.Envelope("{\"statistics\":\"not-a-list\"}"));
            await Assert.ThrowsAsync<BizgoInvalidResponseException>(() => t.Client.Messages.StatisticsAsync("20260101"));
        }

        [Fact]
        public async Task Connection_error_hides_request_details()
        {
            using var t = new TestClient();
            var inner = new HttpRequestException("GET https://sandbox-mars.ibapi.kr" + MoHistory + "?from=" + Placeholders.OtherPhone + " failed");
            t.Handler.On(HttpMethod.Get, MoHistory, Responses.Throw(inner));

            var error = await Assert.ThrowsAsync<BizgoConnectionException>(() => t.Client.Messages.MoHistoryAsync("2026-04-23T14:11:01+09:00", from: Placeholders.OtherPhone));

            Assert.DoesNotContain(Placeholders.OtherPhone, error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("https://", error.ToString(), StringComparison.Ordinal);
            Assert.Null(error.InnerException); // the HttpRequestException (with the full URL) is not chained
            Assert.Equal(3, t.Handler.Count(HttpMethod.Get, MoHistory)); // GET is retried on connection errors
        }

        [Fact]
        public async Task Log_has_no_key_query_or_body()
        {
            using var t = new TestClient();
            t.Handler.OnJson(HttpMethod.Get, MoHistory, Responses.Envelope("{\"messages\":[],\"hasNext\":false}"));
            t.Handler.OnJson(HttpMethod.Post, "/api/comm/v1/send/omni", Responses.Accepted("A000"));

            await t.Client.Messages.MoHistoryAsync("2026-04-23T14:11:01+09:00", from: Placeholders.OtherPhone);
            await t.Client.Send.SmsAsync(Placeholders.OtherPhone, Placeholders.OtherPhone, "secret text");

            var lines = t.Log.ToArray();
            Assert.Equal(2, lines.Length);
            Assert.Matches(@"^GET /api/comm/v1/message/history/mo -> 200 \(\d+ ms, attempt 1\)$", lines[0]);
            Assert.Matches(@"^POST /api/comm/v1/send/omni -> 200 \(\d+ ms, attempt 1\)$", lines[1]);
            var all = string.Join("\n", lines);
            Assert.DoesNotContain(Placeholders.ApiKey, all, StringComparison.Ordinal);
            Assert.DoesNotContain(Placeholders.OtherPhone, all, StringComparison.Ordinal);
            Assert.DoesNotContain("secret text", all, StringComparison.Ordinal);
            Assert.DoesNotContain("?", all, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Failing_log_sink_does_not_fail_the_call()
        {
            using var t = new TestClient(o => o.Log = _ => throw new InvalidOperationException("sink down"));
            t.Handler.OnJson(HttpMethod.Get, Stats, Responses.Envelope("{\"statistics\":[]}"));
            Assert.Empty(await t.Client.Messages.StatisticsAsync("20260101"));
        }

        [Fact]
        public void Client_and_options_to_string_hide_key()
        {
            var options = new BizgoClientOptions { ApiKey = Placeholders.ApiKey };
            using var client = new BizgoClient(options);
            Assert.DoesNotContain(Placeholders.ApiKey, client.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Placeholders.ApiKey, options.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(Placeholders.ApiKey, client.Transport.Config.ToString(), StringComparison.Ordinal);
            Assert.Equal("BizgoClient(BaseUrl=https://mars.ibapi.kr, MaxRetries=2)", client.ToString());
        }

        [Theory]
        [InlineData("Bearer abc")]
        [InlineData("ApiKey abc")]
        [InlineData("  ")]
        [InlineData("")]
        [InlineData("abc\r\nX-Injected: 1")]
        [InlineData("키")]
        public void Prefixed_blank_or_unsafe_keys_are_rejected(string key)
        {
            var error = Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = key }));
            if (key.Trim().Length > 0)
            {
                Assert.DoesNotContain(key.Trim(), error.Message, StringComparison.Ordinal);
            }
        }

        [Fact]
        public void Api_key_from_environment_or_missing()
        {
            var previous = Environment.GetEnvironmentVariable(BizgoClient.ApiKeyEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(BizgoClient.ApiKeyEnvironmentVariable, Placeholders.ApiKey);
                using (var client = new BizgoClient())
                {
                    Assert.Equal(new Uri("https://mars.ibapi.kr"), client.BaseUrl);
                }

                Environment.SetEnvironmentVariable(BizgoClient.ApiKeyEnvironmentVariable, null);
                var error = Assert.Throws<BizgoConfigurationException>(() => new BizgoClient());
                Assert.Contains("BIZGO_API_KEY", error.Message, StringComparison.Ordinal);
            }
            finally
            {
                Environment.SetEnvironmentVariable(BizgoClient.ApiKeyEnvironmentVariable, previous);
            }
        }

        [Theory]
        [InlineData("http://mars.ibapi.kr")]
        [InlineData("ftp://example.com")]
        [InlineData("http://localhost.example.com")]
        [InlineData("https://user:pass@mars.ibapi.kr")]
        [InlineData("https://mars.ibapi.kr/?q=1")]
        public void Insecure_or_unsafe_base_url_is_rejected(string url)
        {
            var error = Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, BaseUrl = new Uri(url) }));
            Assert.DoesNotContain("pass", error.Message, StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("http://localhost:4010")]
        [InlineData("http://127.0.0.1:4010")]
        public void Localhost_http_is_allowed_for_mock_servers(string url)
        {
            using var client = new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, BaseUrl = new Uri(url) });
            Assert.Equal(new Uri(url), client.BaseUrl);
        }

        [Fact]
        public void Options_are_validated()
        {
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, MaxRetries = -1 }));
            Assert.Throws<BizgoConfigurationException>(() => new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, Timeout = TimeSpan.Zero }));
            // there is no option for a ready-made HttpClient: the SDK must be able to inspect the handler (SDK-DESIGN §12.6)
            Assert.Null(typeof(BizgoClientOptions).GetProperty("HttpClient"));
        }

        [Fact]
        public async Task Redirects_are_not_followed()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Get, Stats, _ =>
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Found);
                redirect.Headers.Location = new Uri("https://attacker.example/steal");
                return redirect;
            });

            var error = await Assert.ThrowsAsync<BizgoInvalidResponseException>(() => t.Client.Messages.StatisticsAsync("20260101"));

            Assert.Equal(302, error.HttpStatus);
            Assert.DoesNotContain("attacker", error.Message, StringComparison.Ordinal);
            Assert.Single(t.Handler.Requests);
        }

        [Fact]
        public async Task Response_from_another_origin_is_rejected()
        {
            // an injected handler that followed a redirect: the final request URI is elsewhere
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Get, Stats, _ =>
            {
                var response = Responses.Json(Responses.Envelope("{\"statistics\":[]}"));
                response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://other.example" + Stats);
                return response;
            });

            var error = await Assert.ThrowsAsync<BizgoInvalidResponseException>(() => t.Client.Messages.StatisticsAsync("20260101"));
            Assert.DoesNotContain("HTTP 200", error.Message, StringComparison.Ordinal); // the final status is not the (unknown) redirect status
            Assert.Contains("리다이렉트", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Oversized_response_is_rejected()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Get, Stats, _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[(16 * 1024 * 1024) + 1]) });
            var error = await Assert.ThrowsAsync<BizgoInvalidResponseException>(() => t.Client.Messages.StatisticsAsync("20260101"));
            Assert.Contains("너무 큽니다", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public async Task Injected_handler_is_used_and_not_disposed()
        {
            using var handler = new FakeHandler();
            handler.OnJson(HttpMethod.Get, Stats, Responses.Envelope("{\"statistics\":[]}"));
            using (var client = new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, Environment = BizgoEnvironment.Sandbox, HttpMessageHandler = handler }))
            {
                await client.Messages.StatisticsAsync("20260101");
            }

            // the handler is still usable after the SDK client was disposed
            using var http = new HttpClient(handler, disposeHandler: false);
            using var again = await http.GetAsync(new Uri("https://sandbox-mars.ibapi.kr" + Stats));
            Assert.Equal(2, handler.Requests.Count);
            Assert.Equal("sandbox-mars.ibapi.kr", handler.Requests[0].Uri.Host);
        }

        [Fact]
        public async Task Disposed_client_throws()
        {
            var t = new TestClient();
            t.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => t.Client.Messages.StatisticsAsync("20260101"));
        }

        [Fact]
        public async Task Caller_cancellation_is_not_a_timeout()
        {
            using var t = new TestClient();
            using var cts = new CancellationTokenSource();
            t.Handler.On(HttpMethod.Get, Stats, _ =>
            {
                cts.Cancel();
                throw new TaskCanceledException();
            });

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => t.Client.Messages.StatisticsAsync("20260101", cancellationToken: cts.Token));
            Assert.Single(t.Handler.Requests);
        }

        [Fact]
        public async Task Real_timeout_is_reported_as_timeout()
        {
            using var slow = new SlowHandler();
            using var client = new BizgoClient(new BizgoClientOptions { ApiKey = Placeholders.ApiKey, HttpMessageHandler = slow, Timeout = TimeSpan.FromMilliseconds(50), MaxRetries = 0 });

            var error = await Assert.ThrowsAsync<BizgoTimeoutException>(() => client.Messages.StatisticsAsync("20260101"));
            Assert.Contains("시간이 초과", error.Message, StringComparison.Ordinal);
        }

        [Fact]
        public void Every_exception_derives_from_bizgo_exception()
        {
            var types = typeof(BizgoClient).Assembly.GetExportedTypes().Where(t => typeof(Exception).IsAssignableFrom(t)).ToArray();
            Assert.All(types, t => Assert.True(typeof(BizgoException).IsAssignableFrom(t), t.Name));
            Assert.True(typeof(BizgoConnectionException).IsAssignableFrom(typeof(BizgoTimeoutException)));
            Assert.True(typeof(BizgoApiException).IsAssignableFrom(typeof(BizgoDuplicateRequestException)));
        }

        private sealed class SlowHandler : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
                return Responses.Json("{}");
            }
        }
    }
}
