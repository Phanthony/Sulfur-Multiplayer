using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Sent when a player fires a ranged weapon. Unreliable — missed messages
    /// just mean a missed tracer/sound, which is acceptable for visual-only FX.
    /// 34 bytes: SteamId(8) + Position(12) + Direction(12) + WeaponItemId(2)
    /// </summary>
    public class WeaponFireMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.WeaponFire;
        public override bool Reliable => false;

        public ulong SteamId;
        public float PosX, PosY, PosZ;
        public float DirX, DirY, DirZ;
        public ushort WeaponItemId;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(SteamId);
            writer.Write(PosX);
            writer.Write(PosY);
            writer.Write(PosZ);
            writer.Write(DirX);
            writer.Write(DirY);
            writer.Write(DirZ);
            writer.Write(WeaponItemId);
        }

        public override void Deserialize(BinaryReader reader)
        {
            SteamId = reader.ReadUInt64();
            PosX = reader.ReadSingle();
            PosY = reader.ReadSingle();
            PosZ = reader.ReadSingle();
            DirX = reader.ReadSingle();
            DirY = reader.ReadSingle();
            DirZ = reader.ReadSingle();
            WeaponItemId = reader.ReadUInt16();
        }
    }
}
