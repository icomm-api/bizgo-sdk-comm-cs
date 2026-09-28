using System;
using System.Net.Http;

namespace Bizgo
{
    /// <summary>Bizgo API server.</summary>
    public enum BizgoEnvironment
    {
        /// <summary>운영 서버 <c>https://mars.ibapi.kr</c>. 실제로 발송됩니다.</summary>
        Production,

        /// <summary>테스트 서버 <c>https://sandbox-mars.ibapi.kr</c>. 운영과 같은 API Key를 쓰며 실제로 발송되지 않습니다.</summary>
        Sandbox,
    }

    /// <summary>
    /// Settings for <see cref="BizgoClient"/>. The client copies them when it is created, so changing this object later has no effect.
    /// </summary>
    public sealed class BizgoClientOptions
    {
        /// <summary>
        /// Integrated API key from the console (<c>발송관리 &gt; 연동관리</c>). When null, the <c>BIZGO_API_KEY</c> environment
        /// variable is used. Pass the key only, without <c>Bearer</c>/<c>ApiKey</c> prefixes. Never hard-code it.
        /// </summary>
        public string? ApiKey { get; set; }

        /// <summary>Server to use. Default: <see cref="BizgoEnvironment.Production"/> (real sends).</summary>
        public BizgoEnvironment Environment { get; set; } = BizgoEnvironment.Production;

        /// <summary>Overrides the server URL. Must be https (http is allowed only for localhost, for mock servers). No query or credentials.</summary>
        public Uri? BaseUrl { get; set; }

        /// <summary>Time limit of one attempt, including reading the response. Default 30 seconds.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Connection time limit. Default 5 seconds. Applied only to the handler the SDK creates on .NET 5+
        /// (<c>SocketsHttpHandler.ConnectTimeout</c>); on other runtimes and with your own handler only <see cref="Timeout"/> applies.
        /// </summary>
        public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Retries for rate limits and transient errors. Default 2. Sends without <see cref="SendOptions.IdempotencyKey"/> and uploads
        /// are retried only on HTTP 429, never after a timeout or 5xx (they could be delivered twice).
        /// </summary>
        public int MaxRetries { get; set; } = 2;

        /// <summary>
        /// Your own handler, for a proxy, client certificates or <c>IHttpClientFactory</c>
        /// (<c>IHttpMessageHandlerFactory.CreateHandler(name)</c>). The SDK always builds and owns the <see cref="System.Net.Http.HttpClient"/>
        /// around it (timeouts are <see cref="Timeout"/>); it does not accept a ready-made <c>HttpClient</c> because it could not check how
        /// that client authenticates or redirects. The handler is not disposed by the SDK.
        /// </summary>
        /// <remarks>
        /// The handler and every <see cref="DelegatingHandler"/> inner handler are checked when the client is created. An
        /// <c>HttpClientHandler</c>/<c>SocketsHttpHandler</c> must have <c>AllowAutoRedirect = false</c> (a redirect would resend the body,
        /// with phone numbers, to another host), <c>UseCookies = false</c>, and no <c>Credentials</c>, <c>UseDefaultCredentials</c> or
        /// <c>PreAuthenticate</c> (after a 401 the handler would resend a send request with other credentials). Otherwise
        /// <see cref="BizgoConfigurationException"/> is thrown. Proxy credentials are allowed. TLS settings of the handler apply as they are.
        /// </remarks>
        public HttpMessageHandler? HttpMessageHandler { get; set; }

        /// <summary>
        /// Optional diagnostics callback. It receives only lines like <c>POST /api/comm/v1/send/omni -&gt; 200 (123 ms, attempt 1)</c>:
        /// never the API key, bodies, query strings or phone numbers. Default: silent.
        /// With Microsoft.Extensions.Logging: <c>Log = line =&gt; logger.LogDebug("{BizgoRequest}", line)</c>.
        /// </summary>
        public Action<string>? Log { get; set; }

        /// <summary>
        /// Client-side rate limits (default: send 200 messages/s, other 5 requests/s). Set to null to turn them off. They pace this
        /// client only; several processes sharing a key need their own coordination (HTTP 429 is still retried).
        /// </summary>
        public BizgoRateLimit? RateLimit { get; set; } = new BizgoRateLimit();

        /// <summary>Request callbacks for metrics (operation, path template, status, code, attempt, duration; never values).</summary>
        public BizgoHooks? Hooks { get; set; }

        /// <summary>
        /// Optional application name and version appended to the <c>User-Agent</c> header as <c>app/&lt;name&gt;-&lt;version&gt;</c> so Bizgo
        /// can see which integration is calling. Do not put e-mail addresses, phone numbers or other personal data in it.
        /// </summary>
        public BizgoAppInfo? AppInfo { get; set; }

        /// <summary>Returns a description without the API key.</summary>
        /// <returns>A string such as <c>BizgoClientOptions(Environment=Sandbox, BaseUrl=, ApiKey=***)</c>.</returns>
        public override string ToString()
        {
            return "BizgoClientOptions(Environment=" + Environment + ", BaseUrl=" + Internal.ClientConfig.Display(BaseUrl) + ", ApiKey=" + (ApiKey is null ? "(env)" : "***") + ")";
        }
    }

    /// <summary>Application identification for the <c>User-Agent</c> header (SDK-DESIGN §2.1).</summary>
    public sealed class BizgoAppInfo
    {
        /// <summary>Creates the value. It is validated when the client is created.</summary>
        /// <param name="name"><c>^[A-Za-z0-9._-]{1,50}$</c>, for example <c>myshop</c>.</param>
        /// <param name="version"><c>^[A-Za-z0-9._+-]{1,30}$</c>, for example <c>1.4.2</c>.</param>
        public BizgoAppInfo(string name, string version)
        {
            Name = name;
            Version = version;
        }

        /// <summary>Application name.</summary>
        public string Name { get; }

        /// <summary>Application version.</summary>
        public string Version { get; }

        /// <inheritdoc/>
        public override string ToString() => "app/" + Name + "-" + Version;
    }
}
