using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Ping message for RTT measurement. Sender records timestamp, receiver echoes it back as Pong.
    /// </summary>
    public class PingMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.Ping;

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

    /// <summary>
    /// Pong response to a Ping. Contains the original timestamp for RTT calculation.
    /// </summary>
    public class PongMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.Pong;

        public float OriginalTimestamp;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(OriginalTimestamp);
        }

        public override void Deserialize(BinaryReader reader)
        {
            OriginalTimestamp = reader.ReadSingle();
        }
    }
}
