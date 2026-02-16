using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Sent by host at ~60Hz with current NPC positions, Y rotation, animator bool bitmask, state hash, aim target, fire count, attack variant, and anim change counter.
    /// Unreliable — high-frequency updates, like PlayerStateMessage.
    /// 2 + 35*N bytes: Count(2) + [EntityId(2) + PosX(4) + PosY(4) + PosZ(4) + RotY(4) + BoolFlags(2) + AnimStateHash(4) + TargetSteamId(8) + FireCount(1) + AttackVariantId(1) + AnimChangeCount(1)] x N
    /// BoolFlags: generic bitmask of all Bool-type animator parameters (up to 16), packed by parameter array index order
    /// AnimStateHash: Animator.GetCurrentAnimatorStateInfo(0).fullPathHash — drives ALL state transitions on client via CrossFade
    /// TargetSteamId: SteamID of the player this NPC is aiming at (0 = none) — used to correct client-side aim direction
    /// FireCount: Monotonic byte counter of shots fired — client detects delta and calls Weapon.Shoot() directly
    /// AttackVariantId: animator.GetInteger("AttackID") — selects melee attack variant within attack states
    /// </summary>
    public class NpcPositionBatchMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.EnemyState;
        public override bool Reliable => false;

        public struct NpcPosEntry
        {
            public ushort EntityId;
            public float PosX, PosY, PosZ;
            public float RotY;
            public ushort BoolFlags;    // Generic bitmask of all Bool-type animator parameters
            public int AnimStateHash;   // Animator state hash (layer 0) — drives ALL transitions via CrossFade
            public ulong TargetSteamId; // SteamID of NPC's aim target (0 = none)
            public byte FireCount;      // Monotonic shot counter — client detects delta to fire
            public byte AttackVariantId; // animator.GetInteger("AttackID") — melee variant selection
            public byte AnimChangeCount; // Monotonic counter — increments on host animator state change
        }

        public NpcPosEntry[] Entries;

        public override void Serialize(BinaryWriter writer)
        {
            ushort count = (ushort)(Entries?.Length ?? 0);
            writer.Write(count);
            for (int i = 0; i < count; i++)
            {
                ref var e = ref Entries[i];
                writer.Write(e.EntityId);
                writer.Write(e.PosX);
                writer.Write(e.PosY);
                writer.Write(e.PosZ);
                writer.Write(e.RotY);
                writer.Write(e.BoolFlags);
                writer.Write(e.AnimStateHash);
                writer.Write(e.TargetSteamId);
                writer.Write(e.FireCount);
                writer.Write(e.AttackVariantId);
                writer.Write(e.AnimChangeCount);
            }
        }

        public override void Deserialize(BinaryReader reader)
        {
            ushort count = reader.ReadUInt16();
            Entries = new NpcPosEntry[count];
            for (int i = 0; i < count; i++)
            {
                Entries[i].EntityId = reader.ReadUInt16();
                Entries[i].PosX = reader.ReadSingle();
                Entries[i].PosY = reader.ReadSingle();
                Entries[i].PosZ = reader.ReadSingle();
                Entries[i].RotY = reader.ReadSingle();
                Entries[i].BoolFlags = reader.ReadUInt16();
                Entries[i].AnimStateHash = reader.ReadInt32();
                Entries[i].TargetSteamId = reader.ReadUInt64();
                Entries[i].FireCount = reader.ReadByte();
                Entries[i].AttackVariantId = reader.ReadByte();
                Entries[i].AnimChangeCount = reader.ReadByte();
            }
        }
    }
}
