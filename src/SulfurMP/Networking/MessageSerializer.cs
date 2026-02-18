using System;
using System.Collections.Generic;
using System.IO;
using SulfurMP.Networking.Messages;

namespace SulfurMP.Networking
{
    /// <summary>
    /// Serializes/deserializes network messages to/from byte arrays.
    /// Wire format: [MessageType:1byte][Payload:variable]
    /// </summary>
    public static class MessageSerializer
    {
        // Factory functions that create empty message instances for deserialization
        private static readonly Dictionary<MessageType, Func<NetworkMessage>> _factories =
            new Dictionary<MessageType, Func<NetworkMessage>>();

        static MessageSerializer()
        {
            RegisterBuiltinMessages();
        }

        /// <summary>
        /// Register a message type's factory. Called once at startup for each message type.
        /// </summary>
        public static void Register<T>(MessageType type) where T : NetworkMessage, new()
        {
            _factories[type] = () => new T();
        }

        /// <summary>
        /// Serialize a message to a byte array ready for transmission.
        /// </summary>
        public static byte[] Serialize(NetworkMessage message)
        {
            using (var ms = new MemoryStream(64))
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write((byte)message.Type);
                message.Serialize(writer);
                return ms.ToArray();
            }
        }

        /// <summary>
        /// Deserialize a byte array into a typed NetworkMessage.
        /// Returns null if the message type is unknown or deserialization fails.
        /// </summary>
        public static NetworkMessage Deserialize(byte[] data, int length)
        {
            if (data == null || length < 1)
                return null;

            var type = (MessageType)data[0];

            if (!_factories.TryGetValue(type, out var factory))
            {
                Plugin.Log.LogWarning($"Unknown message type: {type} ({data[0]})");
                return null;
            }

            var message = factory();

            try
            {
                using (var ms = new MemoryStream(data, 1, length - 1))
                using (var reader = new BinaryReader(ms))
                {
                    message.Deserialize(reader);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"Failed to deserialize {type}: {ex}");
                return null;
            }

            return message;
        }

        private static void RegisterBuiltinMessages()
        {
            Register<HandshakeMessage>(MessageType.Handshake);
            Register<HandshakeResponseMessage>(MessageType.HandshakeResponse);
            Register<DisconnectMessage>(MessageType.Disconnect);
            Register<HeartbeatMessage>(MessageType.Heartbeat);
            Register<PlayerStateMessage>(MessageType.PlayerState);
            Register<PlayerSpawnMessage>(MessageType.PlayerSpawn);
            Register<PlayerDespawnMessage>(MessageType.PlayerDespawn);
            Register<PingMessage>(MessageType.Ping);
            Register<PongMessage>(MessageType.Pong);

            // Phase 10
            Register<PlayerDeathMessage>(MessageType.PlayerDeath);
            Register<TimeScaleMessage>(MessageType.PauseState);

            // Level sync (Phase 4)
            Register<LevelSeedMessage>(MessageType.LevelSeed);
            Register<SceneReadyMessage>(MessageType.SceneReady);
            Register<LevelTransitionRequestMessage>(MessageType.SceneChange);
            Register<CompleteLevelRequestMessage>(MessageType.LevelCompleteRequest);

            // Entity sync (Phase 7)
            Register<EntitySpawnMessage>(MessageType.EntitySpawn);
            Register<EntityBatchSpawnMessage>(MessageType.EntityState);
            Register<EntityDespawnMessage>(MessageType.EntityDespawn);

            // Combat sync (Phase 6)
            Register<DamageRequestMessage>(MessageType.DamageEvent);
            Register<DamageResultMessage>(MessageType.HitConfirm);
            Register<EntityDeathMessage>(MessageType.EntityDeath);
            Register<HitBlockedMessage>(MessageType.HitBlocked);

            // NPC position sync
            Register<NpcPositionBatchMessage>(MessageType.EnemyState);

            // NPC→Player damage (Phase 9)
            Register<PlayerDamageMessage>(MessageType.EnemyAttack);

            // Item sync (Phase 8)
            Register<ItemSpawnMessage>(MessageType.ItemSpawn);
            Register<ItemPickedUpMessage>(MessageType.ItemPickup);
            Register<ItemPickupRequestMessage>(MessageType.ItemDespawn);
            Register<ContainerInteractMessage>(MessageType.ContainerSync);
            Register<ContainerLootedMessage>(MessageType.ContainerLooted);
            Register<ItemDropMessage>(MessageType.ItemDrop);
            Register<SharedGoldMessage>(MessageType.SharedGold);
            Register<ChurchCollectionLootMessage>(MessageType.ChurchCollectionLoot);

            // World state sync (Phase 11)
            Register<WorldObjectStateMessage>(MessageType.InteractableState);
            Register<BreakableInventoryMessage>(MessageType.BreakableInventory);

            // Weapon fire sync (Phase 12a)
            Register<WeaponFireMessage>(MessageType.WeaponFire);

            // Client NPC spawn notification (Bug 2 fix)
            Register<ClientNpcSpawnNotifyMessage>(MessageType.ClientNpcSpawnNotify);
        }
    }
}
