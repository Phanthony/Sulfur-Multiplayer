using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Sent by host to all clients when a level transition occurs.
    /// Contains all info needed for clients to generate the identical level.
    /// </summary>
    public class LevelSeedMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.LevelSeed;

        public byte EnvironmentId;   // WorldEnvironmentIds cast to byte
        public int LevelIndex;
        public long Seed;
        public string SpawnIdentifier;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(EnvironmentId);
            writer.Write(LevelIndex);
            writer.Write(Seed);
            writer.Write(SpawnIdentifier ?? "");
        }

        public override void Deserialize(BinaryReader reader)
        {
            EnvironmentId = reader.ReadByte();
            LevelIndex = reader.ReadInt32();
            Seed = reader.ReadInt64();
            SpawnIdentifier = reader.ReadString();
        }
    }

    /// <summary>
    /// Sent by client to host when the client triggers a level transition (portal/stairs).
    /// Host receives this and initiates the transition for everyone.
    /// </summary>
    public class LevelTransitionRequestMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.SceneChange;
        public override bool Reliable => true;

        public byte EnvironmentId;
        public int LevelIndex;
        public string SpawnIdentifier;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(EnvironmentId);
            writer.Write(LevelIndex);
            writer.Write(SpawnIdentifier ?? "");
        }

        public override void Deserialize(BinaryReader reader)
        {
            EnvironmentId = reader.ReadByte();
            LevelIndex = reader.ReadInt32();
            SpawnIdentifier = reader.ReadString();
        }
    }

    /// <summary>
    /// Sent by client to host when the client walks through a "complete level" portal
    /// (e.g., Caves 1 → Caves 2). No payload — host calls its own CompleteLevel.
    /// </summary>
    public class CompleteLevelRequestMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.LevelCompleteRequest;
        public override bool Reliable => true;

        public override void Serialize(BinaryWriter writer) { }
        public override void Deserialize(BinaryReader reader) { }
    }

    /// <summary>
    /// Sent by client to host after finishing level generation.
    /// Lets the host know the client is ready for gameplay.
    /// </summary>
    public class SceneReadyMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.SceneReady;

        public ulong SteamId;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(SteamId);
        }

        public override void Deserialize(BinaryReader reader)
        {
            SteamId = reader.ReadUInt64();
        }
    }
}
