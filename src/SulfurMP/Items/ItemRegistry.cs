using System.Collections.Generic;
using UnityEngine;

namespace SulfurMP.Items
{
    /// <summary>
    /// Bidirectional mapping between network pickup IDs (ushort) and Pickup GameObjects.
    /// Host assigns IDs; both sides register/unregister.
    /// Pure C# class — not a MonoBehaviour.
    /// </summary>
    public class ItemRegistry
    {
        private readonly Dictionary<ushort, GameObject> _idToPickup = new Dictionary<ushort, GameObject>();
        private readonly Dictionary<GameObject, ushort> _pickupToId = new Dictionary<GameObject, ushort>();
        private ushort _nextId = 1;

        public int Count => _idToPickup.Count;

        /// <summary>
        /// Host only: assign the next available network pickup ID to a Pickup GameObject.
        /// </summary>
        public ushort AssignId(GameObject pickup)
        {
            if (pickup == null) return 0;

            if (_pickupToId.TryGetValue(pickup, out var existing))
                return existing;

            ushort id = _nextId++;
            if (_nextId == 0) _nextId = 1; // wrap, skip 0

            _idToPickup[id] = pickup;
            _pickupToId[pickup] = id;
            return id;
        }

        /// <summary>
        /// Register a known ID-pickup pair. Used by clients receiving item data from host.
        /// </summary>
        public void Register(ushort id, GameObject pickup)
        {
            if (id == 0 || pickup == null) return;

            _idToPickup[id] = pickup;
            _pickupToId[pickup] = id;
        }

        /// <summary>
        /// Remove a pickup from the registry by its network ID.
        /// </summary>
        public void Remove(ushort id)
        {
            if (id == 0) return;

            if (_idToPickup.TryGetValue(id, out var pickup))
            {
                _idToPickup.Remove(id);
                if (pickup != null)
                    _pickupToId.Remove(pickup);
            }
        }

        /// <summary>
        /// Remove a pickup from the registry by its GameObject.
        /// </summary>
        public void Remove(GameObject pickup)
        {
            if (pickup == null) return;

            if (_pickupToId.TryGetValue(pickup, out var id))
            {
                _pickupToId.Remove(pickup);
                _idToPickup.Remove(id);
            }
        }

        public bool TryGetPickup(ushort id, out GameObject pickup)
        {
            if (id != 0 && _idToPickup.TryGetValue(id, out pickup))
            {
                if (pickup == null)
                {
                    _idToPickup.Remove(id);
                    pickup = null;
                    return false;
                }
                return true;
            }
            pickup = null;
            return false;
        }

        public bool TryGetId(GameObject pickup, out ushort id)
        {
            if (pickup != null && _pickupToId.TryGetValue(pickup, out id))
                return true;
            id = 0;
            return false;
        }

        /// <summary>
        /// Clear all entries. Call on level transitions.
        /// </summary>
        public void Clear()
        {
            _idToPickup.Clear();
            _pickupToId.Clear();
            _nextId = 1;
        }
    }
}
