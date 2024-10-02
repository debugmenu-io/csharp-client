using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace DebugMenuIO.Client {
    public class DebugMenuWebSocketClient : IDebugMenuClient, IDisposable {
        private readonly string url;
        private readonly string token;
        private readonly Dictionary<string, string> metadata;
        private ClientWebSocket socket;

        private readonly CancellationTokenSource disposedCancellationTokenSource = new();
        private readonly CancellationToken disposedCancellationToken;
        private readonly Func<Task> connectedCallback;

        [Serializable]
        public class Message {
            public string channel;
            public object payload;
        }

        public DebugMenuWebSocketClient(string url, string token, Dictionary<string, string> metadata,
            Func<Task> connectedCallback) {
            this.url = url;
            this.token = token;
            this.metadata = metadata;
            this.connectedCallback = connectedCallback;
            socket = new ClientWebSocket();
            disposedCancellationToken = disposedCancellationTokenSource.Token;
        }

        public ConnectionStatus ConnectionStatus { get; private set; } = ConnectionStatus.Waiting;

        public Task SendJson(string channel, object payload, CancellationToken cancellationToken) {
            if(!IsSocketAlive()) {
                return Task.CompletedTask;
            }

            var jsonString = JsonConvert.SerializeObject(new Message() {
                channel = channel,
                payload = payload
            });
            var bytes = Encoding.UTF8.GetBytes(jsonString);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(disposedCancellationToken, cancellationToken);
            return socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
        }

        public Task SendBytes(string channel, byte[] payload, CancellationToken cancellationToken) {
            return SendBytes(channel, payload, 0, payload.Length, cancellationToken);
        }

        public Task SendBytes(string channel, byte[] payload, int payloadIndex, int payloadLength,
            CancellationToken cancellationToken) {
            if(!IsSocketAlive()) {
                return Task.CompletedTask;
            }

            var channelBytes = Encoding.UTF8.GetBytes(channel);
            if(channelBytes.Length > byte.MaxValue) {
                throw new Exception($"Channel name too long. Length is {channelBytes.Length}, max is {byte.MaxValue}.");
            }

            var bytes = new byte[payload.Length + 1 + channelBytes.Length];
            var stream = new MemoryStream(bytes);
            var writer = new BinaryWriter(stream);


            writer.Write((byte)channelBytes.Length);
            writer.Write(channelBytes);
            writer.Write(payload, payloadIndex, payloadLength);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(disposedCancellationToken, cancellationToken);
            var segment = new ArraySegment<byte>(bytes);
            return socket.SendAsync(segment, WebSocketMessageType.Binary, true, cts.Token);
        }

        public event Action<JsonMessage> ReceivedJson;
        public event Action<BinaryMessage> ReceivedBytes;
        public event Action<Exception> ErrorOccurred;

        public Task UpdateSchema(ApiSchema schema) {
            var settings = new JsonSerializerSettings {
                ContractResolver = new CamelCasePropertyNamesContractResolver(),
                NullValueHandling = NullValueHandling.Ignore
            };
            var json = JsonConvert.SerializeObject(schema, settings);

            return SendBytes("__internal/api",
                Encoding.UTF8.GetBytes(json),
                disposedCancellationToken).ContinueWith(t => {
                if(t.IsFaulted) {
                    ErrorOccurred?.Invoke(t.Exception);
                }
            }, disposedCancellationToken);
        }

        public async Task Run(CancellationToken ct) {
            var linkedCancellationToken = disposedCancellationToken;

            if(ct != CancellationToken.None) {
                linkedCancellationToken =
                    CancellationTokenSource.CreateLinkedTokenSource(ct, disposedCancellationToken).Token;
            }

            var buffer = new byte[4096 * 20];
            var stream = new MemoryStream(buffer);

            var expandableStream = new MemoryStream();
            var writer = new BinaryWriter(expandableStream);

            while(!linkedCancellationToken.IsCancellationRequested) {
                try {
                    await TryReconnect(linkedCancellationToken);

                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), linkedCancellationToken);
                    if(result.EndOfMessage) {
                        var streamToUse = expandableStream.Position == 0 ? stream : expandableStream;

                        switch(result.MessageType) {
                        case WebSocketMessageType.Binary:
                            HandleBinaryMessage(streamToUse, buffer, result);
                            break;
                        case WebSocketMessageType.Text:
                            HandleTextMessage(streamToUse, buffer, result);
                            break;
                        }

                        expandableStream.Seek(0, SeekOrigin.Begin);
                    }
                    else {
                        writer.Write(buffer, 0, result.Count);
                    }
                }
                catch(OperationCanceledException) {
                    return;
                }
                catch(Exception e) {
                    ErrorOccurred?.Invoke(e);
                }

                if(!IsSocketAlive()) {
                    ConnectionStatus = ConnectionStatus.Disconnected;

                    await Task.Delay(2000, linkedCancellationToken);
                }
            }
        }

        private async Task TryReconnect(CancellationToken cancellationToken) {
            if(!IsSocketAlive()) {
                ConnectionStatus = ConnectionStatus.Connecting;

                socket = new ClientWebSocket();
                await socket.ConnectAsync(new Uri(url), cancellationToken);

                ConnectionStatus = ConnectionStatus.PerformingHandshake;

                var tokenBytes = Encoding.UTF8.GetBytes(token);
                await socket.SendAsync(new ArraySegment<byte>(tokenBytes), WebSocketMessageType.Text, true,
                    cancellationToken);

                var metadataJson = JsonConvert.SerializeObject(metadata);
                var metadataBytes = Encoding.UTF8.GetBytes(metadataJson);
                await socket.SendAsync(new ArraySegment<byte>(metadataBytes), WebSocketMessageType.Text, true,
                    cancellationToken);

                var task = connectedCallback?.Invoke();
                if(task != null) {
                    await task;
                }

                ConnectionStatus = ConnectionStatus.Connected;
            }
        }

        private bool IsSocketAlive() {
            return socket is { CloseStatus: null } && socket.State != WebSocketState.Aborted
                                                   && socket.State != WebSocketState.None
                                                   && socket.State != WebSocketState.Closed
                                                   && socket.State != WebSocketState.Connecting;
        }

        private void HandleTextMessage(MemoryStream stream, byte[] buffer, WebSocketReceiveResult result) {
            stream.Seek(0, SeekOrigin.Begin);
            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            try {
                var document = JObject.Parse(text);
                var channel = document.GetValue("channel")!.Value<string>();
                var payload = document.GetValue("payload")!.Value<JObject>();
                ReceivedJson?.Invoke(new JsonMessage() {
                    Channel = channel!,
                    Payload = payload!
                });
            }
            catch(Exception e) {
                ErrorOccurred?.Invoke(e);
                throw;
            }
        }

        private void HandleBinaryMessage(MemoryStream stream, byte[] buffer,
            WebSocketReceiveResult result) {
            stream.Seek(0, SeekOrigin.Begin);
            var reader = new BinaryReader(stream);
            var channelLength = reader.ReadByte();

            var segment = new ArraySegment<byte>(buffer, 1 + channelLength, result.Count - 1 - channelLength);
            var channel = Encoding.UTF8.GetString(buffer, segment.Offset, segment.Count);
            ReceivedBytes?.Invoke(new BinaryMessage() {
                Channel = channel,
                Payload = segment,
            });
        }

        public void Dispose() {
            disposedCancellationTokenSource.Cancel();
            socket.Dispose();
        }
    }
}
