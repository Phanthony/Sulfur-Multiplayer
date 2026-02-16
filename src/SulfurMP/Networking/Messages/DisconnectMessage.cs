using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Sent when a peer intentionally disconnects. Contains a reason string.
    /// </summary>
    public class DisconnectMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.Disconnect;

        public string Reason;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(Reason ?? "");
        }

        public override void Deserialize(BinaryReader reader)
        {
            Reason = reader.ReadString();
        }
    }
}
