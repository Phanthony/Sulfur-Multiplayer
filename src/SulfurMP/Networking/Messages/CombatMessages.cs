using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Sent by client to host requesting damage on an NPC.
    /// Client suppresses local damage and defers to host authority.
    /// 19 bytes: EntityId(2) + Damage(4) + DamageTypeId(1) + CollisionPoint(12)
    /// </summary>
    public class DamageRequestMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.DamageEvent;

        public ushort EntityId;
        public float Damage;
        public byte DamageTypeId;
        public float PosX, PosY, PosZ;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(EntityId);
            writer.Write(Damage);
            writer.Write(DamageTypeId);
            writer.Write(PosX);
            writer.Write(PosY);
            writer.Write(PosZ);
        }

        public override void Deserialize(BinaryReader reader)
        {
            EntityId = reader.ReadUInt16();
            Damage = reader.ReadSingle();
            DamageTypeId = reader.ReadByte();
            PosX = reader.ReadSingle();
            PosY = reader.ReadSingle();
            PosZ = reader.ReadSingle();
        }
    }

    /// <summary>
    /// Sent by host to all clients with authoritative damage result.
    /// Clients set NPC health directly from this message.
    /// 37 bytes: EntityId(2) + NewHealth(4) + FinalDamage(4) + DamageTypeId(1) + CollisionPoint(12)
    ///         + HitStateByte(1) + CaliberByte(1) + Direction(12)
    /// </summary>
    public class DamageResultMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.HitConfirm;

        public ushort EntityId;
        public float NewHealth;
        public float FinalDamage;
        public byte DamageTypeId;
        public float PosX, PosY, PosZ;

        // Hit effect sync (Phase 12)
        public byte HitStateByte;           // 0 = no flash, 1=Hit, 2=Crit, 3=ProtectedNpc
        public byte CaliberByte;            // 0xFF = no bullet hole
        public float DirX, DirY, DirZ;     // Bullet hole direction

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(EntityId);
            writer.Write(NewHealth);
            writer.Write(FinalDamage);
            writer.Write(DamageTypeId);
            writer.Write(PosX);
            writer.Write(PosY);
            writer.Write(PosZ);
            writer.Write(HitStateByte);
            writer.Write(CaliberByte);
            writer.Write(DirX);
            writer.Write(DirY);
            writer.Write(DirZ);
        }

        public override void Deserialize(BinaryReader reader)
        {
            EntityId = reader.ReadUInt16();
            NewHealth = reader.ReadSingle();
            FinalDamage = reader.ReadSingle();
            DamageTypeId = reader.ReadByte();
            PosX = reader.ReadSingle();
            PosY = reader.ReadSingle();
            PosZ = reader.ReadSingle();
            HitStateByte = reader.ReadByte();
            CaliberByte = reader.ReadByte();
            DirX = reader.ReadSingle();
            DirY = reader.ReadSingle();
            DirZ = reader.ReadSingle();
        }
    }

    /// <summary>
    /// Sent by host when an NPC is killed in combat.
    /// Clients trigger Die() locally for death animation/effects/loot.
    /// 4 bytes: EntityId(2) + DamageTypeId(1) + KillerIsPlayer(1)
    /// </summary>
    public class EntityDeathMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.EntityDeath;

        public ushort EntityId;
        public byte DamageTypeId;
        public byte KillerIsPlayer;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(EntityId);
            writer.Write(DamageTypeId);
            writer.Write(KillerIsPlayer);
        }

        public override void Deserialize(BinaryReader reader)
        {
            EntityId = reader.ReadUInt16();
            DamageTypeId = reader.ReadByte();
            KillerIsPlayer = reader.ReadByte();
        }
    }

    /// <summary>
    /// Sent by host when an NPC is hit but takes no damage (invulnerable/blocked).
    /// Clients replay visual effects (hit flash, invulnerable spark).
    /// 16 bytes: EntityId(2) + HitStateByte(1) + Position(12) + InvulnerableEffect(1)
    /// </summary>
    public class HitBlockedMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.HitBlocked;

        public ushort EntityId;
        public byte HitStateByte;       // 0=none, 1=Hit, 2=Crit, 3=ProtectedNpc
        public float PosX, PosY, PosZ;
        public byte InvulnerableEffect; // 1 = show spark

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(EntityId);
            writer.Write(HitStateByte);
            writer.Write(PosX);
            writer.Write(PosY);
            writer.Write(PosZ);
            writer.Write(InvulnerableEffect);
        }

        public override void Deserialize(BinaryReader reader)
        {
            EntityId = reader.ReadUInt16();
            HitStateByte = reader.ReadByte();
            PosX = reader.ReadSingle();
            PosY = reader.ReadSingle();
            PosZ = reader.ReadSingle();
            InvulnerableEffect = reader.ReadByte();
        }
    }
}
