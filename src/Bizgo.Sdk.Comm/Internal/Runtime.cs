using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Threading;
using System.Threading.Tasks;

namespace Bizgo.Internal
{
    /// <summary>Result helpers for generated methods: a success response never yields null (SDK-DESIGN §12.19).</summary>
    internal static class Results
    {
        internal static T Object<T>(T? value)
            where T : class, new()
        {
            return value ?? new T();
        }

        internal static IReadOnlyList<T> List<T>(IList<T>? value)
        {
            if (value is null || value.Count == 0)
            {
                return Array.Empty<T>();
            }

            return value.Where(v => v is not null).ToArray();
        }
    }

    /// <summary>Stop rule of page/offset pagination (SDK-DESIGN §10).</summary>
    internal static class Paging
    {
        /// <summary>
        /// True when another page should be requested: this page had items, not fewer than the requested size, <c>hasNext</c> is not
        /// false and fewer than <c>total</c> items were seen.
        /// </summary>
        internal static bool More(int count, long? size, bool? hasNext, long? total, long seen)
        {
            if (count == 0 || (size is not null && count < size))
            {
                return false;
            }

            if (hasNext == false)
            {
                return false;
            }

            return total is null || seen < total;
        }

        internal static long? ParseCount(string? value)
        {
            return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }
    }

    /// <summary>Token cost of a request in the send bucket (SDK-DESIGN §11.2).</summary>
    internal static class RateCost
    {
        /// <summary>Number of destinations, at least 1 (a send without a recipient list costs 1).</summary>
        internal static int Destinations(int? count) => Math.Max(1, count ?? 0);
    }

    /// <summary>Header parameters of generated operations.</summary>
    internal static class HeaderValue
    {
        /// <summary>Adds a header parameter. Values must be one line of printable ASCII (no header injection); the value is never logged.</summary>
        /// <exception cref="BizgoValidationException">The value is empty or has control/non-ASCII characters.</exception>
        internal static void Add(List<KeyValuePair<string, string>> headers, string name, string? value)
        {
            if (value is null)
            {
                return;
            }

            if (value.Length == 0 || value.Any(c => c < 0x20 || c > 0x7E))
            {
                throw new BizgoValidationException(name, "헤더 값은 비어 있지 않은 한 줄 ASCII 문자열이어야 합니다");
            }

            headers.Add(new KeyValuePair<string, string>(name, value));
        }
    }

    /// <summary>
    /// Builds a multipart/form-data body. Files are read once (up to a limit) so every retry sends the same bytes; model fields
    /// become <c>application/json</c> parts.
    /// </summary>
    internal sealed class MultipartForm
    {
        private readonly List<Func<HttpContent>> _parts = new List<Func<HttpContent>>();

        internal async Task FileAsync(string name, FileUpload? file, int limit, CancellationToken cancellationToken)
        {
            if (file is null)
            {
                return;
            }

            var bytes = await file.ReadAsync(limit, "파일이 너무 큽니다(최대 " + limit.ToString("N0", CultureInfo.InvariantCulture) + "byte).", cancellationToken, name).ConfigureAwait(false);
            var fileName = file.FileName;
            var contentType = file.ContentType;
            _parts.Add(() =>
            {
                var part = new ByteArrayContent(bytes);
                part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"" + name + "\"", FileName = FileUpload.Quote(fileName) };
                return part;
            });
        }

        internal async Task FilesAsync(string name, IList<FileUpload>? files, int limit, CancellationToken cancellationToken)
        {
            if (files is null)
            {
                return;
            }

            foreach (var file in files)
            {
                await FileAsync(name, file, limit, cancellationToken).ConfigureAwait(false);
            }
        }

