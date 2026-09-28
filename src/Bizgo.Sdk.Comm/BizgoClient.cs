using System;
using Bizgo.Internal;

namespace Bizgo
{
    /// <summary>
    /// Bizgo Communication API client. Thread-safe: create one per API key and reuse it; dispose it when done.
    /// </summary>
    /// <example>
    /// <code>
    /// using var client = new BizgoClient(new BizgoClientOptions { Environment = BizgoEnvironment.Sandbox }); // key from BIZGO_API_KEY
    /// var result = await client.Send.SmsAsync("01000000000", from: "01000000000", text: "인증번호는 123456 입니다.");
    /// Console.WriteLine(string.Join(",", result.MsgKeys) + " failed=" + result.Failed.Count);
    /// </code>
    /// </example>
    public sealed partial class BizgoClient : IDisposable
    {
        /// <summary>Environment variable read when <see cref="BizgoClientOptions.ApiKey"/> is null.</summary>
        public const string ApiKeyEnvironmentVariable = ClientConfig.ApiKeyVariable;

        /// <summary>
        /// Seconds (86400 = 24 hours, the maximum) sent as <c>idempotencyTtl</c> when a request sets <c>idempotencyKey</c> but leaves the TTL null.
        /// Bizgo rejects a key without a TTL (A309), so every send path (<see cref="SendResource"/> methods, <c>BulkAsync</c> chunks, and
        /// generated operations whose body has both fields) fills it in the serialized body without changing your object.
        /// An explicit TTL (0 included, range 0-86400) is sent as is; without a key no TTL is added.
        /// </summary>
        public const int DefaultIdempotencyTtl = 86400;

        private readonly Transport _transport;

        /// <summary>Creates a client for production with the key from <c>BIZGO_API_KEY</c>.</summary>
        /// <exception cref="BizgoConfigurationException">The key is missing or malformed.</exception>
        public BizgoClient()
            : this(new BizgoClientOptions())
        {
        }

        /// <summary>Creates a client.</summary>
        /// <param name="options">Settings. They are copied; later changes have no effect.</param>
        /// <exception cref="BizgoConfigurationException">The key is missing or malformed, the base URL is not https, or an option is out of range.</exception>
        public BizgoClient(BizgoClientOptions options)
        {
            if (options is null)
            {
                throw new ArgumentNullException(nameof(options));
            }

            var config = ClientConfig.From(options);
            _transport = new Transport(config, options.HttpMessageHandler);
            Send = new SendResource(_transport);
            Files = new FilesResource(_transport);
            Reports = new ReportsResource(_transport);
            Messages = new MessagesResource(_transport);
            InitializeGeneratedResources(_transport);
        }

        /// <summary>Message sending.</summary>
        public SendResource Send { get; }

        /// <summary>Image uploads.</summary>
        public FilesResource Files { get; }

        /// <summary>Delivery reports (polling, inquiry).</summary>
        public ReportsResource Reports { get; }

        /// <summary>Message status, history and statistics.</summary>
        public MessagesResource Messages { get; }

        /// <summary>The server URL in use.</summary>
        public Uri BaseUrl => _transport.Config.BaseUri;

        internal Transport Transport => _transport;

        /// <summary>Releases the HTTP client the SDK created. A client you passed in is not disposed.</summary>
        public void Dispose() => _transport.Dispose();

        /// <summary>Returns a description without the API key.</summary>
        /// <returns>For example <c>BizgoClient(BaseUrl=https://sandbox-mars.ibapi.kr, MaxRetries=2)</c>.</returns>
        public override string ToString() => "BizgoClient(BaseUrl=" + ClientConfig.Display(BaseUrl) + ", MaxRetries=" + _transport.Config.MaxRetries + ")";
    }
}
