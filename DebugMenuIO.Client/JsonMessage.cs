using Newtonsoft.Json.Linq;

namespace DebugMenuIO.Client;

public struct JsonMessage {
    public string Channel;
    public JObject Payload;
}
