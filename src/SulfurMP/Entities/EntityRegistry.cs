using System.Collections.Generic;
using UnityEngine;

namespace SulfurMP.Entities
{
    /// <summary>
    /// Bidirectional mapping between NetworkEntityId and GameObject.
    /// Host assigns IDs via AssignId(); both sides register/unregister.
    /// Pure C# class — not a MonoBehaviour.
    /// </summary>
    public class EntityRegistry
    {
        private readonly Dictionary<ushort, GameObject> _idToEntity = new Dictionary<ushort, GameObject>();
        private readonly Dictionary<GameObject, ushort> _entityToId = new Dictionary<GameObject, ushort>();
        private ushort _nextId = 1;

        public int Count => _idToEntity.Count;

        /// <summary>
        /// Host only: assign the next available NetworkEntityId to a GameObject.
        /// </summary>
        public NetworkEntityId AssignId(GameObject entity)
        {
            if (entity == null) return NetworkEntityId.None;

            // Already registered?
            if (_entityToId.ContainsKey(entity))
                return new NetworkEntityId(_entityToId[entity]);

            ushort id = _nextId++;
            // Wrap around (skip 0)
            if (_nextId == 0) _nextId = 1;

            _idToEntity[id] = entity;
            _entityToId[entity] = id;
            return new NetworkEntityId(id);
        }

        /// <summary>
        /// Register a known ID-entity pair. Used by clients receiving entity data from host.
        /// </summary>
        public void Register(NetworkEntityId id, GameObject entity)
        {
            if (!id.IsValid || entity == null) return;

            _idToEntity[id.Value] = entity;
            _entityToId[entity] = id.Value;
        }

        /// <summary>
        /// Remove an entity from the registry by its ID.
        /// </summary>
        public void Unregister(NetworkEntityId id)
        {
            if (!id.IsValid) return;

            if (_idToEntity.TryGetValue(id.Value, out var entity))
            {
                _idToEntity.Remove(id.Value);
                if (entity != null)
                    _entityToId.Remove(entity);
            }
        }

        /// <summary>
        /// Remove an entity from the registry by its GameObject.
        /// </summary>
        public void Unregister(GameObject entity)
        {
            if (entity == null) return;

            if (_entityToId.TryGetValue(entity, out var id))
            {
                _entityToId.Remove(entity);
                _idToEntity.Remove(id);
            }
        }

        public bool TryGetEntity(NetworkEntityId id, out GameObject entity)
        {
            if (id.IsValid && _idToEntity.TryGetValue(id.Value, out entity))
            {
                // Check for destroyed GameObjects (Unity null)
                if (entity == null)
                {
                    _idToEntity.Remove(id.Value);
                    // Also clean up the reverse mapping
                    // (can't remove by key since the GO is destroyed, but the entry is stale)
                    entity = null;
                    return false;
                }
                return true;
            }
            entity = null;
            return false;
        }

        public bool TryGetId(GameObject entity, out NetworkEntityId id)
        {
            if (entity != null && _entityToId.TryGetValue(entity, out var value))
            {
                id = new NetworkEntityId(value);
                return true;
            }
            id = NetworkEntityId.None;
            return false;
        }

        /// <summary>
        /// Clear all entries. Call on level transitions to reset state.
        /// Resets the ID counter back to 1.
        /// </summary>
        public void Clear()
        {
            _idToEntity.Clear();
            _entityToId.Clear();
            _nextId = 1;
        }
    }
}
