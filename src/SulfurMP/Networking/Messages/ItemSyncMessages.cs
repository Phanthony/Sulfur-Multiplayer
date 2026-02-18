using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Host→all: a new item pickup appeared in the world.
    /// Fields: pickupId(2) + itemId(2) + pos(12) + quantity(2) + hasInvData(1) + [invData]
    /// </summary>
    public class ItemSpawnMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.ItemSpawn;

        public ushort PickupId;
        public ushort ItemId;
        public float PosX, PosY, PosZ;
        public ushort Quantity;
        public bool HasInventoryData;
        // Simplified InventoryData fields (only sent when HasInventoryData=true)
        public int CurrentAmmo;
        public byte CaliberId;
        public ushort[] AttachmentIds;
        public ushort[] EnchantmentIds;
        public int BoughtFor;
        public byte[] AttrIds;
        public float[] AttrValues;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(PickupId);
            writer.Write(ItemId);
            writer.Write(PosX);
            writer.Write(PosY);
            writer.Write(PosZ);
            writer.Write(Quantity);
            writer.Write(HasInventoryData);
            if (HasInventoryData)
            {
                writer.Write(CurrentAmmo);
                writer.Write(CaliberId);
                byte attachCount = (byte)(AttachmentIds?.Length ?? 0);
                writer.Write(attachCount);
                for (int i = 0; i < attachCount; i++)
                    writer.Write(AttachmentIds[i]);
                byte enchantCount = (byte)(EnchantmentIds?.Length ?? 0);
                writer.Write(enchantCount);
                for (int i = 0; i < enchantCount; i++)
                    writer.Write(EnchantmentIds[i]);
                writer.Write(BoughtFor);
                byte attrCount = (byte)(AttrIds?.Length ?? 0);
                writer.Write(attrCount);
                for (int i = 0; i < attrCount; i++)
                {
                    writer.Write(AttrIds[i]);
                    writer.Write(AttrValues[i]);
                }
            }
        }

        public override void Deserialize(BinaryReader reader)
        {
            PickupId = reader.ReadUInt16();
            ItemId = reader.ReadUInt16();
            PosX = reader.ReadSingle();
            PosY = reader.ReadSingle();
            PosZ = reader.ReadSingle();
            Quantity = reader.ReadUInt16();
            HasInventoryData = reader.ReadBoolean();
            if (HasInventoryData)
            {
                CurrentAmmo = reader.ReadInt32();
                CaliberId = reader.ReadByte();
                byte attachCount = reader.ReadByte();
                AttachmentIds = new ushort[attachCount];
                for (int i = 0; i < attachCount; i++)
                    AttachmentIds[i] = reader.ReadUInt16();
                byte enchantCount = reader.ReadByte();
                EnchantmentIds = new ushort[enchantCount];
                for (int i = 0; i < enchantCount; i++)
                    EnchantmentIds[i] = reader.ReadUInt16();
                BoughtFor = reader.ReadInt32();
                byte attrCount = reader.ReadByte();
                AttrIds = new byte[attrCount];
                AttrValues = new float[attrCount];
                for (int i = 0; i < attrCount; i++)
                {
                    AttrIds[i] = reader.ReadByte();
                    AttrValues[i] = reader.ReadSingle();
                }
            }
        }
    }

    /// <summary>
    /// Host→all: an item was picked up (remove from world).
    /// Fields: pickupId(2) + pickerSteamId(8) + itemId(2)
    /// </summary>
    public class ItemPickedUpMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.ItemPickup;

        public ushort PickupId;
        public ulong PickerSteamId;
        public ushort ItemId;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(PickupId);
            writer.Write(PickerSteamId);
            writer.Write(ItemId);
        }

        public override void Deserialize(BinaryReader reader)
        {
            PickupId = reader.ReadUInt16();
            PickerSteamId = reader.ReadUInt64();
            ItemId = reader.ReadUInt16();
        }
    }

    /// <summary>
    /// Client→host: "I want to pick up this item."
    /// Fields: pickupId(2)
    /// </summary>
    public class ItemPickupRequestMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.ItemDespawn; // reuse slot 66

        public ushort PickupId;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(PickupId);
        }

        public override void Deserialize(BinaryReader reader)
        {
            PickupId = reader.ReadUInt16();
        }
    }

    /// <summary>
    /// Client→host: "I want to open this container."
    /// Fields: posX(4) + posY(4) + posZ(4)
    /// </summary>
    public class ContainerInteractMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.ContainerSync; // slot 67

        public float PosX, PosY, PosZ;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(PosX);
            writer.Write(PosY);
            writer.Write(PosZ);
        }

        public override void Deserialize(BinaryReader reader)
        {
            PosX = reader.ReadSingle();
            PosY = reader.ReadSingle();
            PosZ = reader.ReadSingle();
        }
    }

    /// <summary>
    /// Host→all: "This container is now looted."
    /// Fields: posX(4) + posY(4) + posZ(4)
    /// </summary>
    public class ContainerLootedMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.ContainerLooted;

        public float PosX, PosY, PosZ;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(PosX);
            writer.Write(PosY);
            writer.Write(PosZ);
        }

        public override void Deserialize(BinaryReader reader)
        {
            PosX = reader.ReadSingle();
            PosY = reader.ReadSingle();
            PosZ = reader.ReadSingle();
        }
    }

    /// <summary>
    /// Client→host: "I dropped this item." Carries simplified InventoryData.
    /// Fields: itemId(2) + pos(12) + quantity(2) + currentAmmo(4) + caliberId(1) +
    ///         attachCount(1) + attachIds(N*2) + enchantCount(1) + enchantIds(N*2)
    /// </summary>
    public class ItemDropMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.ItemDrop;

        public ushort ItemId;
        public float PosX, PosY, PosZ;
        public ushort Quantity;
        public int CurrentAmmo;
        public byte CaliberId;
        public ushort[] AttachmentIds;
        public ushort[] EnchantmentIds;
        public int BoughtFor;
        public byte[] AttrIds;
        public float[] AttrValues;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(ItemId);
            writer.Write(PosX);
            writer.Write(PosY);
            writer.Write(PosZ);
            writer.Write(Quantity);
            writer.Write(CurrentAmmo);
            writer.Write(CaliberId);
            byte attachCount = (byte)(AttachmentIds?.Length ?? 0);
            writer.Write(attachCount);
            for (int i = 0; i < attachCount; i++)
                writer.Write(AttachmentIds[i]);
            byte enchantCount = (byte)(EnchantmentIds?.Length ?? 0);
            writer.Write(enchantCount);
            for (int i = 0; i < enchantCount; i++)
                writer.Write(EnchantmentIds[i]);
            writer.Write(BoughtFor);
            byte attrCount = (byte)(AttrIds?.Length ?? 0);
            writer.Write(attrCount);
            for (int i = 0; i < attrCount; i++)
            {
                writer.Write(AttrIds[i]);
                writer.Write(AttrValues[i]);
            }
        }

        public override void Deserialize(BinaryReader reader)
        {
            ItemId = reader.ReadUInt16();
            PosX = reader.ReadSingle();
            PosY = reader.ReadSingle();
            PosZ = reader.ReadSingle();
            Quantity = reader.ReadUInt16();
            CurrentAmmo = reader.ReadInt32();
            CaliberId = reader.ReadByte();
            byte attachCount = reader.ReadByte();
            AttachmentIds = new ushort[attachCount];
            for (int i = 0; i < attachCount; i++)
                AttachmentIds[i] = reader.ReadUInt16();
            byte enchantCount = reader.ReadByte();
            EnchantmentIds = new ushort[enchantCount];
            for (int i = 0; i < enchantCount; i++)
                EnchantmentIds[i] = reader.ReadUInt16();
            BoughtFor = reader.ReadInt32();
            byte attrCount = reader.ReadByte();
            AttrIds = new byte[attrCount];
            AttrValues = new float[attrCount];
            for (int i = 0; i < attrCount; i++)
            {
                AttrIds[i] = reader.ReadByte();
                AttrValues[i] = reader.ReadSingle();
            }
        }
    }

    /// <summary>
    /// Client→host: request to loot the church collection box.
    /// No payload needed — there's only one collection box per church level.
    /// </summary>
    public class ChurchCollectionLootMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.ChurchCollectionLoot;
        public override void Serialize(BinaryWriter writer) { }
        public override void Deserialize(BinaryReader reader) { }
    }

    /// <summary>
    /// Host→all: "Everyone gets this coin's value." Used for shared gold.
    /// Fields: itemId(2)
    /// </summary>
    public class SharedGoldMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.SharedGold;

        public ushort ItemId;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(ItemId);
        }

        public override void Deserialize(BinaryReader reader)
        {
            ItemId = reader.ReadUInt16();
        }
    }
}