        internal void Json<T>(string name, T? value, JsonTypeInfo<T> typeInfo)
            where T : class
        {
            if (value is null)
            {
                return;
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, typeInfo);
            _parts.Add(() =>
            {
                var part = new ByteArrayContent(bytes);
                part.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"" + name + "\"" };
                return part;
            });
        }

        internal void Text(string name, string? value)
        {
            if (value is null)
            {
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(value);
            _parts.Add(() =>
            {
                var part = new ByteArrayContent(bytes);
                part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"" + name + "\"" };
                return part;
            });
        }

        internal void Text(string name, long? value) => Text(name, value?.ToString(CultureInfo.InvariantCulture));

        internal void Text(string name, bool? value) => Text(name, value is null ? null : value.Value ? "true" : "false");

        internal Func<HttpContent> Build()
        {
            var parts = _parts.ToArray();
            return () =>
            {
                var form = new MultipartFormDataContent();
                foreach (var part in parts)
                {
                    form.Add(part());
                }

                return form;
            };
        }
    }

    /// <summary>Phone number masking for <c>ToString()</c> (SDK-DESIGN §12.11).</summary>
    internal static class PhoneMask
    {
        /// <summary>
        /// <c>01000000000</c> → <c>010****0000</c>. Numbers shorter than 11 characters keep at most 3 leading and a few trailing
        /// characters so that at least half is hidden (<c>15880000</c> → <c>158****0</c>).
        /// </summary>
        internal static string Mask(string value)
        {
            var n = value.Length;
            if (n >= 11)
            {
                return value.Substring(0, 3) + new string('*', n - 7) + value.Substring(n - 4);
            }

            var hidden = (n + 1) / 2;
            var visible = n - hidden;
            var front = Math.Min(3, visible);
            var back = visible - front;
            return value.Substring(0, front) + new string('*', hidden) + value.Substring(n - back);
        }
    }

    /// <summary>Builds <c>Name(field=value, ...)</c> for model <c>ToString()</c>; only set values are shown.</summary>
    internal sealed class ModelText
    {
        private readonly string _name;
        private readonly List<string> _parts = new List<string>();

        internal ModelText(string name)
        {
            _name = name;
        }

        internal ModelText Add(string name, object? value)
        {
            if (value is null)
            {
                return this;
            }

            string text;
            switch (value)
            {
                case string s:
                    text = s;
                    break;
                case bool b:
                    text = b ? "true" : "false";
                    break;
                case IFormattable f:
                    text = f.ToString(null, CultureInfo.InvariantCulture);
                    break;
                default:
                    text = value.ToString() ?? string.Empty;
                    break;
            }

            _parts.Add(name + "=" + text);
            return this;
        }

        internal ModelText Phone(string name, string? value)
        {
            if (value is not null)
            {
                _parts.Add(name + "=" + PhoneMask.Mask(value));
            }

            return this;
        }

        internal ModelText Phones(string name, IEnumerable<string>? values)
        {
            if (values is not null)
            {
                _parts.Add(name + "=[" + string.Join(", ", values.Select(v => v is null ? "null" : PhoneMask.Mask(v))) + "]");
            }

            return this;
        }

        /// <summary>A person's name, nickname, e-mail or address: first character only.</summary>
        internal ModelText Masked(string name, string? value)
        {
            if (value is not null)
            {
                _parts.Add(name + "=" + (value.Length == 0 ? string.Empty : value.Substring(0, 1) + new string('*', Math.Max(value.Length - 1, 2))));
            }

            return this;
        }

        internal ModelText Length(string name, string? value)
        {
            if (value is not null)
            {
                _parts.Add(name + "=<" + value.Length.ToString(CultureInfo.InvariantCulture) + " chars>");
            }

            return this;
        }

        internal ModelText Count(string name, IEnumerable? values)
        {
            if (values is not null)
            {
                var count = values is ICollection c ? c.Count : values.Cast<object?>().Count();
                _parts.Add(name + "=<" + count.ToString(CultureInfo.InvariantCulture) + " items>");
            }

            return this;
        }

        public override string ToString() => _name + "(" + string.Join(", ", _parts) + ")";
    }
}
