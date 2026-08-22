using Microsoft.Extensions.Logging;

namespace TextCascade.Server;

public interface IConnectionCoordinator
{
    ILogger Logger { get; }

    void CancelConnection(ConnectionContext connection, string reason);

    void RebuildHub(UserHub hub);

    void RemoveEmptyHubAfterRecovery(UserHub hub);
}
