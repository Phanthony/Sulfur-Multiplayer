using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Sent by client to host when an NPC spawns on the client but isn't registered.
    /// The host force-spawns or finds the NPC on its side, registers it, and sends
    /// EntitySpawnMessage back so CombatSync works normally.
    /// 14 bytes: UnitSOId(2) + Pos(12)
    /// </summary>
    public class ClientNpcSpawnNotifyMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.ClientNpcSpawnNotify;

        public ushort UnitSOId;
        public float PosX, PosY, PosZ;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(UnitSOId);
            writer.Write(PosX);
            writer.Write(PosY);
            writer.Write(PosZ);
        }

        public override void Deserialize(BinaryReader reader)
        {
            UnitSOId = reader.ReadUInt16();
            PosX = reader.ReadSingle();
            PosY = reader.ReadSingle();
            PosZ = reader.ReadSingle();
        }
    }
}
