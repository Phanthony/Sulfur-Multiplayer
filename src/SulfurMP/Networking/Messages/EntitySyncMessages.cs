using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Sent by host when a single NPC spawns dynamically (mid-level, not during initial batch).
    /// 21 bytes: EntityId(2) + UnitIdValue(2) + Pos(12) + Health(4) + State(1)
    /// </summary>
    public class EntitySpawnMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.EntitySpawn;

        public ushort EntityId;
        public ushort UnitIdValue;
        public float PosX, PosY, PosZ;
        public float Health;
        public byte State;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(EntityId);
            writer.Write(UnitIdValue);
            writer.Write(PosX);
            writer.Write(PosY);
            writer.Write(PosZ);
            writer.Write(Health);
            writer.Write(State);
        }

        public override void Deserialize(BinaryReader reader)
        {
            EntityId = reader.ReadUInt16();
            UnitIdValue = reader.ReadUInt16();
            PosX = reader.ReadSingle();
            PosY = reader.ReadSingle();
            PosZ = reader.ReadSingle();
            Health = reader.ReadSingle();
            State = reader.ReadByte();
        }
    }

    /// <summary>
    /// Sent by host after level load (or to late joiners) with all tracked NPC entities.
    /// 2 + 21*N bytes: Count(2) + [EntityId(2) + UnitIdValue(2) + Pos(12) + Health(4) + State(1)] x N
    /// </summary>
    public class EntityBatchSpawnMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.EntityState;

        public struct EntityEntry
        {
            public ushort EntityId;
            public ushort UnitIdValue;
            public float PosX, PosY, PosZ;
            public float Health;
            public byte State;
        }

        public EntityEntry[] Entries;

        public override void Serialize(BinaryWriter writer)
        {
            ushort count = (ushort)(Entries?.Length ?? 0);
            writer.Write(count);
            for (int i = 0; i < count; i++)
            {
                ref var e = ref Entries[i];
                writer.Write(e.EntityId);
                writer.Write(e.UnitIdValue);
                writer.Write(e.PosX);
                writer.Write(e.PosY);
                writer.Write(e.PosZ);
                writer.Write(e.Health);
                writer.Write(e.State);
            }
        }

        public override void Deserialize(BinaryReader reader)
        {
            ushort count = reader.ReadUInt16();
            Entries = new EntityEntry[count];
            for (int i = 0; i < count; i++)
            {
                Entries[i].EntityId = reader.ReadUInt16();
                Entries[i].UnitIdValue = reader.ReadUInt16();
                Entries[i].PosX = reader.ReadSingle();
                Entries[i].PosY = reader.ReadSingle();
                Entries[i].PosZ = reader.ReadSingle();
                Entries[i].Health = reader.ReadSingle();
                Entries[i].State = reader.ReadByte();
            }
        }
    }

    /// <summary>
    /// Sent by host when a tracked NPC dies or is removed.
    /// 3 bytes: EntityId(2) + Reason(1)
    /// </summary>
    public class EntityDespawnMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.EntityDespawn;

        public ushort EntityId;
        public byte Reason; // 0=Death, 1=Destroyed, 2=LevelTransition

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(EntityId);
            writer.Write(Reason);
        }

        public override void Deserialize(BinaryReader reader)
        {
            EntityId = reader.ReadUInt16();
            Reason = reader.ReadByte();
        }
    }
}
