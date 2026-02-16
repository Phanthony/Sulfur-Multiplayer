using System.Collections.Generic;
using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Host sends its complete breakable inventory after level gen.
    /// Clients compare and destroy any extras not in the host's list.
    /// Wire format: ushort count + per entry 3×float (posX, posY, posZ)
    /// </summary>
    public class BreakableInventoryMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.BreakableInventory;
        public override bool Reliable => true;

        public struct BreakableEntry
        {
            public float X, Y, Z;
        }

        public List<BreakableEntry> Entries = new List<BreakableEntry>();

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write((ushort)Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                writer.Write(Entries[i].X);
                writer.Write(Entries[i].Y);
                writer.Write(Entries[i].Z);
            }
        }

        public override void Deserialize(BinaryReader reader)
        {
            int count = reader.ReadUInt16();
            Entries = new List<BreakableEntry>(count);
            for (int i = 0; i < count; i++)
            {
                Entries.Add(new BreakableEntry
                {
                    X = reader.ReadSingle(),
                    Y = reader.ReadSingle(),
                    Z = reader.ReadSingle()
                });
            }
        }
    }
}
