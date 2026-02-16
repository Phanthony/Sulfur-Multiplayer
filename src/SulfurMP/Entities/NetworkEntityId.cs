using System;

namespace SulfurMP.Entities
{
    /// <summary>
    /// Lightweight host-assigned entity identifier. Wraps a ushort (1-65535).
    /// Value 0 means "no entity" / invalid.
    /// </summary>
    public readonly struct NetworkEntityId : IEquatable<NetworkEntityId>
    {
        public readonly ushort Value;

        public static readonly NetworkEntityId None = new NetworkEntityId(0);

        public bool IsValid => Value != 0;

        public NetworkEntityId(ushort value)
        {
            Value = value;
        }

        public bool Equals(NetworkEntityId other) => Value == other.Value;
        public override bool Equals(object obj) => obj is NetworkEntityId other && Equals(other);
        public override int GetHashCode() => Value.GetHashCode();
        public override string ToString() => $"Entity({Value})";

        public static bool operator ==(NetworkEntityId a, NetworkEntityId b) => a.Value == b.Value;
        public static bool operator !=(NetworkEntityId a, NetworkEntityId b) => a.Value != b.Value;
    }
}
