using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace Bizgo.Internal
{
    /// <summary>Jitter source that is safe to use from many threads (netstandard2.0 has no <c>Random.Shared</c>).</summary>
    internal static class ThreadSafeRandom
    {
#if NET6_0_OR_GREATER
        internal static double Jitter() => 0.75 + (Random.Shared.NextDouble() * 0.5);
#else
        private static readonly Random Shared = new Random();
        private static readonly object Gate = new object();

        internal static double Jitter()
        {
            lock (Gate)
            {
                return 0.75 + (Shared.NextDouble() * 0.5);
            }
        }
#endif
    }

    /// <summary>Crypto helpers that exist only on newer runtimes.</summary>
    internal static class CryptoHelpers
    {
        /// <summary>
        /// Compares two byte arrays in time that depends only on their length (not on where they differ), like
        /// <c>CryptographicOperations.FixedTimeEquals</c> on .NET Core 2.1+.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
        internal static bool FixedTimeEquals(byte[] left, byte[] right)
        {
#if NETCOREAPP2_1_OR_GREATER
            return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(left, right);
#else
            if (left.Length != right.Length)
            {
                return false;
            }

            var accumulator = 0;
            for (var i = 0; i < left.Length; i++)
            {
                accumulator |= left[i] - right[i];
            }

            return accumulator == 0;
#endif
        }

        /// <summary>Lower-case hex (like <c>Convert.ToHexString(...).ToLowerInvariant()</c>).</summary>
        internal static string ToHexLower(byte[] bytes)
        {
            const string Digits = "0123456789abcdef";
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                sb.Append(Digits[b >> 4]).Append(Digits[b & 0xF]);
            }

            return sb.ToString();
        }
    }
}
