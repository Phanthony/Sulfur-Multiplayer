using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Sent when a player spawns into the game world.
    /// Host sends this to all clients when a new player joins.
    /// </summary>
    public class PlayerSpawnMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.PlayerSpawn;

        public ulong SteamId;
        public string PlayerName;
        public float PosX, PosY, PosZ;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(SteamId);
            writer.Write(PlayerName ?? "");
            writer.Write(PosX);
            writer.Write(PosY);
            writer.Write(PosZ);
        }

        public override void Deserialize(BinaryReader reader)
        {
            SteamId = reader.ReadUInt64();
            PlayerName = reader.ReadString();
            PosX = reader.ReadSingle();
            PosY = reader.ReadSingle();
            PosZ = reader.ReadSingle();
        }
    }

    /// <summary>
    /// Sent when a player leaves or disconnects.
    /// </summary>
    public class PlayerDespawnMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.PlayerDespawn;

        public ulong SteamId;
        public string Reason;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(SteamId);
            writer.Write(Reason ?? "");
        }

        public override void Deserialize(BinaryReader reader)
        {
            SteamId = reader.ReadUInt64();
            Reason = reader.ReadString();
        }
    }
}
