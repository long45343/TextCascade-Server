using System.Net.WebSockets;

namespace TextCascade.Server;

public sealed class ConnectionContext
{
    public string ConnectionId { get; }
    public string Username { get; }
    public string ClientId { get; }
    public string ClientName { get; }
    public WebSocket Socket { get; }
    public UserHub? Hub { get; internal set; }
    public ConnectionStateBag State { get; }

    public ConnectionContext(string connectionId, string username, string clientId, string clientName, WebSocket socket, UserHub? hub, RuntimeConfig config)
    {
        ConnectionId = connectionId;
        Username = username;
        ClientId = clientId;
        ClientName = clientName;
        Socket = socket;
        Hub = hub;
        State = new ConnectionStateBag(config);
    }
}

