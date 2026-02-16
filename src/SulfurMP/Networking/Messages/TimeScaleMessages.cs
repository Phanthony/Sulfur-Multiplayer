using System.IO;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// Syncs GameManager.SetTimeScale calls across all players.
    /// When one player triggers slow-mo (dodge, item, punishment),
    /// all players experience it together.
    /// </summary>
    public class TimeScaleMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.PauseState;
        public override bool Reliable => true;

        public float Scale;
        public float LerpDuration;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(Scale);
            writer.Write(LerpDuration);
        }

        public override void Deserialize(BinaryReader reader)
        {
            Scale = reader.ReadSingle();
            LerpDuration = reader.ReadSingle();
        }
    }
}
