using System.Net.WebSockets;

namespace TextCascade.Server;

internal sealed record ReceivedMessage(WebSocketMessageType MessageType, byte[] Payload);
