using System;
using System.Text;

namespace Bizgo.Internal
{
    /// <summary>
    /// Byte length the carrier counts for <c>x-charset: EUC-KR</c> fields: the text encoded with code page 949
    /// (CP949, a superset of EUC-KR with the same 2 bytes per Hangul syllable).
    /// </summary>
    /// <remarks>
    /// The encoding is taken from <see cref="CodePagesEncodingProvider.Instance"/> directly instead of calling
    /// <see cref="Encoding.RegisterProvider"/>, so the SDK does not change process-wide encoding settings.
    /// </remarks>
    internal static class EucKr
    {
        private static readonly Encoding Cp949 = CreateEncoding();

        /// <summary>Returns the CP949 byte count of <paramref name="value"/>.</summary>
        /// <exception cref="UnencodableCharacterException">A character (emoji, lone surrogate, ...) has no CP949 representation.</exception>
        internal static int ByteCount(string value)
        {
            try
            {
                return Cp949.GetByteCount(value);
            }
            catch (EncoderFallbackException e)
            {
                throw new UnencodableCharacterException(Position(value, e.Index));
            }
        }

        private static Encoding CreateEncoding()
        {
            var encoding = CodePagesEncodingProvider.Instance.GetEncoding(949, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return encoding ?? throw new InvalidOperationException("code page 949 is not available");
        }

        /// <summary>1-based code point position of the UTF-16 index (never includes the character itself).</summary>
        private static int Position(string value, int index)
        {
            var position = 0;
            for (var i = 0; i < value.Length && i <= index; i++)
            {
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    i++;
                }

                position++;
            }

            return Math.Max(position, 1);
        }
    }

    /// <summary>A character cannot be represented in CP949.</summary>
#pragma warning disable CA1032, CA1064 // internal signalling exception; never escapes the SDK
    internal sealed class UnencodableCharacterException : Exception
#pragma warning restore CA1032, CA1064
    {
        internal UnencodableCharacterException(int position)
            : base("unencodable character")
        {
            Position = position;
        }

        /// <summary>1-based position of the character, in code points.</summary>
        internal int Position { get; }
    }
}
