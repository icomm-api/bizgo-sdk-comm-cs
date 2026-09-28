using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Bizgo.Models;
using Xunit;

namespace Bizgo.Tests
{
    /// <summary>
    /// <c>x-sdk-required-if</c> (SDK-DESIGN §4): conditionally required fields are generated from the spec and checked before anything is sent.
    /// </summary>
    public class RequiredIfTests
    {
        private const string SendPath = "/api/comm/v1/send/omni";
        private const string Text = "#{name}님 주문(ORDER-SECRET-TEXT)이 접수되었습니다";

        private static Destination Recipient(bool replaceWords = false) => new Destination(Placeholders.OtherPhone)
        {
            ReplaceWords = replaceWords ? new Dictionary<string, string> { ["name"] = "REPLACE-VALUE" } : null,
        };

        [Fact]
        public async Task Alimtalk_full_send_without_msgType_fails_before_any_http_call()
        {
            using var t = new TestClient();

            // sendType unset = full send: the server would answer A523 (sandbox 2026-09-28)
            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Send.OmniAsync(
                Recipient(),
                new IChannelMessage[] { new AlimtalkMessage { SenderKey = Placeholders.SenderKey, TemplateCode = Placeholders.TemplateCode } }));

            var paths = error.Errors.Select(e => e.Path).ToArray();
            Assert.Contains("messageFlow[0].alimtalk.msgType", paths);
            Assert.Contains("messageFlow[0].alimtalk.text", paths);
            Assert.All(error.Errors, e => Assert.Contains("sendType != template", e.Reason, StringComparison.Ordinal));
            Assert.Empty(t.Handler.Requests);
        }

