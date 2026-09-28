using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Bizgo.Internal
{
    /// <summary>Validated, immutable per-client settings. The API key is never exposed by <see cref="ToString"/>.</summary>
    internal sealed class ClientConfig
    {
        internal const string ApiKeyVariable = "BIZGO_API_KEY";
        internal const string ProductionUrl = "https://mars.ibapi.kr";
        internal const string SandboxUrl = "https://sandbox-mars.ibapi.kr";

        private static readonly Regex AppName = new Regex("^[A-Za-z0-9._-]{1,50}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        private static readonly Regex AppVersion = new Regex("^[A-Za-z0-9._+-]{1,30}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        private ClientConfig(string apiKey, Uri baseUri, TimeSpan timeout, TimeSpan connectTimeout, int maxRetries, Action<string>? log, BizgoRateLimit? rateLimit, BizgoHooks? hooks, string userAgent)
        {
            ApiKey = apiKey;
            BaseUri = baseUri;
            BaseUrl = baseUri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped).TrimEnd('/');
            Timeout = timeout;
            ConnectTimeout = connectTimeout;
            MaxRetries = maxRetries;
            Log = log;
            RateLimit = rateLimit;
            OnRequest = hooks?.OnRequest;
            OnResponse = hooks?.OnResponse;
            UserAgent = userAgent;
        }

        /// <summary>Copy of the rate limits (null: off).</summary>
        internal BizgoRateLimit? RateLimit { get; }

        internal Action<BizgoRequestEvent>? OnRequest { get; }

        internal Action<BizgoResponseEvent>? OnResponse { get; }

        /// <summary><c>User-Agent</c> value (SDK-DESIGN §2.1), built once.</summary>
        internal string UserAgent { get; }

        internal string ApiKey { get; }

        internal Uri BaseUri { get; }

        /// <summary>Base URL without a trailing slash, for example <c>https://mars.ibapi.kr</c>.</summary>
        internal string BaseUrl { get; }

        internal TimeSpan Timeout { get; }

        internal TimeSpan ConnectTimeout { get; }

        internal int MaxRetries { get; }

        internal Action<string>? Log { get; }

        internal static ClientConfig From(BizgoClientOptions options)
        {
            if (options.MaxRetries < 0)
            {
                throw new BizgoConfigurationException("MaxRetries는 0 이상이어야 합니다.");
            }

            if (options.Timeout <= TimeSpan.Zero && options.Timeout != System.Threading.Timeout.InfiniteTimeSpan)
            {
                throw new BizgoConfigurationException("Timeout은 0보다 커야 합니다.");
            }

            if (options.ConnectTimeout <= TimeSpan.Zero && options.ConnectTimeout != System.Threading.Timeout.InfiniteTimeSpan)
            {
                throw new BizgoConfigurationException("ConnectTimeout은 0보다 커야 합니다.");
            }

            CheckCallback(options.Log, nameof(options.Log));
            CheckCallback(options.Hooks?.OnRequest, "Hooks.OnRequest");
            CheckCallback(options.Hooks?.OnResponse, "Hooks.OnResponse");

            BizgoRateLimit? rateLimit = null;
            if (options.RateLimit is not null)
            {
                options.RateLimit.Check();
                rateLimit = new BizgoRateLimit { SendPerSecond = options.RateLimit.SendPerSecond, OtherPerSecond = options.RateLimit.OtherPerSecond };
            }

            return new ClientConfig(
                ResolveApiKey(options.ApiKey),
                ResolveBaseUri(options.Environment, options.BaseUrl),
                options.Timeout,
                options.ConnectTimeout,
                options.MaxRetries,
                options.Log,
                rateLimit,
                options.Hooks,
                Identification.UserAgent(CheckAppInfo(options.AppInfo)));
        }

        /// <summary>
        /// Rejects <c>async</c> lambdas and methods: as an <c>Action</c> they become <c>async void</c>, and an exception after their first
        /// <c>await</c> cannot be caught by the SDK and ends the process (SDK-DESIGN §12.15).
        /// </summary>
        internal static void CheckCallback(Delegate? callback, string name)
        {
            if (callback is null)
            {
                return;
            }

            foreach (var target in callback.GetInvocationList())
            {
                if (target.Method.IsDefined(typeof(System.Runtime.CompilerServices.AsyncStateMachineAttribute), inherit: false))
                {
                    throw new BizgoConfigurationException(name + "에 async 람다/메서드를 넣을 수 없습니다(async void의 예외는 SDK가 잡을 수 없어 프로세스가 종료됩니다). 동기 콜백에서 작업을 큐에 넣으세요.");
                }
            }
        }

        internal static BizgoAppInfo? CheckAppInfo(BizgoAppInfo? appInfo)
        {
            if (appInfo is null)
            {
                return null;
            }

            if (appInfo.Name is null || !AppName.IsMatch(appInfo.Name))
            {
                throw new BizgoConfigurationException("AppInfo.Name은 영문·숫자·'.'·'_'·'-' 1~50자여야 합니다(헤더에 들어가므로 개인정보를 넣지 마세요).");
            }

            if (appInfo.Version is null || !AppVersion.IsMatch(appInfo.Version))
            {
                throw new BizgoConfigurationException("AppInfo.Version은 영문·숫자·'.'·'_'·'+'·'-' 1~30자여야 합니다.");
            }

            return appInfo;
        }

        internal static string ResolveApiKey(string? apiKey)
        {
            // not trimmed: a key with surrounding whitespace or a line break is a configuration error (SDK-DESIGN §12.16)
            var key = apiKey ?? Environment.GetEnvironmentVariable(ApiKeyVariable) ?? string.Empty;
            if (key.Length == 0)
            {
                throw new BizgoConfigurationException(
                    "API Key가 없습니다. BizgoClientOptions.ApiKey로 넘기거나 환경변수 " + ApiKeyVariable + "에 설정하세요. "
                    + "키는 코드에 직접 쓰지 말고 환경변수나 시크릿 저장소에서 읽어 오세요.");
            }

            foreach (var c in key)
            {
                if (char.IsWhiteSpace(c))
                {
                    throw new BizgoConfigurationException("API Key에 공백이 있습니다. 'Bearer '나 'ApiKey ' 같은 접두어 없이 키만 넣으세요.");
                }

                if (c < 0x21 || c > 0x7E)
                {
                    // header injection / non-ASCII: the key is sent verbatim in the Authorization header
                    throw new BizgoConfigurationException("API Key에 쓸 수 없는 문자(제어 문자 또는 ASCII 밖 문자)가 있습니다.");
                }
            }

            return key;
        }

        internal static Uri ResolveBaseUri(BizgoEnvironment environment, Uri? baseUrl)
        {
            Uri uri;
            if (baseUrl is null)
            {
                switch (environment)
                {
                    case BizgoEnvironment.Production:
                        uri = new Uri(ProductionUrl);
                        break;
                    case BizgoEnvironment.Sandbox:
                        uri = new Uri(SandboxUrl);
                        break;
                    default:
                        throw new BizgoConfigurationException("알 수 없는 Environment입니다: " + ((int)environment).ToString(CultureInfo.InvariantCulture));
                }
            }
            else
            {
                uri = baseUrl;
            }

            if (!uri.IsAbsoluteUri)
            {
                throw new BizgoConfigurationException("BaseUrl은 절대 URL이어야 합니다.");
            }

            var local = uri.IsLoopback && (uri.Host == "localhost" || uri.Host == "127.0.0.1" || uri.Host == "[::1]" || uri.Host == "::1");
            if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && local))
            {
                // The API key travels in a header, so it must never be sent over plain HTTP.
                throw new BizgoConfigurationException("BaseUrl은 https여야 합니다(테스트용 localhost 제외): " + Display(uri));
            }

            if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            {
                throw new BizgoConfigurationException("BaseUrl에 사용자 정보, 쿼리, fragment를 넣을 수 없습니다.");
            }

            return uri;
        }

        /// <summary>Scheme, host and port only (never user info, path or query).</summary>
        internal static string Display(Uri? uri)
        {
            if (uri is null)
            {
                return string.Empty;
            }

            if (!uri.IsAbsoluteUri)
            {
                return "(relative)";
            }

            return uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
        }

        public override string ToString()
        {
            return "ClientConfig(BaseUrl=" + Display(BaseUri) + ", MaxRetries=" + MaxRetries.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }
}
