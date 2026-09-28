using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Bizgo.Internal
{
    /// <summary>
    /// Collects validation problems of a request. Used by the generated <c>ValidateInto</c> methods.
    /// Reasons contain the field path and the rule only, never the value (it can be a phone number).
    /// </summary>
    internal sealed class RequestValidator
    {
        private static readonly ConcurrentDictionary<string, Regex> Patterns = new ConcurrentDictionary<string, Regex>(StringComparer.Ordinal);
        private readonly List<BizgoValidationError> _errors = new List<BizgoValidationError>();

        internal IReadOnlyList<BizgoValidationError> Errors => _errors;

        /// <summary>
        /// The request body being validated: set by the first body model whose <c>ValidateInto</c> runs with this validator.
        /// <c>x-sdk-required-if</c> paths starting with <c>$.</c> are checked against it; null (a nested model validated alone) skips them.
        /// </summary>
        internal object? Root { get; set; }

        /// <summary><c>x-sdk-required-if</c> condition: <paramref name="value"/> (null = missing) is one of <paramref name="values"/>, compared as strings.</summary>
        internal static bool In(string? value, params string[] values) => value is not null && Array.IndexOf(values, value) >= 0;

        /// <summary>A number or boolean as the string <c>x-sdk-required-if</c> compares (<c>true</c>, <c>12</c>).</summary>
        internal static string? Text(bool? value) => value is null ? null : value.Value ? "true" : "false";

        /// <inheritdoc cref="Text(bool?)"/>
        internal static string? Text(long? value) => value?.ToString(CultureInfo.InvariantCulture);

        /// <inheritdoc cref="Text(bool?)"/>
        internal static string? Text(double? value) => value?.ToString("R", CultureInfo.InvariantCulture);

        /// <summary>Required because of an <c>x-sdk-required-if</c> rule; <paramref name="condition"/> is the rule's text (field, operator, the spec's literal), never an input value.</summary>
        internal void RequiredWhen(string path, string name, object? value, string condition)
        {
            if (value is null)
            {
                Add(Child(path, name), condition);
            }
        }

        internal static string Child(string path, string name) => path.Length == 0 ? name : path + "." + name;

        internal static string Index(string path, string name, int index) => Child(path, name) + "[" + index.ToString(CultureInfo.InvariantCulture) + "]";

        internal void Add(string path, string reason) => _errors.Add(new BizgoValidationError(path, reason));

        internal void Required(string path, string name, object? value)
        {
            if (value is null)
            {
                Add(Child(path, name), "필수 필드입니다");
            }
        }

        internal void MinLength(string path, string name, string? value, long min)
        {
            if (value is not null && CodePoints(value) < min)
            {
                Add(Child(path, name), "최소 " + min.ToString(CultureInfo.InvariantCulture) + "자여야 합니다");
            }
        }

        /// <summary>Fields the model does not know (kept from a response or read from JSON) are rejected when the model is sent.</summary>
        internal void Unknown<T>(string path, IDictionary<string, T>? additional)
        {
            if (additional is null)
            {
                return;
            }

            foreach (var key in additional.Keys)
            {
                // the field name comes from the input; it is not a value, but keep it short and printable
                var shown = key.Length > 40 || key.Any(char.IsControl) ? "(이름 생략)" : key;
                Add(Child(path, shown), "모델에 없는 필드입니다(오타 확인)");
            }
        }

        internal void MaxLength(string path, string name, string? value, long max)
        {
            if (value is null)
            {
                return;
            }

            var length = CodePoints(value);
            if (length > max)
            {
                Add(Child(path, name), "최대 " + max.ToString(CultureInfo.InvariantCulture) + "자인데 " + length.ToString(CultureInfo.InvariantCulture) + "자입니다");
            }
        }

        internal void MaxBytesEucKr(string path, string name, string? value, long max)
        {
            if (value is null)
            {
                return;
            }

            int size;
            try
            {
                size = EucKr.ByteCount(value);
            }
            catch (UnencodableCharacterException e)
            {
                Add(Child(path, name), "EUC-KR(코드 페이지 949)로 표현할 수 없는 문자가 있습니다(" + e.Position.ToString(CultureInfo.InvariantCulture) + "번째 문자). 이모지 등은 문자메시지에 쓸 수 없습니다");
                return;
            }

            if (size > max)
            {
                Add(Child(path, name), "최대 " + max.ToString(CultureInfo.InvariantCulture) + "byte(EUC-KR)인데 " + size.ToString(CultureInfo.InvariantCulture) + "byte입니다");
            }
        }

        internal void MaxBytesUtf8(string path, string name, string? value, long max)
        {
            if (value is null)
            {
                return;
            }

            var size = Encoding.UTF8.GetByteCount(value);
            if (size > max)
            {
                Add(Child(path, name), "최대 " + max.ToString(CultureInfo.InvariantCulture) + "byte(UTF-8)인데 " + size.ToString(CultureInfo.InvariantCulture) + "byte입니다");
            }
        }

        internal void Pattern(string path, string name, string? value, string pattern)
        {
            if (value is null)
            {
                return;
            }

            var regex = Patterns.GetOrAdd(pattern, Compile);
            bool ok;
            try
            {
                ok = regex.IsMatch(value);
            }
            catch (RegexMatchTimeoutException)
            {
                ok = false;
            }

            if (!ok)
            {
                Add(Child(path, name), "형식이 맞지 않습니다(" + pattern + ")");
            }
        }

        internal void OneOf(string path, string name, string? value, params string[] allowed)
        {
            if (value is not null && Array.IndexOf(allowed, value) < 0)
            {
                Add(Child(path, name), "허용 값(" + string.Join(", ", allowed) + ")이 아닙니다");
            }
        }

        internal void Range(string path, string name, long? value, long? min, long? max)
        {
            if (value is null)
            {
                return;
            }

            if ((min is not null && value < min) || (max is not null && value > max))
            {
                Add(Child(path, name), "허용 범위(" + (min?.ToString(CultureInfo.InvariantCulture) ?? string.Empty) + "~" + (max?.ToString(CultureInfo.InvariantCulture) ?? string.Empty) + ")를 벗어났습니다");
            }
        }

        internal void Items<T>(string path, string name, IList<T>? list, long? min, long? max)
        {
            if (list is null)
            {
                return;
            }

            if (min is not null && list.Count < min)
            {
                Add(Child(path, name), "항목이 최소 " + min.Value.ToString(CultureInfo.InvariantCulture) + "개여야 합니다(현재 " + list.Count.ToString(CultureInfo.InvariantCulture) + "개)");
            }

            if (max is not null && list.Count > max)
            {
                Add(Child(path, name), "항목은 최대 " + max.Value.ToString(CultureInfo.InvariantCulture) + "개입니다(현재 " + list.Count.ToString(CultureInfo.InvariantCulture) + "개)");
            }

            for (var i = 0; i < list.Count; i++)
            {
                if (list[i] is null)
                {
                    Add(Index(path, name, i), "null 항목입니다");
                }
            }
        }

        internal void StringMap(string path, string name, IDictionary<string, string>? map)
        {
            if (map is null)
            {
                return;
            }

            foreach (var entry in map)
            {
                if (entry.Value is null)
                {
                    Add(Child(path, name), "값이 null인 항목이 있습니다");
                    return;
                }
            }
        }

        internal void ThrowIfInvalid()
        {
            if (_errors.Count > 0)
            {
                throw new BizgoValidationException(_errors.ToArray());
            }
        }

        private static int CodePoints(string value)
        {
            var count = 0;
            for (var i = 0; i < value.Length; i++)
            {
                if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                {
                    i++;
                }

                count++;
            }

            return count;
        }

        private static Regex Compile(string pattern)
        {
            // In .NET a trailing '$' also matches before a final newline; the spec means "end of text".
            var anchored = pattern.EndsWith("$", StringComparison.Ordinal) && !pattern.EndsWith("\\$", StringComparison.Ordinal)
                ? pattern.Substring(0, pattern.Length - 1) + "\\z"
                : pattern;
            return new Regex(anchored, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
        }
    }
}
