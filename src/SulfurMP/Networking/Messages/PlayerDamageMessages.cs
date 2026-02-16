using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Sent by host to a specific client when an NPC damages their remote player.
    /// The target client applies damage to their local player.
    /// 27 bytes: TargetSteamId(8) + Damage(4) + DamageTypeId(1) + SourceEntityId(2) + Position(12)
    /// </summary>
    public class PlayerDamageMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.EnemyAttack;

        public ulong TargetSteamId;
        public float Damage;
        public byte DamageTypeId;
        public ushort SourceEntityId;
        public float PosX, PosY, PosZ;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(TargetSteamId);
            writer.Write(Damage);
            writer.Write(DamageTypeId);
            writer.Write(SourceEntityId);
            writer.Write(PosX);
            writer.Write(PosY);
            writer.Write(PosZ);
        }

        public override void Deserialize(BinaryReader reader)
        {
            TargetSteamId = reader.ReadUInt64();
            Damage = reader.ReadSingle();
            DamageTypeId = reader.ReadByte();
            SourceEntityId = reader.ReadUInt16();
            PosX = reader.ReadSingle();
            PosY = reader.ReadSingle();
            PosZ = reader.ReadSingle();
        }
    }
}
