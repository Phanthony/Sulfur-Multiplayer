namespace SulfurMP.Networking
{
    /// <summary>
    /// All network message types. First byte of every message.
    /// Keep values stable — changing them breaks protocol compatibility.
    /// </summary>
    public enum MessageType : byte
    {
        // Connection / Handshake (0-15)
        Handshake = 0,
        HandshakeResponse = 1,
        Disconnect = 2,
        Heartbeat = 3,

        // Player State (16-31)
        PlayerState = 16,
        PlayerSpawn = 17,
        PlayerDespawn = 18,
        PlayerDeath = 19,
        PlayerRespawn = 20,

        // Level / Scene (32-47)
        LevelSeed = 32,
        SceneChange = 33,
        SceneReady = 34,
        LevelCompleteRequest = 35,

        // Combat (48-63)
        WeaponFire = 48,
        HitConfirm = 49,
        DamageEvent = 50,
        EntityDeath = 51,
        HitBlocked = 52,

        // Items (64-79)
        ItemSpawn = 64,
        ItemPickup = 65,
        ItemDespawn = 66,       // ItemPickupRequest (client→host)
        ContainerSync = 67,     // ContainerInteract (client→host)
        ContainerLooted = 68,   // ContainerLooted (host→all)
        ItemDrop = 69,          // ItemDrop (client→host)
        SharedGold = 70,        // SharedGold (host→all)

        // Entities (80-95)
        EntitySpawn = 80,
        EntityState = 81,
        EntityDespawn = 82,

        // Enemy AI (96-111)
        EnemyState = 96,
        EnemyAttack = 97,
        EnemyTarget = 98,

        // Interactables (112-127)
        InteractableState = 112,
        InteractableUse = 113,
        BreakableInventory = 114,
        ClientNpcSpawnNotify = 115,

        // Game Flow (128-143)
        GameFlowState = 128,
        PauseState = 129,

        // Debug / Admin (240-255)
        DebugMessage = 240,
        Ping = 254,
        Pong = 255,
    }
}