        [Fact]
        public async Task Alimtalk_full_send_with_msgType_and_text_is_sent()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Ok(Responses.Accepted("A000")));

            await t.Client.Send.OmniAsync(
                Recipient(),
                new IChannelMessage[] { new AlimtalkMessage { SenderKey = Placeholders.SenderKey, TemplateCode = Placeholders.TemplateCode, MsgType = "AT", Text = Text } });

            Assert.Equal(1, t.Handler.Count(HttpMethod.Post, SendPath));
        }

        [Fact]
        public async Task Alimtalk_template_send_needs_replaceWords_on_every_destination()
        {
            using var t = new TestClient();
            var message = new AlimtalkMessage { SenderKey = Placeholders.SenderKey, TemplateCode = Placeholders.TemplateCode, SendType = "template" };

            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Send.OmniAsync(
                new[] { Recipient(replaceWords: true), Recipient() },
                new IChannelMessage[] { message }));

            var only = Assert.Single(error.Errors);
            Assert.Equal("destinations[1].replaceWords", only.Path); // "$." path: from the request root
            Assert.Contains("messageFlow[0].alimtalk.sendType == template", only.Reason, StringComparison.Ordinal);
            Assert.Empty(t.Handler.Requests);
        }

        [Fact]
        public async Task Alimtalk_template_send_with_replaceWords_does_not_need_msgType()
        {
            using var t = new TestClient();
            t.Handler.On(HttpMethod.Post, SendPath, Responses.Ok(Responses.Accepted("A000")));

            await t.Client.Send.OmniAsync(
                Recipient(replaceWords: true),
                new IChannelMessage[] { new AlimtalkMessage { SenderKey = Placeholders.SenderKey, TemplateCode = Placeholders.TemplateCode, SendType = "template" } });

            var alimtalk = t.Handler.Last.Json["messageFlow"]![0]!["alimtalk"]!.AsObject();
            Assert.False(alimtalk.ContainsKey("msgType"));
            Assert.Equal(1, t.Handler.Count(HttpMethod.Post, SendPath));
        }

        [Fact]
        public void Root_paths_are_skipped_when_a_channel_model_is_validated_alone()
        {
            // "$.destinations[]" belongs to the request body; the message alone has no destinations to check
            new AlimtalkMessage { SenderKey = Placeholders.SenderKey, TemplateCode = Placeholders.TemplateCode, SendType = "template" }.Validate();
        }

        [Fact]
        public void Json_request_bodies_are_checked_too()
        {
            var json = "{\"destinations\":[{\"to\":\"01000000000\"}],\"messageFlow\":[{\"alimtalk\":{\"senderKey\":\"SENDER_KEY_EXAMPLE\",\"templateCode\":\"TEMPLATE_CODE_EXAMPLE\",\"text\":\"ORDER-SECRET-TEXT\"}}]}";

            var error = Assert.Throws<BizgoValidationException>(() => SendOmniRequest.FromJson(json));

            Assert.Equal("messageFlow[0].alimtalk.msgType", Assert.Single(error.Errors).Path);
        }

        [Fact]
        public async Task Bulk_reports_root_paths_with_indexes_of_the_whole_list()
        {
            using var t = new TestClient();
            var recipients = Enumerable.Range(0, 5).Select(i => Recipient(replaceWords: i != 3)).ToList();

            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Send.BulkAsync(
                recipients,
                new IChannelMessage[] { new AlimtalkMessage { SenderKey = Placeholders.SenderKey, TemplateCode = Placeholders.TemplateCode, SendType = "template" } },
                new BulkSendOptions { ChunkSize = 2 }));

            Assert.Equal("destinations[3].replaceWords", Assert.Single(error.Errors).Path);
            Assert.Empty(t.Handler.Requests);
        }

        [Fact]
        public void Brand_message_web_link_button_needs_pc_and_mobile_urls()
        {
            var error = Assert.Throws<BizgoValidationException>(() => new BrandMessageButton { Type = "WL", Name = "BUTTON-NAME" }.Validate());
            Assert.Equal(new[] { "urlPc", "urlMobile" }, error.Errors.Select(e => e.Path).ToArray());
            Assert.All(error.Errors, e => Assert.Equal("type == WL일 때 필수입니다", e.Reason));

            var app = Assert.Throws<BizgoValidationException>(() => new BrandMessageButton { Type = "AL", Name = "BUTTON-NAME" }.Validate());
            Assert.Equal("urlMobile", Assert.Single(app.Errors).Path);

            new BrandMessageButton { Type = "WL", Name = "BUTTON-NAME", UrlPc = "https://example.com", UrlMobile = "https://m.example.com" }.Validate();
            new BrandMessageButton { Type = "BK", Name = "BUTTON-NAME" }.Validate();
        }

        [Fact]
        public async Task Rcs_header_needs_footer()
        {
            using var t = new TestClient();
            RcsMessage Rcs(string? footer) => new RcsMessage { From = Placeholders.Phone, FormatId = "FORMAT_ID_EXAMPLE", BrandKey = "BRAND_KEY_EXAMPLE", Body = new RcsBody(), Header = "1", Footer = footer };

            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Send.OmniAsync(Recipient(), new IChannelMessage[] { Rcs(null) }));

            var only = Assert.Single(error.Errors);
            Assert.Equal("messageFlow[0].rcs.footer", only.Path);
            Assert.Equal("header == 1일 때 필수입니다", only.Reason);
            Assert.Empty(t.Handler.Requests);

            Rcs("080-0000-0000").Validate();
        }

        [Fact]
        public async Task Counsel_file_message_needs_file_name_and_size()
        {
            using var t = new TestClient();
            var request = new CounselPlainMessageRequest
            {
                UserKey = "USER_KEY_EXAMPLE",
                SenderKey = Placeholders.SenderKey,
                MsgType = "FILE",
                Message = "FILE-MESSAGE-TEXT",
                Attachment = new CounselPlainAttachment { File = new CounselFile { FileUrl = "https://example.com/f.pdf" } },
            };

            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Counsel.Messages.SendPlainAsync(request));

            Assert.Equal(new[] { "attachment.file.fileName", "attachment.file.fileSize" }, error.Errors.Select(e => e.Path).ToArray());
            Assert.Empty(t.Handler.Requests);

            request.Attachment = null; // a missing intermediate object means the path is missing
            var missing = Assert.Throws<BizgoValidationException>(() => request.Validate());
            Assert.Equal(new[] { "attachment.file.fileName", "attachment.file.fileSize" }, missing.Errors.Select(e => e.Path).ToArray());
        }

        [Fact]
        public async Task Conditional_errors_contain_no_values()
        {
            using var t = new TestClient();

            var error = await Assert.ThrowsAsync<BizgoValidationException>(() => t.Client.Send.OmniAsync(
                new[] { Recipient() },
                new IChannelMessage[]
                {
                    new AlimtalkMessage { SenderKey = Placeholders.SenderKey, TemplateCode = Placeholders.TemplateCode, Text = Text },
                    new AlimtalkMessage { SenderKey = Placeholders.SenderKey, TemplateCode = Placeholders.TemplateCode, SendType = "template", Title = "TITLE-VALUE" },
                }));

            Assert.Equal(new[] { "messageFlow[0].alimtalk.msgType", "destinations[0].replaceWords" }, error.Errors.Select(e => e.Path).ToArray());
            var text = error.ToString() + error.Message + string.Join("|", error.Errors.Select(e => e.Path + e.Reason));
            foreach (var value in new[] { Placeholders.OtherPhone, Placeholders.SenderKey, Placeholders.TemplateCode, "ORDER-SECRET-TEXT", "TITLE-VALUE" })
            {
                Assert.DoesNotContain(value, text, StringComparison.Ordinal);
            }
        }
    }
}
