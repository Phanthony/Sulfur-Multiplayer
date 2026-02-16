using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Broadcast when a player dies or respawns in multiplayer.
    /// Used to track dead players for spectate mode and all-dead detection.
    /// </summary>
    public class PlayerDeathMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.PlayerDeath;
        public override bool Reliable => true;

        /// <summary>SteamID of the player who died/respawned.</summary>
        public ulong SteamId;

        /// <summary>True = player died, False = player respawned.</summary>
        public bool IsDead;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(SteamId);
            writer.Write(IsDead);
        }

        public override void Deserialize(BinaryReader reader)
        {
            SteamId = reader.ReadUInt64();
            IsDead = reader.ReadBoolean();
        }
    }
}
