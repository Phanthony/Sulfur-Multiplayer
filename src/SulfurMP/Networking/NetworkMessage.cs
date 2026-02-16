using System.IO;

namespace SulfurMP.Networking
{
    /// <summary>
    /// Base class for all network messages. Subclasses implement Serialize/Deserialize
    /// using BinaryWriter/BinaryReader for fast binary serialization.
    /// </summary>
    public abstract class NetworkMessage
    {
        /// <summary>
        /// The message type identifier. Each subclass returns its fixed type.
        /// </summary>
        public abstract MessageType Type { get; }

        /// <summary>
        /// Whether this message should be sent reliably (TCP-like) or unreliably (UDP-like).
        /// Override to return false for high-frequency state updates (e.g. player position).
        /// </summary>
        public virtual bool Reliable => true;

        /// <summary>
        /// Write message payload to the writer. Do NOT write the message type header —
        /// MessageSerializer handles that.
        /// </summary>
        public abstract void Serialize(BinaryWriter writer);

        /// <summary>
        /// Read message payload from the reader. Do NOT read the message type header —
        /// MessageSerializer handles that.
        /// </summary>
        public abstract void Deserialize(BinaryReader reader);
    }
}
