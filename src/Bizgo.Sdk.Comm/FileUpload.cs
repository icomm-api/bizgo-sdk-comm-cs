using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Bizgo
{
    /// <summary>Brand message image type. Each has its own size and ratio rules (see the API reference).</summary>
    public enum BrandImageKind
    {
        /// <summary><c>default</c>: basic image.</summary>
        Default,

        /// <summary><c>wide</c>: wide image.</summary>
        Wide,

        /// <summary><c>wideItemList</c>: wide list items 2-4.</summary>
        WideItemList,

        /// <summary><c>wideItemList/first</c>: first wide list item.</summary>
        WideItemListFirst,

        /// <summary><c>carouselFeed</c>: carousel feed.</summary>
        CarouselFeed,

        /// <summary><c>carouselCommerce</c>: carousel commerce.</summary>
        CarouselCommerce,
    }

    /// <summary>
    /// A file to upload: a path, bytes or a stream. The content is read once and buffered so a retried upload sends the same bytes.
    /// </summary>
    public sealed class FileUpload
    {
        /// <summary>Largest file the SDK reads (the documented limits are 300KB for MMS, 1MB for RCS and 5MB for brand messages).</summary>
        public const int MaxBytes = 10 * 1024 * 1024;

        private readonly byte[]? _bytes;
        private readonly string? _path;
        private readonly Stream? _stream;

        private FileUpload(string fileName, byte[]? bytes, string? path, Stream? stream)
        {
            FileName = SafeFileName(fileName);
            _bytes = bytes;
            _path = path;
            _stream = stream;
        }

        /// <summary>File name sent in the multipart part (path and control characters removed).</summary>
        public string FileName { get; }

        /// <summary>Content type of the part, from the file extension.</summary>
        public string ContentType => ContentTypeOf(FileName);

        /// <summary>Uploads <paramref name="content"/>. The array is not copied; do not change it until the upload completes.</summary>
        /// <param name="content">File bytes.</param>
        /// <param name="fileName">File name, used for the part's content type (for example <c>banner.jpg</c>).</param>
        /// <returns>The file.</returns>
        public static FileUpload FromBytes(byte[] content, string fileName = "image.jpg")
        {
            if (content is null)
            {
                throw new ArgumentNullException(nameof(content));
            }

            return new FileUpload(fileName, content, null, null);
        }

        /// <summary>Uploads the file at <paramref name="path"/>. It is read when the upload starts.</summary>
        /// <param name="path">File path.</param>
        /// <param name="fileName">File name to send; default: the name part of <paramref name="path"/>.</param>
        /// <returns>The file.</returns>
        public static FileUpload FromPath(string path, string? fileName = null)
        {
            if (path is null)
            {
                throw new ArgumentNullException(nameof(path));
            }

            // no Path.GetFileName: it throws for some invalid paths on .NET Framework; problems are reported when the file is read
            return new FileUpload(fileName ?? path, null, path, null);
        }

        /// <summary>Uploads the rest of <paramref name="stream"/>. It is read when the upload starts and is not disposed.</summary>
        /// <param name="stream">Readable stream.</param>
        /// <param name="fileName">File name (for the content type).</param>
        /// <returns>The file.</returns>
        public static FileUpload FromStream(Stream stream, string fileName = "image.jpg")
        {
            if (stream is null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            return new FileUpload(fileName, null, null, stream);
        }

        /// <inheritdoc/>
        public override string ToString() => "FileUpload(" + FileName + ")";

        internal async Task<byte[]> ReadAsync(int limit, string tooLarge, CancellationToken cancellationToken, string field = "file")
        {
            if (_bytes is not null)
            {
                if (_bytes.Length > limit)
                {
                    throw new BizgoValidationException(field, tooLarge);
                }

                return _bytes;
            }

            try
            {
                if (_path is not null && (_path.Length == 0 || _path.Contains("\0")))
                {
                    throw new IOException("invalid path"); // reported below as a validation error with the file name only
                }

                if (_path is not null)
                {
                    using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 16 * 1024, useAsync: true);
                    return await ReadLimitedAsync(file, limit, tooLarge, field, cancellationToken).ConfigureAwait(false);
                }

                return await ReadLimitedAsync(_stream!, limit, tooLarge, field, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is NotSupportedException || e is ObjectDisposedException || e is System.Security.SecurityException || e is ArgumentException)
            {
                // the OS message and the exception can contain the full path: report the file name only, without chaining
                throw new BizgoValidationException(field, "파일을 읽을 수 없습니다(" + FileName + ", " + e.GetType().Name + ")");
            }
        }

        /// <summary>The file name for a <c>Content-Disposition</c> header: quoted ASCII, or as-is (MIME-encoded by the header class).</summary>
        internal static string Quote(string fileName)
        {
            foreach (var c in fileName)
            {
                if (c > 0x7E)
                {
                    return fileName;
                }
            }

            return "\"" + fileName + "\"";
        }

        internal static string ContentTypeOf(string fileName)
        {
            switch (Path.GetExtension(fileName).ToUpperInvariant())
            {
                case ".JPG":
                case ".JPEG":
                    return "image/jpeg";
                case ".PNG":
                    return "image/png";
                case ".GIF":
                    return "image/gif";
                case ".BMP":
                    return "image/bmp";
                default:
                    return "application/octet-stream";
            }
        }

        private static async Task<byte[]> ReadLimitedAsync(Stream stream, int limit, string tooLarge, string field, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[16 * 1024];
            while (true)
            {
#if NET5_0_OR_GREATER
                var read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), cancellationToken).ConfigureAwait(false);
#else
                var read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken).ConfigureAwait(false);
#endif
                if (read == 0)
                {
                    return buffer.ToArray();
                }

                if (buffer.Length + read > limit)
                {
                    throw new BizgoValidationException(field, tooLarge);
                }

                buffer.Write(chunk, 0, read);
            }
        }

        private static string SafeFileName(string? name)
        {
            var bare = (name ?? string.Empty).Split('/', '\\').LastOrDefault() ?? string.Empty;
            var cleaned = new string(bare.Where(c => !char.IsControl(c) && c != '"').ToArray()).Trim();
            return cleaned.Length == 0 || cleaned == "." || cleaned == ".." ? "image.jpg" : cleaned;
        }
    }
}
