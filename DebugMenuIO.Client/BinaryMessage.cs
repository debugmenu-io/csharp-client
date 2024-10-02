using System;

namespace DebugMenuIO.Client;

public class BinaryMessage {
    public string Channel;
    public ArraySegment<byte> Payload;
}
