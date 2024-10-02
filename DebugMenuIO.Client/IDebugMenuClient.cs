using System;
using System.Threading;
using System.Threading.Tasks;

namespace DebugMenuIO.Client {
    public interface IDebugMenuClient {
        ConnectionStatus ConnectionStatus { get; }

        Task SendJson(string channel, object payload, CancellationToken cancellationToken);
        Task SendBytes(string channel, byte[] payload, int payloadIndex, int payloadLength, CancellationToken cancellationToken);
        Task SendBytes(string channel, byte[] payload, CancellationToken cancellationToken);

        event Action<JsonMessage> ReceivedJson;
        event Action<BinaryMessage> ReceivedBytes;
        event Action<Exception> ErrorOccurred;

        Task UpdateSchema(ApiSchema schema);
    }
}
