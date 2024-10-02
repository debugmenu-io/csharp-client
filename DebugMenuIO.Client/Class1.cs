#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;

namespace DebugMenuIO.Client {
    public class DebugMenuClient : IDisposable {
        private readonly string url;
        private readonly string token;
        private readonly Dictionary<string, string> metadata;

        private DebugMenuWebSocketClient? webSocketClient;
        private Task? clientTask;
        private readonly Dictionary<string, DebugMenuChannelHandler> handlers = new();

        public DebugMenuClient(string url, string token, Dictionary<string, string> metadata) {
            this.url = url;
            this.token = token;
            this.metadata = metadata;
        }

        private Task OnConnected() {
            TryUpdateSchema();
            return Task.CompletedTask;
        }


        [Serializable]
        private class CreateRunningInstanceRequest {
            public string Token { get; set; } = string.Empty;
            public Dictionary<string, string> Metadata { get; set; } = new();
        }

        [Serializable]
        public class RunningInstance {
            public string Id { get; set; } = string.Empty;
            public string? DeviceId { get; set; }
            public string? WebsocketUrl { get; set; }
            public int ConnectedViewers { get; set; }
            public bool HasConnectedInstance { get; set; }
            public int ApplicationId { get; set; }
        }

        public async Task<RunningInstance> Run(CancellationToken cancellationToken,
            RunningInstance? runningInstance = null) {
            if(clientTask != null) {
                throw new InvalidOperationException($"{nameof(DebugMenuClient)} is already running.");
            }

            if(runningInstance == null) {
                runningInstance = await RequestInstance(cancellationToken);
            }

            webSocketClient =
                new DebugMenuWebSocketClient(runningInstance!.WebsocketUrl! + "/instance", token, metadata,
                    OnConnected);

            webSocketClient.ReceivedJson += OnReceivedJson;

            clientTask = webSocketClient.Run(cancellationToken);

            return runningInstance;
        }

        private async Task<RunningInstance> RequestInstance(CancellationToken cancellationToken) {
            var body = JsonConvert.SerializeObject(new CreateRunningInstanceRequest() {
                Token = token,
                Metadata = metadata
            });
            var content = new StringContent(body, Encoding.UTF8, "application/json");
            var client = new HttpClient();

            var response = await client.PostAsync(url + "/api/instances", content, cancellationToken);

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            return JsonConvert.DeserializeObject<RunningInstance>(responseJson)!;
        }

         private void TryUpdateSchema() {
             if(webSocketClient != null && webSocketClient.ConnectionStatus == ConnectionStatus.Connected) {
                 var api = BuildApi();

                 var _ = webSocketClient.SendBytes("__internal/api",
                     Encoding.UTF8.GetBytes(api),
                     CancellationToken.None);
             }
         }

         private string BuildApi() {
             var document = new ApiSchema() {
                 DebugMenuApi = "1.0.0",
                 Channels = handlers.ToDictionary(
                     kvp => kvp.Key,
                     kvp => kvp.Value.GetSchema())
             };

             foreach(var kvp in explicitSchemas) {
                 document.Channels.Add(kvp.Key, kvp.Value);
             }

             var settings = new JsonSerializerSettings {
                 ContractResolver = new CamelCasePropertyNamesContractResolver(),
                 NullValueHandling = NullValueHandling.Ignore
             };
             return JsonConvert.SerializeObject(document, settings);
         }

        private void OnReceivedJson(JsonMessage message) {
            if(handlers.TryGetValue(message.Channel.ToLowerInvariant(), out var handler)) {
                var returnValue = handler.HandleMessage(message.Payload);
                if(returnValue != null) {
                    webSocketClient?.SendJson(message.Channel, returnValue, CancellationToken.None);
                }
            }
        }

        public void Dispose() {
            webSocketClient?.Dispose();
            webSocketClient = null;
            clientTask = null;
        }

        private readonly Dictionary<string, Channel> explicitSchemas = new();

        public void AddExplicitSchema(string channel, Channel schema) {
            explicitSchemas.Add(channel, schema);
            TryUpdateSchema();
        }

        public void SendLog(string channel, string message, string type, string details, long timestamp) {
            webSocketClient?.SendJson(channel, new {
                message = message,
                type = type,
                details = details,
                timestamp = timestamp
            }, CancellationToken.None);
        }

        public void RegisterController(object controller) {
            var type = controller.GetType();

            var controllerAttribute = type.GetCustomAttribute<ControllerAttribute>();
            if(controllerAttribute == null) {
                return;
            }

            var handlers = RegisterMethods(controller, type);
            foreach(var handler in handlers) {
                this.handlers.Add(handler.Channel.ToLowerInvariant(), handler);
            }

            TryUpdateSchema();
        }

        private List<DebugMenuChannelHandler> RegisterMethods(object controller, Type type) {
            var handlers = new List<DebugMenuChannelHandler>();
            var methods = type.GetMethods();

            foreach(var methodInfo in methods) {
                var buttonAttr = methodInfo.GetCustomAttribute<ButtonAttribute>();
                if(buttonAttr != null) {
                    handlers.Add(new ButtonDebugMenuChannelHandler(GetChannel(controller, methodInfo), controller,
                        methodInfo, buttonAttr));
                    continue;
                }

                var toggleAttr = methodInfo.GetCustomAttribute<ToggleAttribute>();
                if(toggleAttr != null) {
                    handlers.Add(new ToggleDebugMenuChannelHandler(GetChannel(controller, methodInfo), controller,
                        methodInfo, toggleAttr));
                }

                var textFieldAttr = methodInfo.GetCustomAttribute<TextFieldAttribute>();
                if(textFieldAttr != null) {
                    handlers.Add(new TextFieldDebugMenuChannelHandler(GetChannel(controller, methodInfo), controller,
                        methodInfo, textFieldAttr));
                }
            }

            return handlers;
        }

        private string GetChannel(object instance, MethodInfo methodInfo) {
            var type = instance.GetType();
            var controllerAttribute = type.GetCustomAttribute<ControllerAttribute>();
            var methodAttribute = methodInfo.GetCustomAttribute<DebugMenuChannelAttribute>();

            return $"{controllerAttribute?.Path ?? type.Name}/{methodAttribute?.Path ?? methodInfo.Name}";
        }
    }

    public abstract class DebugMenuChannelHandler {
        public string Channel { get; }
        public abstract string Type { get; }

        protected DebugMenuChannelHandler(string channel) {
            Channel = channel;
        }

        public abstract Channel GetSchema();
        public abstract object? HandleMessage(JObject payload);
    }
}
