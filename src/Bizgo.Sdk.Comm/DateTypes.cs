using System;
using System.Globalization;
using Bizgo.Internal;

namespace Bizgo
{
    /// <summary>
    /// A calendar date for the statistics API (<c>YYYYMMDD</c>, KST). Converts implicitly from <see cref="DateTime"/>,
    /// <see cref="DateTimeOffset"/>, <c>DateOnly</c> (.NET 6+) and a <c>"YYYYMMDD"</c> string.
    /// </summary>
    /// <remarks>
    /// <see cref="DateTimeOffset"/> and <see cref="DateTime"/> values of kind <see cref="DateTimeKind.Utc"/>/<see cref="DateTimeKind.Local"/>
    /// are converted to KST first; <see cref="DateTimeKind.Unspecified"/> values are used as they are.
    /// </remarks>
    public readonly struct BizgoDate : IEquatable<BizgoDate>
    {
        private readonly string? _value;

        private BizgoDate(string value)
        {
            _value = value;
        }

        /// <summary>The <c>YYYYMMDD</c> text sent to the API.</summary>
        public string Value => _value ?? string.Empty;

        /// <summary>From a date/time (see remarks of <see cref="BizgoDate"/>).</summary>
        /// <param name="value">Date.</param>
        public static implicit operator BizgoDate(DateTime value) => FromDateTime(value);

        /// <summary>From an absolute time; converted to the KST date.</summary>
        /// <param name="value">Time.</param>
        public static implicit operator BizgoDate(DateTimeOffset value) => FromDateTimeOffset(value);

        /// <summary>From <c>"YYYYMMDD"</c>. The format is checked when the request is made.</summary>
        /// <param name="value">Date text.</param>
        public static implicit operator BizgoDate(string value) => FromString(value);

#if NET6_0_OR_GREATER
        /// <summary>From a calendar date.</summary>
        /// <param name="value">Date.</param>
        public static implicit operator BizgoDate(DateOnly value) => FromDateOnly(value);

        /// <summary>From a calendar date.</summary>
        /// <param name="value">Date.</param>
        /// <returns>The date.</returns>
        public static BizgoDate FromDateOnly(DateOnly value) => new BizgoDate(value.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
#endif

        /// <summary>Compares two dates.</summary>
        /// <param name="left">Left.</param>
        /// <param name="right">Right.</param>
        /// <returns>True if equal.</returns>
        public static bool operator ==(BizgoDate left, BizgoDate right) => left.Equals(right);

        /// <summary>Compares two dates.</summary>
        /// <param name="left">Left.</param>
        /// <param name="right">Right.</param>
        /// <returns>True if different.</returns>
        public static bool operator !=(BizgoDate left, BizgoDate right) => !left.Equals(right);

        /// <summary>From a date/time.</summary>
        /// <param name="value">Date.</param>
        /// <returns>The date.</returns>
        public static BizgoDate FromDateTime(DateTime value)
        {
            var date = value.Kind == DateTimeKind.Unspecified ? value : Kst.From(value).DateTime;
            return new BizgoDate(date.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        }

        /// <summary>From an absolute time (KST date).</summary>
        /// <param name="value">Time.</param>
        /// <returns>The date.</returns>
        public static BizgoDate FromDateTimeOffset(DateTimeOffset value) => new BizgoDate(Kst.From(value).ToString("yyyyMMdd", CultureInfo.InvariantCulture));

        /// <summary>From <c>"YYYYMMDD"</c>.</summary>
        /// <param name="value">Date text.</param>
        /// <returns>The date.</returns>
        public static BizgoDate FromString(string value) => new BizgoDate(value ?? string.Empty);

        /// <inheritdoc/>
        public bool Equals(BizgoDate other) => string.Equals(Value, other.Value, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is BizgoDate other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

        /// <inheritdoc/>
        public override string ToString() => Value;

        internal string ToQuery(string name)
        {
            var value = Value;
            if (value.Length != 8 || !IsDigits(value))
            {
                throw new BizgoValidationException(name, "YYYYMMDD 형식이어야 합니다");
            }

            return value;
        }

        private static bool IsDigits(string value)
        {
            foreach (var c in value)
            {
                if (c < '0' || c > '9')
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// A point in time for the history APIs. Converts implicitly from <see cref="DateTime"/>, <see cref="DateTimeOffset"/> and a string
    /// (sent as is).
    /// </summary>
    /// <remarks>
    /// Send history (<c>requestTime</c>) is sent as KST <c>yyyy-MM-ddTHH:mm:ss</c>; MO history (<c>occurredTime</c>) as
    /// <c>yyyy-MM-ddTHH:mm:ss+09:00</c>. <see cref="DateTimeOffset"/> and <see cref="DateTime"/> values of kind
    /// <see cref="DateTimeKind.Utc"/>/<see cref="DateTimeKind.Local"/> are converted to KST; <see cref="DateTimeKind.Unspecified"/>
    /// values are taken as KST wall-clock time.
    /// </remarks>
    public readonly struct BizgoDateTime : IEquatable<BizgoDateTime>
    {
        private readonly DateTimeOffset? _time;
        private readonly string? _text;

        private BizgoDateTime(DateTimeOffset? time, string? text)
        {
            _time = time;
            _text = text;
        }

        /// <summary>From a date/time (see remarks of <see cref="BizgoDateTime"/>).</summary>
        /// <param name="value">Time.</param>
        public static implicit operator BizgoDateTime(DateTime value) => FromDateTime(value);

        /// <summary>From an absolute time.</summary>
        /// <param name="value">Time.</param>
        public static implicit operator BizgoDateTime(DateTimeOffset value) => FromDateTimeOffset(value);

        /// <summary>From text that is sent to the API unchanged.</summary>
        /// <param name="value">Time text.</param>
        public static implicit operator BizgoDateTime(string value) => FromString(value);

        /// <summary>Compares two values.</summary>
        /// <param name="left">Left.</param>
        /// <param name="right">Right.</param>
        /// <returns>True if equal.</returns>
        public static bool operator ==(BizgoDateTime left, BizgoDateTime right) => left.Equals(right);

        /// <summary>Compares two values.</summary>
        /// <param name="left">Left.</param>
        /// <param name="right">Right.</param>
        /// <returns>True if different.</returns>
        public static bool operator !=(BizgoDateTime left, BizgoDateTime right) => !left.Equals(right);

        /// <summary>From a date/time.</summary>
        /// <param name="value">Time.</param>
        /// <returns>The value.</returns>
        public static BizgoDateTime FromDateTime(DateTime value) => new BizgoDateTime(Kst.From(value), null);

        /// <summary>From an absolute time.</summary>
        /// <param name="value">Time.</param>
        /// <returns>The value.</returns>
        public static BizgoDateTime FromDateTimeOffset(DateTimeOffset value) => new BizgoDateTime(Kst.From(value), null);

        /// <summary>From text that is sent unchanged.</summary>
        /// <param name="value">Time text.</param>
        /// <returns>The value.</returns>
        public static BizgoDateTime FromString(string value) => new BizgoDateTime(null, value ?? string.Empty);

        /// <summary>KST <c>yyyy-MM-ddTHH:mm:ss</c> (send history <c>requestTime</c>).</summary>
        /// <returns>Text.</returns>
        public string ToRequestTime() => _time is null ? _text ?? string.Empty : _time.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

        /// <summary><c>yyyy-MM-ddTHH:mm:ss+09:00</c> (MO history <c>occurredTime</c>).</summary>
        /// <returns>Text.</returns>
        public string ToOccurredTime() => _time is null ? _text ?? string.Empty : _time.Value.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+09:00";

        /// <inheritdoc/>
        public bool Equals(BizgoDateTime other) => Nullable.Equals(_time, other._time) && string.Equals(_text, other._text, StringComparison.Ordinal);

        /// <inheritdoc/>
        public override bool Equals(object? obj) => obj is BizgoDateTime other && Equals(other);

        /// <inheritdoc/>
        public override int GetHashCode() => _time?.GetHashCode() ?? StringComparer.Ordinal.GetHashCode(_text ?? string.Empty);

        /// <inheritdoc/>
        public override string ToString() => ToOccurredTime();

        /// <summary>Formats for a query parameter with the documented <c>x-format</c>; a string is sent as given.</summary>
        internal string Format(string format, string name)
        {
            if (_time is null)
            {
                return _text is null || _text.Length == 0 ? throw new BizgoValidationException(name, "필수 값입니다") : _text;
            }

            var kst = _time.Value;
            switch (format)
            {
                case "yyyy-MM-dd'T'HH:mm:ss":
                    return kst.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
                case "yyyy-MM-dd'T'HH:mm:ssXXX":
                    return kst.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+09:00";
                case "yyyy-MM-dd HH:mm:ss":
                    return kst.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                case "yyyyMMddHHmmss":
                    return kst.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
                default:
                    throw new BizgoValidationException(name, "지원하지 않는 시각 형식입니다");
            }
        }

        internal string Require(string name, bool offset)
        {
            var text = offset ? ToOccurredTime() : ToRequestTime();
            if (text.Length == 0)
            {
                throw new BizgoValidationException(name, "필수 값입니다");
            }

            return text;
        }
    }
}
