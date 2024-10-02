#nullable enable
namespace DebugMenuIO.Client {
    public enum ConnectionStatus {
        Waiting,
        Connecting,
        PerformingHandshake,
        Connected,
        Disconnected
    }
}
