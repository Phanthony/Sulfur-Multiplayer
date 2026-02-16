using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Bidirectional world object state sync for doors, gates, and breakables.
    /// Sent on interaction (rare events). Both sides run native logic, then broadcast.
    /// 14 bytes: ObjectType(1) + PosX(4) + PosY(4) + PosZ(4) + IsOpen(1)
    /// </summary>
    public class WorldObjectStateMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.InteractableState;

        public byte ObjectType; // 0=Door, 1=Gate, 2=Breakable
        public float PosX, PosY, PosZ;
        public bool IsOpen;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(ObjectType);
            writer.Write(PosX);
            writer.Write(PosY);
            writer.Write(PosZ);
            writer.Write(IsOpen);
        }

        public override void Deserialize(BinaryReader reader)
        {
            ObjectType = reader.ReadByte();
            PosX = reader.ReadSingle();
            PosY = reader.ReadSingle();
            PosZ = reader.ReadSingle();
            IsOpen = reader.ReadBoolean();
        }
    }
}
