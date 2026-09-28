using System;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Bizgo.Internal
{
    /// <summary>
    /// SDK identification headers (SDK-DESIGN §2.1): <c>User-Agent: bizgo-sdk-comm-cs/&lt;ver&gt; dotnet/&lt;ver&gt; (&lt;os&gt;; &lt;arch&gt;)[ app/&lt;name&gt;-&lt;ver&gt;]</c>
    /// and <c>X-Bizgo-Client: bizgo-sdk-comm-cs/&lt;ver&gt;</c>. Only coarse values: no host name, user name or kernel version.
    /// The SDK sends nothing else (no phone-home).
    /// </summary>
    internal static class Identification
    {
        internal const string ClientHeaderName = "X-Bizgo-Client";

        internal static readonly string SdkVersion = ReadVersion();

        internal static readonly string ClientHeader = "bizgo-sdk-comm-cs/" + SdkVersion;

        private static readonly string Base = ClientHeader + " " + Runtime() + " (" + Os() + "; " + Arch() + ")";

        internal static string UserAgent(BizgoAppInfo? app) => app is null ? Base : Base + " app/" + app.Name + "-" + app.Version;

        internal static string Os()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                return "linux";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                return "windows";
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                return "darwin";
            }

#if NET5_0_OR_GREATER
            if (RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD))
            {
                return "freebsd";
            }
#endif
            return "other";
        }

        internal static string Arch()
        {
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64:
                    return "x64";
                case Architecture.Arm64:
                    return "arm64";
                case Architecture.X86:
                    return "x86";
                case Architecture.Arm:
                    return "arm";
                default:
                    return "other";
            }
        }

        private static string Runtime()
        {
            var framework = RuntimeInformation.FrameworkDescription ?? string.Empty;
            var runtime = framework.StartsWith(".NET Framework", StringComparison.Ordinal) ? "dotnetfx" : "dotnet";
            var version = new string(framework.Split(' ').LastOrDefault()?.Where(c => char.IsDigit(c) || c == '.').ToArray() ?? Array.Empty<char>());
            if (version.Length == 0)
            {
                version = Environment.Version.ToString();
            }

            return runtime + "/" + Token(version);
        }

        private static string ReadVersion()
        {
            var version = typeof(Identification).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = version.IndexOf('+');
            return Token(plus >= 0 ? version.Substring(0, plus) : version);
        }

        private static string Token(string value)
        {
            var cleaned = new string(value.Where(c => (c < 0x80 && char.IsLetterOrDigit(c)) || c == '.' || c == '-').ToArray());
            return cleaned.Length == 0 ? "0" : cleaned;
        }
    }
}
