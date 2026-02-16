using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Sent by client to host immediately after connection.
    /// Contains mod version for compatibility check.
    /// </summary>
    public class HandshakeMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.Handshake;

        public string ModVersion;
        public string PlayerName;
        public string Password;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(ModVersion ?? "");
            writer.Write(PlayerName ?? "");
            writer.Write(Password ?? "");
        }

        public override void Deserialize(BinaryReader reader)
        {
            ModVersion = reader.ReadString();
            PlayerName = reader.ReadString();
            Password = reader.ReadString();
        }
    }

    /// <summary>
    /// Sent by host to client in response to Handshake.
    /// Contains accept/reject and host info.
    /// </summary>
    public class HandshakeResponseMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.HandshakeResponse;

        public bool Accepted;
        public string RejectReason;
        public string HostName;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(Accepted);
            writer.Write(RejectReason ?? "");
            writer.Write(HostName ?? "");
        }

        public override void Deserialize(BinaryReader reader)
        {
            Accepted = reader.ReadBoolean();
            RejectReason = reader.ReadString();
            HostName = reader.ReadString();
        }
    }
}
