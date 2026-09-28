using System;
using System.IO;
using Bizgo;
using Bizgo.Examples;

// Every example connects to the SANDBOX (no real delivery) and reads its settings from environment variables.
// Never hard-code API keys or phone numbers. See examples/README.md.
if (args.Length == 0)
{
    Console.WriteLine("usage: dotnet run --project examples/Bizgo.Examples -- <send-sms|send-alimtalk-fallback|send-mms|poll-reports|message-history|concurrent-send|bulk-send|alimtalk-templates|webhook-server>");
    return 1;
}

if (args[0] == "webhook-server")
{
    await WebhookServer.RunAsync(Env.Require("BIZGO_WEBHOOK_SECRET"), "http://127.0.0.1:8080/");
    return 0;
}

using var client = new BizgoClient(new BizgoClientOptions { Environment = BizgoEnvironment.Sandbox }); // API key from BIZGO_API_KEY
switch (args[0])
{
    case "send-sms":
        await SendSms.RunAsync(client, Env.Require("BIZGO_FROM"), Env.Require("BIZGO_TO"));
        break;
    case "send-alimtalk-fallback":
        await SendAlimtalkFallback.RunAsync(client, Env.Require("BIZGO_FROM"), Env.Require("BIZGO_TO"), Env.Require("BIZGO_KAKAO_SENDER_KEY"), Env.Require("BIZGO_KAKAO_TEMPLATE_CODE"));
        break;
    case "send-mms":
        await SendMms.RunAsync(client, Env.Require("BIZGO_FROM"), Env.Require("BIZGO_TO"), await File.ReadAllBytesAsync(Env.Require("BIZGO_IMAGE")));
        break;
    case "poll-reports":
        await PollReports.RunAsync(client);
        break;
    case "message-history":
        await MessageHistory.RunAsync(client);
        break;
    case "bulk-send":
        await BulkSend.RunAsync(client, Env.Require("BIZGO_FROM"), new[] { Env.Require("BIZGO_TO") }, "example-campaign");
        break;
    case "alimtalk-templates":
        await AlimtalkTemplates.RunAsync(client, Env.Require("BIZGO_KAKAO_SENDER_KEY"));
        break;
    case "concurrent-send":
        await ConcurrentSend.RunAsync(client, Env.Require("BIZGO_FROM"), new[] { new[] { Env.Require("BIZGO_TO") } });
        break;
    default:
        Console.WriteLine("unknown example: " + args[0]);
        return 1;
}

return 0;
