using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text;

namespace Miosuke.Messages;

public static class Chat
{
    private static readonly ushort orangeColourKey = 557;



    public static void PluginMessage(string message, ushort? prefixColourKey = null) =>
        PluginMessage([new TextPayload(message)], prefixColourKey);
    public static void PluginMessage(List<Payload> payloadList, ushort? prefixColourKey = null) =>
        PluginMessage(payloadList, MiosukeHelper.PluginNameShortPayload, prefixColourKey);
    public static void PluginMessage(List<Payload> payloadList, DalamudLinkPayload? prefixPayload, ushort? prefixColourKey = null) =>
        PluginMessage(XivChatType.Debug, MiosukeHelper.PluginNameShort, payloadList, prefixPayload, prefixColourKey);
    public static void PluginMessage(XivChatType channel, string prefix, List<Payload> payloadList, DalamudLinkPayload? prefixPayload = null, ushort? prefixColourKey = null)
    {
        var payloads = new List<Payload> {
            new UIForegroundPayload(prefixColourKey ?? orangeColourKey),
            new TextPayload(prefix),
            new UIForegroundPayload(0),
        };

        if (prefixPayload is not null)
        {
            payloads.Insert(1, prefixPayload);
            payloads.Insert(3, RawPayload.LinkTerminator);
        }

        Service.Chat.Print(new XivChatEntry
        {
            Message = new SeString(payloads.Concat(payloadList).ToList()),
            Type = channel,
        });
    }

    public static void Message(XivChatType channel, List<Payload> payloadList)
    {
        Service.Chat.Print(new XivChatEntry
        {
            Message = new SeString(payloadList),
            Type = channel,
        });
    }
}
