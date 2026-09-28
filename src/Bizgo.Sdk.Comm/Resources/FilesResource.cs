using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Bizgo.Internal;
using Bizgo.Models;

namespace Bizgo
{
    /// <summary>Image uploads (<c>POST /api/comm/v1/file/*</c>). Access as <c>client.Files</c>. Uploads are retried only on HTTP 429.</summary>
    public sealed partial class FilesResource
    {
        /// <summary>Largest MMS image (300KB = 307,200 bytes).</summary>
        public const int MmsMaxBytes = 300 * 1024;

        private const string MmsTooLarge = "MMS 이미지는 최대 300KB입니다(jpg, 권장 1,500×1,440px 이하).";
        private const string TooLarge = "파일이 너무 큽니다(SDK 상한 10MB).";

        private readonly Transport _transport;

        internal FilesResource(Transport transport)
        {
            _transport = transport;
        }

        /// <summary>Uploads an MMS image (jpg, max 300KB). Use <see cref="FileUploadResult.FileKey"/> in <see cref="MmsMessage.FileKey"/>.</summary>
        /// <param name="file">The image.</param>
        /// <param name="fileKey">Optional key to identify the file; the server generates one when omitted.</param>
        /// <param name="imageName">Optional file name; default: the file name without extension.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The file key and its expiry.</returns>
        /// <exception cref="BizgoValidationException">The file is larger than 300KB (checked before sending).</exception>
        public async Task<FileUploadResult> UploadMmsAsync(FileUpload file, string? fileKey = null, string? imageName = null, CancellationToken cancellationToken = default)
        {
            var content = await Read(file, MmsMaxBytes, MmsTooLarge, cancellationToken).ConfigureAwait(false);
            var response = await _transport.SendAsync(BizgoOperation.UploadMmsFile, "/api/comm/v1/file/mms", null, Form(file, content, fileKey, imageName), RetryPolicy.RateLimitOnly, 1, null, BizgoJsonContext.Default.FileUploadResponse, cancellationToken).ConfigureAwait(false);
            return response.Data?.Data ?? new FileUploadResult();
        }

        /// <summary>Uploads an RCS image. Use <see cref="RcsFileUploadResult.Media"/> in <see cref="RcsBody.Media"/>.</summary>
        /// <param name="file">The image.</param>
        /// <param name="fileKey">Optional key to identify the file.</param>
        /// <param name="imageName">Optional file name.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The media key and its expiry.</returns>
        public async Task<RcsFileUploadResult> UploadRcsAsync(FileUpload file, string? fileKey = null, string? imageName = null, CancellationToken cancellationToken = default)
        {
            var content = await Read(file, FileUpload.MaxBytes, TooLarge, cancellationToken).ConfigureAwait(false);
            var response = await _transport.SendAsync(BizgoOperation.UploadRcsFile, "/api/comm/v1/file/rcs", null, Form(file, content, fileKey, imageName), RetryPolicy.RateLimitOnly, 1, null, BizgoJsonContext.Default.RcsFileUploadResponse, cancellationToken).ConfigureAwait(false);
            return response.Data?.Data ?? new RcsFileUploadResult();
        }

        /// <summary>Uploads a Kakao brand message image. Use <see cref="BrandMessageFileUploadResult.ImgUrl"/> in the message.</summary>
        /// <param name="file">The image.</param>
        /// <param name="kind">Image type; each has its own size and ratio rules.</param>
        /// <param name="fileKey">Optional key to identify the file.</param>
        /// <param name="imageName">Optional file name.</param>
        /// <param name="cancellationToken">Cancellation.</param>
        /// <returns>The image URL.</returns>
        /// <exception cref="BizgoValidationException"><paramref name="kind"/> is not a defined <see cref="BrandImageKind"/>.</exception>
        public async Task<BrandMessageFileUploadResult> UploadBrandMessageAsync(FileUpload file, BrandImageKind kind = BrandImageKind.Default, string? fileKey = null, string? imageName = null, CancellationToken cancellationToken = default)
        {
            var path = "/api/comm/v1/file/brandmessage/" + KindPath(kind);
            var content = await Read(file, FileUpload.MaxBytes, TooLarge, cancellationToken).ConfigureAwait(false);
            var response = await _transport.SendAsync(KindOperation(kind), path, null, Form(file, content, fileKey, imageName), RetryPolicy.RateLimitOnly, 1, null, BizgoJsonContext.Default.BrandMessageFileUploadResponse, cancellationToken).ConfigureAwait(false);
            return response.Data?.Data ?? new BrandMessageFileUploadResult();
        }

        /// <inheritdoc/>
        public override string ToString() => "FilesResource";

        internal static string KindPath(BrandImageKind kind)
        {
            switch (kind)
            {
                case BrandImageKind.Default:
                    return "default";
                case BrandImageKind.Wide:
                    return "wide";
                case BrandImageKind.WideItemList:
                    return "wideItemList";
                case BrandImageKind.WideItemListFirst:
                    return "wideItemList/first";
                case BrandImageKind.CarouselFeed:
                    return "carouselFeed";
                case BrandImageKind.CarouselCommerce:
                    return "carouselCommerce";
                default:
                    throw new BizgoValidationException("kind", "지원하지 않는 브랜드메시지 이미지 종류입니다");
            }
        }

        internal static BizgoOperation KindOperation(BrandImageKind kind)
        {
            switch (kind)
            {
                case BrandImageKind.Default:
                    return BizgoOperation.UploadBrandMessageDefaultImage;
                case BrandImageKind.Wide:
                    return BizgoOperation.UploadBrandMessageWideImage;
                case BrandImageKind.WideItemList:
                    return BizgoOperation.UploadBrandMessageWideItemListImage;
                case BrandImageKind.WideItemListFirst:
                    return BizgoOperation.UploadBrandMessageWideItemListFirstImage;
                case BrandImageKind.CarouselFeed:
                    return BizgoOperation.UploadBrandMessageCarouselFeedImage;
                case BrandImageKind.CarouselCommerce:
                    return BizgoOperation.UploadBrandMessageCarouselCommerceImage;
                default:
                    throw new BizgoValidationException("kind", "지원하지 않는 브랜드메시지 이미지 종류입니다");
            }
        }

        private static Task<byte[]> Read(FileUpload file, int limit, string tooLarge, CancellationToken cancellationToken)
        {
            if (file is null)
            {
                throw new ArgumentNullException(nameof(file));
            }

            return file.ReadAsync(limit, tooLarge, cancellationToken);
        }

        /// <summary>Multipart body built from buffered bytes, so every retry sends the same content.</summary>
        private static Func<HttpContent> Form(FileUpload file, byte[] content, string? fileKey, string? imageName)
        {
            var fileName = file.FileName;
            var contentType = file.ContentType;
            return () =>
            {
                var form = new MultipartFormDataContent();
                var part = new ByteArrayContent(content);
                part.Headers.ContentType = new MediaTypeHeaderValue(contentType);
                part.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"file\"", FileName = FileUpload.Quote(fileName) };
                form.Add(part);
                AddField(form, "fileKey", fileKey);
                AddField(form, "imageName", imageName);
                return form;
            };
        }

        private static void AddField(MultipartFormDataContent form, string name, string? value)
        {
            if (value is null)
            {
                return;
            }

            var field = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
            field.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"" + name + "\"" };
            form.Add(field);
        }
    }
}
