using System.IO;
using UnityEngine;

namespace SulfurMP.Networking.Messages
{
    /// <summary>
    /// High-frequency player state update. Sent unreliably at ~20Hz.
    /// Contains position, rotation, velocity for interpolation.
    /// </summary>
    public class PlayerStateMessage : NetworkMessage
    {
        public override MessageType Type => MessageType.PlayerState;
        public override bool Reliable => false; // Unreliable for high-frequency updates

        public ulong SteamId;
        public float Timestamp;

        // Position
        public float PosX, PosY, PosZ;

        // Rotation (yaw + pitch is sufficient for FPS)
        public float Yaw, Pitch;

        // Velocity (for extrapolation)
        public float VelX, VelY, VelZ;

        // Animation state (bit flags: 0=sprinting, 1=crouching, 2=jumping, 3=falling)
        public byte AnimationState;
        public bool IsGrounded;

        // Health (0-255 mapped from 0.0-1.0 normalized)
        public byte Health;

        public override void Serialize(BinaryWriter writer)
        {
            writer.Write(SteamId);
            writer.Write(Timestamp);
            writer.Write(PosX);
            writer.Write(PosY);
            writer.Write(PosZ);
            writer.Write(Yaw);
            writer.Write(Pitch);
            writer.Write(VelX);
            writer.Write(VelY);
            writer.Write(VelZ);
            writer.Write(AnimationState);
            writer.Write(IsGrounded);
            writer.Write(Health);
        }

        public override void Deserialize(BinaryReader reader)
        {
            SteamId = reader.ReadUInt64();
            Timestamp = reader.ReadSingle();
            PosX = reader.ReadSingle();
            PosY = reader.ReadSingle();
            PosZ = reader.ReadSingle();
            Yaw = reader.ReadSingle();
            Pitch = reader.ReadSingle();
            VelX = reader.ReadSingle();
            VelY = reader.ReadSingle();
            VelZ = reader.ReadSingle();
            AnimationState = reader.ReadByte();
            IsGrounded = reader.ReadBoolean();
            Health = reader.ReadByte();
        }

        public void SetPosition(Vector3 pos)
        {
            PosX = pos.x;
            PosY = pos.y;
            PosZ = pos.z;
        }

        public Vector3 GetPosition() => new Vector3(PosX, PosY, PosZ);

        public void SetVelocity(Vector3 vel)
        {
            VelX = vel.x;
            VelY = vel.y;
            VelZ = vel.z;
        }

        public Vector3 GetVelocity() => new Vector3(VelX, VelY, VelZ);
    }
}
