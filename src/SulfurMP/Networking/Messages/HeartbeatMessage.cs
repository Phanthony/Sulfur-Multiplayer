using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Lightweight keepalive message sent periodically.
    /// </summary>
    public class HeartbeatMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.Heartbeat;

        public float Timestamp;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(Timestamp);
        }

        public override void Deserialize(BinaryReader reader)
        {
            Timestamp = reader.ReadSingle();
        }
    }
}
