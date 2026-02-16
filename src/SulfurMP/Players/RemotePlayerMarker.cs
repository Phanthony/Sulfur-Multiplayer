using System.Collections.Generic;
using UnityEngine;

namespace SulfurMP.Players
{
    /// <summary>
    /// Marker component on host-side remote player capsules.
    /// Used by CombatSyncManager to identify remote players in TakeHit interception,
    /// and by EntitySyncManager.ResolveNpcTargetSteamId for NPC target resolution.
    /// </summary>
    public class RemotePlayerMarker : MonoBehaviour
    {
        public ulong SteamId { get; set; }

        /// <summary>
        /// The Unit component added to this remote player capsule (host only).
        /// Stored as object because Unit type is resolved via reflection.
        /// </summary>
        public object UnitComponent { get; set; }

        /// <summary>
        /// All remote player Unit objects currently registered (host only).
        /// Used by EntitySyncManager.ResolveNpcTargetSteamId to identify when
        /// an NPC is targeting a remote player through native AI (hostilesInLOS).
        /// </summary>
        public static readonly HashSet<object> RegisteredUnits = new HashSet<object>();
    }
}
