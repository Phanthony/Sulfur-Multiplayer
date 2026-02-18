using System;
using System.Collections.Generic;
using System.Reflection;
using MonoMod.RuntimeDetour;
using SulfurMP.Networking;
using SulfurMP.Networking.Messages;
using Steamworks;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SulfurMP.Items
{
    /// <summary>
    /// Host-authoritative item synchronization.
    /// Hooks InteractionManager.SpawnPickup, ExecutePickup, and Container.OnInteract.
    ///
    /// - Host spawns items normally → broadcasts ItemSpawnMessage to clients
    /// - Client item spawns suppressed → created only from network messages
    /// - Client pickup requests sent to host → host validates + broadcasts result
    /// - Container opens: host-only, broadcasts loot spawn + ContainerLootedMessage
    /// - Coins: shared gold — all players get the coins value
    /// - Player drops: client sends ItemDropMessage → host spawns → broadcasts to all
    /// </summary>
    public class ItemSyncManager : MonoBehaviour
    {
        public static ItemSyncManager Instance { get; private set; }

        public ItemRegistry Registry { get; private set; } = new ItemRegistry();

        #region Fields

        // Hooks
        private Hook _spawnPickupHook;
        private Hook _executePickupHook;
        private Hook _containerOnInteractHook;
        private Hook _churchCollectionLootHook;
        private bool _hookAttempted;
        private int _hookRetryCount;
        private const int MaxHookRetries = 60;

        // Network spawn bypass flag — set before calling SpawnPickup for network-created items
        private static bool _isNetworkSpawn;

        // Host handling container on behalf of client — skip host inventory interaction
        private static bool _isRemoteContainerInteract;

        // Pending item spawns (client-side, messages received during scene loading)
        private readonly List<ItemSpawnMessage> _pendingSpawns = new List<ItemSpawnMessage>();

        // Pending pickup requests (client-side, to avoid spamming host)
        private readonly HashSet<ushort> _pendingPickupRequests = new HashSet<ushort>();

        // Item definition resolution cache
        private readonly Dictionary<ushort, object> _itemDefCache = new Dictionary<ushort, object>();

        #endregion

        #region Reflection Cache

        private static bool _reflectionInit;

        // Types
        private static Type _interactionManagerType;
        private static Type _pickupType;
        private static Type _itemDefinitionType;
        private static Type _itemIdType;
        private static Type _inventoryDataType;
        private static Type _containerType;
        private static Type _currencySOType;
        private static Type _itemGridType;
        private static Type _unitType;
        private static Type _caliberType;
        private static Type _gameManagerType;
        private static Type _itemDatabaseType;
        private static Type _asyncAssetLoadingType;
        private static Type _itemAttrCollDataType;   // ItemAttributeCollectionData
        private static Type _itemAttrDataType;       // ItemAttributeData
        private static Type _itemAttributesEnumType; // ItemAttributes enum (for Enum.ToObject)
        private static Type _churchCollectionLootableType;
        private static Type _lootableObjectType;

        // Singleton instance props (FlattenHierarchy for StaticInstance<T>)
        private static PropertyInfo _imInstanceProp;
        private static PropertyInfo _gmInstanceProp;
        private static PropertyInfo _uiInstanceProp;
        private static PropertyInfo _aalInstanceProp;

        // Methods — hook targets
        private static MethodInfo _spawnPickupMethod;
        private static MethodInfo _executePickupMethod;
        private static MethodInfo _containerOnInteractMethod;
        private static MethodInfo _churchLootMethod;       // ChurchCollectionLootable.Loot() (protected override)

        // Methods — church collection
        private static MethodInfo _lootableTriggerMethod;  // LootableObject.Trigger() (public)
        private static FieldInfo _lootHasBeenSpawnedField; // LootableObject.lootHasBeenSpawned (private)

        // Methods — called via reflection
        private static MethodInfo _removePickupMethod;
        private static MethodInfo _consumeItemMethod;

        // Fields & Properties — item resolution
        private static PropertyInfo _aalItemDatabaseProp;  // AsyncAssetLoading.itemDatabase
        private static FieldInfo _itemDefIdField;          // ItemDefinition.id (field, ItemId)
        private static FieldInfo _itemIdValueField;        // ItemId.value (field, ushort)
        private static FieldInfo _itemDefInvSizeField;     // ItemDefinition.inventorySize (Vector2Int)

        // Fields & Properties — Pickup
        private static PropertyInfo _pickupItemSOProp;     // Pickup.ItemSO (ItemDefinition)
        private static PropertyInfo _pickupInvDataProp;    // Pickup.inventoryData (InventoryData)

        // Fields & Properties — Container
        private static FieldInfo _containerLootedField;    // Container.looted (private bool)
        private static FieldInfo _interactableAnimatorField; // Interactable.animator

        // Fields & Properties — InventoryData
        private static FieldInfo _invDataIdField;
        private static FieldInfo _invDataQuantityField;
        private static FieldInfo _invDataCurrentAmmoField;
        private static FieldInfo _invDataCaliberField;
        private static FieldInfo _invDataAttachmentsField;
        private static FieldInfo _invDataEnchantmentsField;
        private static FieldInfo _invDataAttributesField;    // InventoryData.attributes (ItemAttributeCollectionData)
        private static FieldInfo _invDataBoughtForField;     // InventoryData.boughtFor (int)

        // Fields — ItemAttributeData + CharacterStat
        private static FieldInfo _itemAttrCollItemAttrsField; // ItemAttributeCollectionData.itemAttributes (ItemAttributeData[])
        private static FieldInfo _itemAttrDataIdField;        // ItemAttributeData.id (ItemAttributes enum)
        private static FieldInfo _itemAttrDataValueField;     // ItemAttributeData.value (CharacterStat)
        private static FieldInfo _charStatBaseValueField;     // CharacterStat.BaseValue (float)

        // Fields & Properties — GameManager
        private static PropertyInfo _gmPlayerUnitProp;
        private static PropertyInfo _gmPlayerObjectProp;

        // Fields & Properties — UIManager
        private static PropertyInfo _uiPlayerBackpackGridProp;

        // AddItem on ItemGrid
        private static MethodInfo _addItemMethod;

        // InventoryData constructor
        private static ConstructorInfo _invDataCtor;

        #endregion

        #region MonoMod Delegates

        // SpawnPickup: instance method, returns Pickup, 7 params
        private delegate object orig_SpawnPickup(
            object self, Vector3 pos, bool motionTowardsPlayer,
            object item, object insideRoom, object inventoryData,
            object spawnedIn, float minPickupDelay);

        private delegate object hook_SpawnPickup(
            orig_SpawnPickup orig, object self, Vector3 pos,
            bool motionTowardsPlayer, object item, object insideRoom,
            object inventoryData, object spawnedIn, float minPickupDelay);

        // ExecutePickup: private instance method, returns bool, 1 param
        private delegate bool orig_ExecutePickup(object self, object pickup);
        private delegate bool hook_ExecutePickup(orig_ExecutePickup orig, object self, object pickup);

        // Container.OnInteract: public virtual instance, returns bool, 1 param
        private delegate bool orig_ContainerOnInteract(object self, object player);
        private delegate bool hook_ContainerOnInteract(orig_ContainerOnInteract orig, object self, object player);

        // ChurchCollectionLootable.Loot(): protected override void, no params
        private delegate void orig_ChurchLoot(object self);
        private delegate void hook_ChurchLoot(orig_ChurchLoot orig, object self);

        #endregion

        #region Lifecycle

        private void Awake()
        {
            if (Instance != null)
            {
                Destroy(this);
                return;
            }
            Instance = this;
        }

        private void OnEnable()
        {
            NetworkEvents.OnMessageReceived += OnMessageReceived;
            NetworkEvents.OnDisconnected += OnDisconnected;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDisable()
        {
            NetworkEvents.OnMessageReceived -= OnMessageReceived;
            NetworkEvents.OnDisconnected -= OnDisconnected;
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnDestroy()
        {
            DisposeHooks();
            if (Instance == this)
                Instance = null;
        }

        private void Update()
        {
            if (!_hookAttempted)
                TryInstallHooks();

            ProcessPendingSpawns();
        }

        private void OnDisconnected(string reason)
        {
            ClearState();
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            ClearState();
        }

        private void ClearState()
        {
            Registry.Clear();
            _pendingSpawns.Clear();
            _pendingPickupRequests.Clear();
            _itemDefCache.Clear();
        }

        #endregion

        #region Reflection & Hook Installation

        private static void InitReflection()
        {
            if (_reflectionInit) return;
            _reflectionInit = true;

            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        var fn = type.FullName;
                        if (fn == null) continue;

                        if (fn == "PerfectRandom.Sulfur.Core.InteractionManager") _interactionManagerType = type;
                        else if (fn == "PerfectRandom.Sulfur.Core.Pickup") _pickupType = type;
                        else if (fn == "PerfectRandom.Sulfur.Core.Items.ItemDefinition") _itemDefinitionType = type;
                        else if (fn == "PerfectRandom.Sulfur.Core.ItemId") _itemIdType = type;
                        else if (fn == "PerfectRandom.Sulfur.Core.Items.InventoryData") _inventoryDataType = type;
                        else if (fn == "PerfectRandom.Sulfur.Core.World.Container") _containerType = type;
                        else if (fn == "PerfectRandom.Sulfur.Core.Items.CurrencySO") _currencySOType = type;
                        else if (fn == "PerfectRandom.Sulfur.Core.Items.ItemGrid") _itemGridType = type;
                        else if (fn == "PerfectRandom.Sulfur.Core.Units.Unit") _unitType = type;
                        else if (fn == "PerfectRandom.Sulfur.Core.CaliberTypes") _caliberType = type;
                        else if (fn == "PerfectRandom.Sulfur.Core.AsyncAssetLoading") _asyncAssetLoadingType = type;
                        else if (fn == "PerfectRandom.Sulfur.Core.ItemDatabase") _itemDatabaseType = type;
                        else if (fn == "PerfectRandom.Sulfur.Core.Stats.ItemAttributeCollectionData") _itemAttrCollDataType = type;
                        else if (fn == "PerfectRandom.Sulfur.Core.Stats.ItemAttributeData") _itemAttrDataType = type;
                        else if (fn == "PerfectRandom.Sulfur.Core.Stats.ItemAttributes") _itemAttributesEnumType = type;
                        else if (fn == "PerfectRandom.Sulfur.Gameplay.ChurchCollectionLootable") _churchCollectionLootableType = type;
                        else if (fn == "PerfectRandom.Sulfur.Gameplay.LootableObject") _lootableObjectType = type;
                    }
                }
                catch (ReflectionTypeLoadException) { }
            }

            // Also check global namespace for types that might not have PerfectRandom prefix
            if (_itemIdType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        _itemIdType = _itemIdType ?? asm.GetType("ItemId");
                        _caliberType = _caliberType ?? asm.GetType("CaliberTypes");
                    }
                    catch { }
                }
            }

            // GameManager — reuse reflection pattern from other managers
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (var type in asm.GetTypes())
                    {
                        if (type.Name == "GameManager" && type.GetProperty("PlayerUnit") != null)
                        {
                            _gameManagerType = type;
                            break;
                        }
                    }
                }
                catch (ReflectionTypeLoadException) { }
                if (_gameManagerType != null) break;
            }

            // Singleton instance properties (StaticInstance<T> pattern)
            _imInstanceProp = _interactionManagerType?.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            _gmInstanceProp = _gameManagerType?.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            _aalInstanceProp = _asyncAssetLoadingType?.GetProperty("Instance",
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);

            // UIManager — find type and instance prop
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var uiType = asm.GetType("PerfectRandom.Sulfur.Core.UI.UIManager");
                    if (uiType != null)
                    {
                        _uiInstanceProp = uiType.GetProperty("Instance",
                            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                        _uiPlayerBackpackGridProp = uiType.GetProperty("PlayerBackpackGrid",
                            BindingFlags.Public | BindingFlags.Instance);
                        break;
                    }
                }
                catch { }
            }

            // Hook target methods
            _spawnPickupMethod = _interactionManagerType?.GetMethod("SpawnPickup",
                BindingFlags.Public | BindingFlags.Instance);
            _executePickupMethod = _interactionManagerType?.GetMethod("ExecutePickup",
                BindingFlags.NonPublic | BindingFlags.Instance);
            _containerOnInteractMethod = _containerType?.GetMethod("OnInteract",
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);

            // Church collection hook targets
            _churchLootMethod = _churchCollectionLootableType?.GetMethod("Loot",
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            _lootableTriggerMethod = _lootableObjectType?.GetMethod("Trigger",
                BindingFlags.Public | BindingFlags.Instance);
            // lootHasBeenSpawned is private on base LootableObject — must get from base type
            _lootHasBeenSpawnedField = _lootableObjectType?.GetField("lootHasBeenSpawned",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Callable methods
            _removePickupMethod = _interactionManagerType?.GetMethod("RemovePickup",
                BindingFlags.Public | BindingFlags.Instance);

            if (_unitType != null && _itemDefinitionType != null)
            {
                foreach (var m in _unitType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "ConsumeItem")
                    {
                        var ps = m.GetParameters();
                        if (ps.Length == 1 && ps[0].ParameterType == _itemDefinitionType)
                        {
                            _consumeItemMethod = m;
                            break;
                        }
                    }
                }
            }

            // ItemGrid.AddItem(ItemDefinition, bool, bool, InventoryData)
            if (_itemGridType != null && _itemDefinitionType != null && _inventoryDataType != null)
            {
                foreach (var m in _itemGridType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (m.Name == "AddItem")
                    {
                        var ps = m.GetParameters();
                        if (ps.Length == 4 && ps[0].ParameterType == _itemDefinitionType)
                        {
                            _addItemMethod = m;
                            break;
                        }
                    }
                }
            }

            // Item resolution
            _aalItemDatabaseProp = _asyncAssetLoadingType?.GetProperty("itemDatabase",
                BindingFlags.Public | BindingFlags.Instance);

            // ItemDefinition fields
            _itemDefIdField = _itemDefinitionType?.GetField("id",
                BindingFlags.Public | BindingFlags.Instance);
            _itemIdValueField = _itemIdType?.GetField("value",
                BindingFlags.Public | BindingFlags.Instance);
            _itemDefInvSizeField = _itemDefinitionType?.GetField("inventorySize",
                BindingFlags.Public | BindingFlags.Instance);

            // Pickup properties
            _pickupItemSOProp = _pickupType?.GetProperty("ItemSO",
                BindingFlags.Public | BindingFlags.Instance);
            _pickupInvDataProp = _pickupType?.GetProperty("inventoryData",
                BindingFlags.Public | BindingFlags.Instance);

            // Container fields
            _containerLootedField = _containerType?.GetField("looted",
                BindingFlags.NonPublic | BindingFlags.Instance);

            // Interactable.animator — Container's base type
            if (_containerType != null)
            {
                var interactableType = _containerType.BaseType;
                if (interactableType != null)
                {
                    _interactableAnimatorField = interactableType.GetField("animator",
                        BindingFlags.Public | BindingFlags.Instance);
                }
            }

            // InventoryData fields
            _invDataIdField = _inventoryDataType?.GetField("id",
                BindingFlags.Public | BindingFlags.Instance);
            _invDataQuantityField = _inventoryDataType?.GetField("quantity",
                BindingFlags.Public | BindingFlags.Instance);
            _invDataCurrentAmmoField = _inventoryDataType?.GetField("currentAmmo",
                BindingFlags.Public | BindingFlags.Instance);
            _invDataCaliberField = _inventoryDataType?.GetField("caliberId",
                BindingFlags.Public | BindingFlags.Instance);
            _invDataAttachmentsField = _inventoryDataType?.GetField("attachmentIds",
                BindingFlags.Public | BindingFlags.Instance);
            _invDataEnchantmentsField = _inventoryDataType?.GetField("enchantmentIds",
                BindingFlags.Public | BindingFlags.Instance);
            _invDataAttributesField = _inventoryDataType?.GetField("attributes",
                BindingFlags.Public | BindingFlags.Instance);
            _invDataBoughtForField = _inventoryDataType?.GetField("boughtFor",
                BindingFlags.Public | BindingFlags.Instance);

            // ItemAttributeCollectionData / ItemAttributeData / CharacterStat fields
            _itemAttrCollItemAttrsField = _itemAttrCollDataType?.GetField("itemAttributes",
                BindingFlags.Public | BindingFlags.Instance);
            _itemAttrDataIdField = _itemAttrDataType?.GetField("id",
                BindingFlags.Public | BindingFlags.Instance);
            _itemAttrDataValueField = _itemAttrDataType?.GetField("value",
                BindingFlags.Public | BindingFlags.Instance);
            // CharacterStat type — find via ItemAttributeData.value's field type
            if (_itemAttrDataValueField != null)
            {
                var charStatType = _itemAttrDataValueField.FieldType;
                _charStatBaseValueField = charStatType?.GetField("BaseValue",
                    BindingFlags.Public | BindingFlags.Instance);
            }

            // GameManager properties
            _gmPlayerUnitProp = _gameManagerType?.GetProperty("PlayerUnit",
                BindingFlags.Public | BindingFlags.Instance);
            _gmPlayerObjectProp = _gameManagerType?.GetProperty("PlayerObject",
                BindingFlags.Public | BindingFlags.Instance);

            // InventoryData constructor: (ItemId, int, int, int, int, CaliberTypes, object, ItemId[], ItemId[], int, int, int, bool, bool)
            if (_inventoryDataType != null)
            {
                foreach (var ctor in _inventoryDataType.GetConstructors())
                {
                    var ps = ctor.GetParameters();
                    if (ps.Length >= 12 && ps[1].ParameterType == typeof(int))
                    {
                        _invDataCtor = ctor;
                        break;
                    }
                }
            }

            LogReflectionStatus();
        }

        private static void LogReflectionStatus()
        {
            int found = 0, total = 0;
            total++; if (_interactionManagerType != null) found++;
            total++; if (_pickupType != null) found++;
            total++; if (_itemDefinitionType != null) found++;
            total++; if (_itemIdType != null) found++;
            total++; if (_inventoryDataType != null) found++;
            total++; if (_containerType != null) found++;
            total++; if (_currencySOType != null) found++;
            total++; if (_spawnPickupMethod != null) found++;
            total++; if (_executePickupMethod != null) found++;
            total++; if (_containerOnInteractMethod != null) found++;
            total++; if (_removePickupMethod != null) found++;
            total++; if (_aalItemDatabaseProp != null) found++;
            Plugin.Log.LogInfo($"ItemSync: Reflection resolved {found}/{total} targets");

            if (_spawnPickupMethod == null) Plugin.Log.LogWarning("ItemSync: SpawnPickup method not found");
            if (_executePickupMethod == null) Plugin.Log.LogWarning("ItemSync: ExecutePickup method not found");
            if (_containerOnInteractMethod == null) Plugin.Log.LogWarning("ItemSync: Container.OnInteract method not found");
        }

        private void TryInstallHooks()
        {
            InitReflection();

            if (_spawnPickupMethod == null && _executePickupMethod == null)
            {
                _hookRetryCount++;
                if (_hookRetryCount >= MaxHookRetries)
                {
                    _hookAttempted = true;
                    Plugin.Log.LogWarning("ItemSync: Could not find hook targets after max retries");
                }
                return;
            }

            _hookAttempted = true;

            if (_spawnPickupMethod != null)
            {
                try
                {
                    _spawnPickupHook = new Hook(
                        _spawnPickupMethod,
                        new hook_SpawnPickup(SpawnPickupInterceptor));
                    Plugin.Log.LogInfo("ItemSync: Hooked InteractionManager.SpawnPickup");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"ItemSync: Failed to hook SpawnPickup: {ex}");
                }
            }

            if (_executePickupMethod != null)
            {
                try
                {
                    _executePickupHook = new Hook(
                        _executePickupMethod,
                        new hook_ExecutePickup(ExecutePickupInterceptor));
                    Plugin.Log.LogInfo("ItemSync: Hooked InteractionManager.ExecutePickup");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"ItemSync: Failed to hook ExecutePickup: {ex}");
                }
            }

            if (_containerOnInteractMethod != null)
            {
                try
                {
                    _containerOnInteractHook = new Hook(
                        _containerOnInteractMethod,
                        new hook_ContainerOnInteract(ContainerOnInteractInterceptor));
                    Plugin.Log.LogInfo("ItemSync: Hooked Container.OnInteract");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"ItemSync: Failed to hook Container.OnInteract: {ex}");
                }
            }

            if (_churchLootMethod != null)
            {
                try
                {
                    _churchCollectionLootHook = new Hook(
                        _churchLootMethod,
                        new hook_ChurchLoot(ChurchCollectionLootInterceptor));
                    Plugin.Log.LogInfo("ItemSync: Hooked ChurchCollectionLootable.Loot");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"ItemSync: Failed to hook ChurchCollectionLootable.Loot: {ex}");
                }
            }
        }

        private void DisposeHooks()
        {
            _spawnPickupHook?.Dispose();
            _spawnPickupHook = null;
            _executePickupHook?.Dispose();
            _executePickupHook = null;
            _containerOnInteractHook?.Dispose();
            _containerOnInteractHook = null;
            _churchCollectionLootHook?.Dispose();
            _churchCollectionLootHook = null;
            _hookAttempted = false;
        }

        #endregion

        #region SpawnPickup Hook

        private static object SpawnPickupInterceptor(
            orig_SpawnPickup orig, object self, Vector3 pos,
            bool motionTowardsPlayer, object item, object insideRoom,
            object inventoryData, object spawnedIn, float minPickupDelay)
        {
            var instance = Instance;
            var net = NetworkManager.Instance;
            if (instance == null || net == null || !net.IsConnected)
                return orig(self, pos, motionTowardsPlayer, item, insideRoom, inventoryData, spawnedIn, minPickupDelay);

            // Network spawn bypass — client creating pickup from network message
            if (_isNetworkSpawn)
            {
                return orig(self, pos, motionTowardsPlayer, item, insideRoom, inventoryData, spawnedIn, minPickupDelay);
            }

            // Coins (CurrencySO) are independent per-player.
            // Host spawns locally AND broadcasts to clients; client suppresses native (RNG-different) coins.
            if (item != null && _currencySOType != null && _currencySOType.IsInstanceOfType(item))
            {
                if (net.IsHost)
                {
                    var result = orig(self, pos, motionTowardsPlayer, item, insideRoom, inventoryData, spawnedIn, minPickupDelay);
                    if (result != null)
                    {
                        ushort itemId = GetItemIdValue(item);
                        net.SendToAll(new ItemSpawnMessage
                        {
                            PickupId = 0, // sentinel: independent coin, not tracked in registry
                            ItemId = itemId,
                            PosX = pos.x,
                            PosY = pos.y,
                            PosZ = pos.z,
                            Quantity = 1,
                            HasInventoryData = false,
                        });
                    }
                    return result;
                }
                else
                {
                    // Client: suppress native coin spawns (different RNG = wrong coins).
                    // Client coins only come from network messages.
                    return null;
                }
            }

            if (net.IsHost)
            {
                // Host: call orig, assign network ID, broadcast to clients
                var result = orig(self, pos, motionTowardsPlayer, item, insideRoom, inventoryData, spawnedIn, minPickupDelay);
                if (result == null) return null;

                var go = ((Component)result).gameObject;
                ushort pickupId = instance.Registry.AssignId(go);
                ushort itemId = GetItemIdValue(item);

                var msg = new ItemSpawnMessage
                {
                    PickupId = pickupId,
                    ItemId = itemId,
                    PosX = pos.x,
                    PosY = pos.y,
                    PosZ = pos.z,
                    Quantity = 1,
                    HasInventoryData = inventoryData != null,
                };

                if (inventoryData != null)
                {
                    ExtractInventoryData(inventoryData,
                        out var qty, out var ammo, out var cal,
                        out var attachIds, out var enchantIds,
                        out var bFor, out var aIds, out var aVals);
                    msg.Quantity = qty;
                    msg.CurrentAmmo = ammo;
                    msg.CaliberId = cal;
                    msg.AttachmentIds = attachIds;
                    msg.EnchantmentIds = enchantIds;
                    msg.BoughtFor = bFor;
                    msg.AttrIds = aIds;
                    msg.AttrValues = aVals;
                }

                net.SendToAll(msg);
                return result;
            }
            else
            {
                // Client: check if this is a player drop (inventoryData != null from DropFromPlayer)
                if (inventoryData != null)
                {
                    // Player dropping an item — send ItemDropMessage to host
                    ushort itemId = GetItemIdValue(item);
                    ExtractInventoryData(inventoryData,
                        out var qty, out var ammo, out var cal,
                        out var attachIds, out var enchantIds,
                        out var bFor, out var aIds, out var aVals);
                    var dropMsg = new ItemDropMessage
                    {
                        ItemId = itemId,
                        PosX = pos.x,
                        PosY = pos.y,
                        PosZ = pos.z,
                        Quantity = qty,
                        CurrentAmmo = ammo,
                        CaliberId = cal,
                        AttachmentIds = attachIds,
                        EnchantmentIds = enchantIds,
                        BoughtFor = bFor,
                        AttrIds = aIds,
                        AttrValues = aVals,
                    };
                    net.SendToAll(dropMsg); // client→host
                    return null; // suppress local spawn; item appears after host roundtrip
                }

                // Client: suppress all other local spawns (loot, containers, placed loot)
                return null;
            }
        }

        #endregion

        #region ExecutePickup Hook

        private static bool ExecutePickupInterceptor(
            orig_ExecutePickup orig, object self, object pickup)
        {
            var instance = Instance;
            var net = NetworkManager.Instance;
            if (instance == null || net == null || !net.IsConnected)
                return orig(self, pickup);

            // Coins (CurrencySO) are independent per-player — bypass sync entirely.
            if (IsCurrencySO(pickup))
                return orig(self, pickup);

            var go = ((Component)pickup).gameObject;

            if (net.IsHost)
            {
                // Host picking up an item — call orig
                bool success = orig(self, pickup);
                if (!success) return false;

                // Get item info before it's destroyed
                ushort pickupId = 0;
                instance.Registry.TryGetId(go, out pickupId);
                ushort itemId = GetPickupItemId(pickup);

                if (pickupId != 0)
                {
                    // Broadcast pickup event to all clients
                    net.SendToAll(new ItemPickedUpMessage
                    {
                        PickupId = pickupId,
                        PickerSteamId = net.LocalSteamId.m_SteamID,
                        ItemId = itemId,
                    });
                    instance.Registry.Remove(pickupId);
                }

                return true;
            }
            else
            {
                // Client: suppress local pickup, send request to host
                ushort pickupId = 0;
                if (!instance.Registry.TryGetId(go, out pickupId) || pickupId == 0)
                    return false;

                if (instance._pendingPickupRequests.Contains(pickupId))
                    return false; // already requested, waiting for response

                instance._pendingPickupRequests.Add(pickupId);
                net.SendToAll(new ItemPickupRequestMessage { PickupId = pickupId });
                return false;
            }
        }

        #endregion

        #region Container.OnInteract Hook

        private static bool ContainerOnInteractInterceptor(
            orig_ContainerOnInteract orig, object self, object player)
        {
            var instance = Instance;
            var net = NetworkManager.Instance;
            if (instance == null || net == null || !net.IsConnected)
                return orig(self, player);

            // If this is a remote container interact triggered by host on behalf of client
            if (_isRemoteContainerInteract)
                return orig(self, player);

            if (net.IsHost)
            {
                // Host: call orig normally (SpawnPickup hook handles item broadcast)
                bool result = orig(self, player);
                if (result)
                {
                    // Broadcast container looted status to clients
                    var containerPos = ((Component)self).transform.position;
                    net.SendToAll(new ContainerLootedMessage
                    {
                        PosX = containerPos.x,
                        PosY = containerPos.y,
                        PosZ = containerPos.z,
                    });
                }
                return result;
            }
            else
            {
                // Client: suppress, send request to host
                var containerPos = ((Component)self).transform.position;
                net.SendToAll(new ContainerInteractMessage
                {
                    PosX = containerPos.x,
                    PosY = containerPos.y,
                    PosZ = containerPos.z,
                });
                return false;
            }
        }

        #endregion

        #region ChurchCollectionLootable.Loot Hook

        private static void ChurchCollectionLootInterceptor(orig_ChurchLoot orig, object self)
        {
            var net = NetworkManager.Instance;
            if (net == null || !net.IsConnected)
            {
                orig(self); // single-player: normal behavior
                return;
            }

            if (net.IsHost)
            {
                orig(self); // host: normal flow (SpawnPickup hook broadcasts items)
                return;
            }

            // Client: suppress item spawning (client has no stash data anyway).
            // The opening animation was already started by Trigger() → "Loot" animator trigger.
            // Send request to host to process using host's stash data.
            net.SendToAll(new ChurchCollectionLootMessage());
        }

        #endregion

        #region Message Routing

        private void OnMessageReceived(CSteamID sender, NetworkMessage msg)
        {
            switch (msg.Type)
            {
                case MessageType.ItemSpawn:
                    HandleItemSpawn((ItemSpawnMessage)msg);
                    break;
                case MessageType.ItemPickup:
                    HandleItemPickedUp((ItemPickedUpMessage)msg);
                    break;
                case MessageType.ItemDespawn: // ItemPickupRequestMessage
                    if (NetworkManager.Instance?.IsHost == true)
                        HandleItemPickupRequest(sender, (ItemPickupRequestMessage)msg);
                    break;
                case MessageType.ContainerSync: // ContainerInteractMessage
                    if (NetworkManager.Instance?.IsHost == true)
                        HandleContainerInteract(sender, (ContainerInteractMessage)msg);
                    break;
                case MessageType.ContainerLooted:
                    HandleContainerLooted((ContainerLootedMessage)msg);
                    break;
                case MessageType.ItemDrop:
                    if (NetworkManager.Instance?.IsHost == true)
                        HandleItemDrop(sender, (ItemDropMessage)msg);
                    break;
                case MessageType.SharedGold:
                    HandleSharedGold((SharedGoldMessage)msg);
                    break;
                case MessageType.ChurchCollectionLoot:
                    if (NetworkManager.Instance?.IsHost == true)
                        HandleChurchCollectionLootRequest(sender);
                    break;
            }
        }

        #endregion

        #region Message Handlers

        /// <summary>
        /// Client: host spawned an item in the world. Create local Pickup.
        /// </summary>
        private void HandleItemSpawn(ItemSpawnMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || net.IsHost) return;

            // If InteractionManager isn't available yet (scene loading), buffer the message
            var im = GetSingletonInstance(_imInstanceProp);
            if (im == null)
            {
                _pendingSpawns.Add(msg);
                return;
            }

            CreatePickupFromMessage(msg, im);
        }

        private void CreatePickupFromMessage(ItemSpawnMessage msg, object interactionManager)
        {
            var itemDef = ResolveItemDefinition(msg.ItemId);
            if (itemDef == null)
            {
                Plugin.Log.LogWarning($"ItemSync: Cannot resolve item {msg.ItemId} for spawn");
                return;
            }

            // Reconstruct InventoryData if present
            object invData = null;
            if (msg.HasInventoryData)
            {
                invData = CreateInventoryData(msg.ItemId, msg.Quantity, msg.CurrentAmmo,
                    msg.CaliberId, msg.AttachmentIds, msg.EnchantmentIds, itemDef,
                    msg.BoughtFor, msg.AttrIds, msg.AttrValues);
            }

            var pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);

            _isNetworkSpawn = true;
            try
            {
                var result = _spawnPickupMethod.Invoke(interactionManager,
                    new object[] { pos, false, itemDef, null, invData, null, 0f });

                if (result != null)
                {
                    var go = ((Component)result).gameObject;
                    if (msg.PickupId != 0) // 0 = independent coin, not tracked
                        Registry.Register(msg.PickupId, go);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ItemSync: Failed to create pickup from message: {ex}");
            }
            finally
            {
                _isNetworkSpawn = false;
            }
        }

        /// <summary>
        /// Client: an item was picked up by someone.
        /// If it was us, add to inventory. Remove the visual pickup.
        /// </summary>
        private void HandleItemPickedUp(ItemPickedUpMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || net.IsHost) return;

            _pendingPickupRequests.Remove(msg.PickupId);

            bool isLocalPicker = msg.PickerSteamId == net.LocalSteamId.m_SteamID;

            // Try to get the local Pickup before destroying it
            if (isLocalPicker && Registry.TryGetPickup(msg.PickupId, out var pickupGo))
            {
                var itemDef = ResolveItemDefinition(msg.ItemId);
                bool isCurrency = itemDef != null && _currencySOType != null &&
                                  _currencySOType.IsInstanceOfType(itemDef);

                if (!isCurrency && itemDef != null)
                {
                    // Non-currency item: add to local inventory with inventoryData if available
                    object invData = null;
                    var pickupComp = pickupGo.GetComponent(_pickupType);
                    if (pickupComp != null && _pickupInvDataProp != null)
                        invData = _pickupInvDataProp.GetValue(pickupComp, null);

                    AddItemToLocalInventory(itemDef, invData);
                }
                // Currency items: gold already added via SharedGoldMessage
            }

            // Destroy the visual pickup
            DestroyPickupByNetId(msg.PickupId);
        }

        /// <summary>
        /// Host: a client wants to pick up an item. Validate and broadcast.
        /// </summary>
        private void HandleItemPickupRequest(CSteamID sender, ItemPickupRequestMessage msg)
        {
            if (!Registry.TryGetPickup(msg.PickupId, out var pickupGo))
                return; // item doesn't exist (already picked up)

            var pickupComp = pickupGo.GetComponent(_pickupType);
            if (pickupComp == null) return;

            ushort itemId = GetPickupItemId(pickupComp);

            // Remove the pickup from the world
            var im = GetSingletonInstance(_imInstanceProp);
            if (im != null && _removePickupMethod != null)
            {
                try
                {
                    _removePickupMethod.Invoke(im, new object[] { pickupComp });
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"ItemSync: RemovePickup failed: {ex}");
                }
            }

            // Broadcast pickup event to all clients
            NetworkManager.Instance.SendToAll(new ItemPickedUpMessage
            {
                PickupId = msg.PickupId,
                PickerSteamId = sender.m_SteamID,
                ItemId = itemId,
            });

            Registry.Remove(msg.PickupId);
        }

        /// <summary>
        /// Host: a client wants to open a container. Find it and interact.
        /// </summary>
        private void HandleContainerInteract(CSteamID sender, ContainerInteractMessage msg)
        {
            var containerPos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            var container = FindContainerAtPosition(containerPos);
            if (container == null) return;

            // Check if already looted
            if (_containerLootedField != null)
            {
                bool looted = (bool)_containerLootedField.GetValue(container);
                if (looted) return;
            }

            // Interact on behalf of client (SpawnPickup hook broadcasts the item)
            if (_containerOnInteractMethod != null)
            {
                _isRemoteContainerInteract = true;
                try
                {
                    _containerOnInteractMethod.Invoke(container, new object[] { null });
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"ItemSync: Container interact failed: {ex}");
                }
                finally
                {
                    _isRemoteContainerInteract = false;
                }

                // Broadcast looted status
                var actualPos = ((Component)container).transform.position;
                NetworkManager.Instance.SendToAll(new ContainerLootedMessage
                {
                    PosX = actualPos.x,
                    PosY = actualPos.y,
                    PosZ = actualPos.z,
                });
            }
        }

        /// <summary>
        /// Client: a container was looted. Mark it and play animation.
        /// </summary>
        private void HandleContainerLooted(ContainerLootedMessage msg)
        {
            var net = NetworkManager.Instance;
            if (net == null || net.IsHost) return;

            var pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);
            var container = FindContainerAtPosition(pos);
            if (container == null) return;

            // Mark as looted
            if (_containerLootedField != null)
                _containerLootedField.SetValue(container, true);

            // Play open animation — Container uses GetComponentInChildren<Animator>() to find
            // the modelAnimator on a child object, and triggers "Open" (not "Interact")
            if (container is Component containerComp)
            {
                var animator = containerComp.GetComponentInChildren<Animator>();
                if (animator != null && animator.isActiveAndEnabled)
                    animator.SetTrigger("Open");
            }
        }

        /// <summary>
        /// Host: a client dropped an item. Spawn it in the world (broadcasts via SpawnPickup hook).
        /// </summary>
        private void HandleItemDrop(CSteamID sender, ItemDropMessage msg)
        {
            var itemDef = ResolveItemDefinition(msg.ItemId);
            if (itemDef == null)
            {
                Plugin.Log.LogWarning($"ItemSync: Cannot resolve dropped item {msg.ItemId}");
                return;
            }

            // Reconstruct InventoryData
            var invData = CreateInventoryData(msg.ItemId, msg.Quantity, msg.CurrentAmmo,
                msg.CaliberId, msg.AttachmentIds, msg.EnchantmentIds, itemDef,
                msg.BoughtFor, msg.AttrIds, msg.AttrValues);

            var pos = new Vector3(msg.PosX, msg.PosY, msg.PosZ);

            // Spawn on host — SpawnPickup hook will broadcast ItemSpawnMessage to all clients
            var im = GetSingletonInstance(_imInstanceProp);
            if (im != null && _spawnPickupMethod != null)
            {
                try
                {
                    _spawnPickupMethod.Invoke(im,
                        new object[] { pos, false, itemDef, null, invData, null, 0f });
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"ItemSync: Failed to spawn dropped item: {ex}");
                }
            }
        }

        /// <summary>
        /// Client: shared gold — consume the coin item to give everyone the value.
        /// </summary>
        private void HandleSharedGold(SharedGoldMessage msg)
        {
            var itemDef = ResolveItemDefinition(msg.ItemId);
            if (itemDef == null) return;

            ConsumeItemOnLocalPlayer(itemDef);
        }

        /// <summary>
        /// Host: a client wants to loot the church collection box.
        /// Find it in the scene, guard against double-loot, and call Trigger().
        /// Trigger() → animation → Loot() → SpawnPickup → existing broadcast.
        /// </summary>
        private void HandleChurchCollectionLootRequest(CSteamID sender)
        {
            if (_churchCollectionLootableType == null || _lootableTriggerMethod == null) return;

            // Find the ChurchCollectionLootable in the scene (only one per church level)
            var collectionBox = UnityEngine.Object.FindObjectOfType(_churchCollectionLootableType);
            if (collectionBox == null) return;

            // Guard against double-loot via lootHasBeenSpawned on base LootableObject
            if (_lootHasBeenSpawnedField != null)
            {
                bool alreadyLooted = (bool)_lootHasBeenSpawnedField.GetValue(collectionBox);
                if (alreadyLooted) return;
                // Set it now to prevent concurrent requests
                _lootHasBeenSpawnedField.SetValue(collectionBox, true);
            }

            // Call Trigger() which plays sound + animation → eventually calls Loot() →
            // our hook sees IsHost=true → calls orig → LootSpawnRoutine → SpawnPickup broadcasts
            try
            {
                _lootableTriggerMethod.Invoke(collectionBox, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ItemSync: ChurchCollectionLootable.Trigger() failed: {ex}");
            }
        }

        #endregion

        #region Helpers

        private void ProcessPendingSpawns()
        {
            if (_pendingSpawns.Count == 0) return;

            var im = GetSingletonInstance(_imInstanceProp);
            if (im == null) return;

            // Process all buffered spawns
            for (int i = _pendingSpawns.Count - 1; i >= 0; i--)
            {
                CreatePickupFromMessage(_pendingSpawns[i], im);
            }
            _pendingSpawns.Clear();
        }

        /// <summary>
        /// Resolve an ItemId ushort value to an ItemDefinition ScriptableObject.
        /// Uses AsyncAssetLoading.Instance.itemDatabase[itemId] with fallback to FindObjectsOfTypeAll.
        /// </summary>
        private object ResolveItemDefinition(ushort itemIdValue)
        {
            if (itemIdValue == 0) return null;

            if (_itemDefCache.TryGetValue(itemIdValue, out var cached))
                return cached;

            // Try primary path: AsyncAssetLoading.Instance.itemDatabase[ItemId]
            if (_aalInstanceProp != null && _aalItemDatabaseProp != null && _itemDatabaseType != null)
            {
                try
                {
                    var aal = _aalInstanceProp.GetValue(null, null);
                    if (aal != null)
                    {
                        var db = _aalItemDatabaseProp.GetValue(aal, null);
                        if (db != null)
                        {
                            // ItemDatabase has an indexer that takes ItemId
                            // Use the indexer property: this[ItemId]
                            var indexer = _itemDatabaseType.GetProperty("Item",
                                BindingFlags.Public | BindingFlags.Instance);
                            if (indexer != null && _itemIdType != null)
                            {
                                var itemId = Activator.CreateInstance(_itemIdType, new object[] { itemIdValue });
                                var result = indexer.GetValue(db, new object[] { itemId });
                                if (result != null)
                                {
                                    _itemDefCache[itemIdValue] = result;
                                    return result;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogDebug($"ItemSync: Primary item resolution failed: {ex.Message}");
                }
            }

            // Fallback: search all loaded ItemDefinitions
            if (_itemDefinitionType != null && _itemDefIdField != null && _itemIdValueField != null)
            {
                var allItems = Resources.FindObjectsOfTypeAll(_itemDefinitionType);
                foreach (var item in allItems)
                {
                    var id = _itemDefIdField.GetValue(item);
                    var val = (ushort)_itemIdValueField.GetValue(id);
                    if (!_itemDefCache.ContainsKey(val))
                        _itemDefCache[val] = item;
                }

                if (_itemDefCache.TryGetValue(itemIdValue, out cached))
                    return cached;
            }

            Plugin.Log.LogWarning($"ItemSync: Could not resolve ItemDefinition for id {itemIdValue}");
            return null;
        }

        /// <summary>
        /// Get the ushort ItemId value from an ItemDefinition object.
        /// </summary>
        private static ushort GetItemIdValue(object itemDefinition)
        {
            if (itemDefinition == null || _itemDefIdField == null || _itemIdValueField == null)
                return 0;

            try
            {
                var id = _itemDefIdField.GetValue(itemDefinition); // boxed ItemId struct
                return (ushort)_itemIdValueField.GetValue(id);
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Get the ItemId value from a Pickup component.
        /// </summary>
        private static ushort GetPickupItemId(object pickupComponent)
        {
            if (pickupComponent == null || _pickupItemSOProp == null) return 0;
            var itemDef = _pickupItemSOProp.GetValue(pickupComponent, null);
            return GetItemIdValue(itemDef);
        }

        /// <summary>
        /// Check if a Pickup's item is a CurrencySO (coin).
        /// </summary>
        private static bool IsCurrencySO(object pickupComponent)
        {
            if (_currencySOType == null || _pickupItemSOProp == null) return false;
            var itemDef = _pickupItemSOProp.GetValue(pickupComponent, null);
            return itemDef != null && _currencySOType.IsInstanceOfType(itemDef);
        }

        /// <summary>
        /// Extract all synced InventoryData fields from a game InventoryData object.
        /// </summary>
        private static void ExtractInventoryData(object inventoryData,
            out ushort quantity, out int currentAmmo, out byte caliberId,
            out ushort[] attachmentIds, out ushort[] enchantmentIds,
            out int boughtFor, out byte[] attrIds, out float[] attrValues)
        {
            quantity = 1;
            currentAmmo = 0;
            caliberId = 0;
            attachmentIds = null;
            enchantmentIds = null;
            boughtFor = 0;
            attrIds = null;
            attrValues = null;

            try
            {
                if (_invDataQuantityField != null)
                    quantity = (ushort)(int)_invDataQuantityField.GetValue(inventoryData);
                if (_invDataCurrentAmmoField != null)
                    currentAmmo = (int)_invDataCurrentAmmoField.GetValue(inventoryData);
                if (_invDataCaliberField != null)
                    caliberId = Convert.ToByte(_invDataCaliberField.GetValue(inventoryData));

                attachmentIds = ExtractItemIdArray(
                    _invDataAttachmentsField?.GetValue(inventoryData));
                enchantmentIds = ExtractItemIdArray(
                    _invDataEnchantmentsField?.GetValue(inventoryData));

                if (_invDataBoughtForField != null)
                    boughtFor = (int)_invDataBoughtForField.GetValue(inventoryData);

                ExtractAttributes(inventoryData, out attrIds, out attrValues);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ItemSync: Failed to extract InventoryData: {ex}");
            }
        }

        /// <summary>
        /// Extract ItemAttributeCollectionData from InventoryData into parallel byte[]/float[] arrays.
        /// </summary>
        private static void ExtractAttributes(object inventoryData,
            out byte[] attrIds, out float[] attrValues)
        {
            attrIds = null;
            attrValues = null;

            if (_invDataAttributesField == null || _itemAttrCollItemAttrsField == null ||
                _itemAttrDataIdField == null || _itemAttrDataValueField == null ||
                _charStatBaseValueField == null)
                return;

            try
            {
                var attrCollData = _invDataAttributesField.GetValue(inventoryData);
                if (attrCollData == null) return;

                var itemAttrs = _itemAttrCollItemAttrsField.GetValue(attrCollData) as Array;
                if (itemAttrs == null || itemAttrs.Length == 0) return;

                attrIds = new byte[itemAttrs.Length];
                attrValues = new float[itemAttrs.Length];

                for (int i = 0; i < itemAttrs.Length; i++)
                {
                    var attrData = itemAttrs.GetValue(i);
                    // id is an ItemAttributes enum — unbox via int then byte
                    var idEnum = _itemAttrDataIdField.GetValue(attrData);
                    attrIds[i] = (byte)(int)idEnum;
                    // value is a CharacterStat — read BaseValue
                    var charStat = _itemAttrDataValueField.GetValue(attrData);
                    if (charStat != null)
                        attrValues[i] = (float)_charStatBaseValueField.GetValue(charStat);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ItemSync: Failed to extract attributes: {ex}");
            }
        }

        /// <summary>
        /// Convert an ItemId[] (game type) to ushort[] for network serialization.
        /// </summary>
        private static ushort[] ExtractItemIdArray(object itemIdArray)
        {
            if (itemIdArray == null || _itemIdValueField == null) return null;

            var arr = itemIdArray as Array;
            if (arr == null || arr.Length == 0) return null;

            var result = new ushort[arr.Length];
            for (int i = 0; i < arr.Length; i++)
            {
                var element = arr.GetValue(i); // boxed ItemId
                result[i] = (ushort)_itemIdValueField.GetValue(element);
            }
            return result;
        }

        /// <summary>
        /// Create an InventoryData object from network message fields.
        /// </summary>
        private object CreateInventoryData(ushort itemIdValue, ushort quantity,
            int currentAmmo, byte caliberId, ushort[] attachmentIds,
            ushort[] enchantmentIds, object itemDef,
            int boughtFor = 0, byte[] attrIds = null, float[] attrValues = null)
        {
            if (_invDataCtor == null || _itemIdType == null) return null;

            try
            {
                // Create ItemId for the item
                var itemId = Activator.CreateInstance(_itemIdType, new object[] { itemIdValue });

                // Create CaliberTypes enum value
                object caliberEnum = _caliberType != null
                    ? Enum.ToObject(_caliberType, caliberId)
                    : caliberId;

                // Create ItemId[] arrays for attachments and enchantments
                var attachArr = CreateItemIdArray(attachmentIds);
                var enchantArr = CreateItemIdArray(enchantmentIds);

                // Get item inventory size for constructor
                int xSize = 1, ySize = 1;
                if (_itemDefInvSizeField != null && itemDef != null)
                {
                    var size = (Vector2Int)_itemDefInvSizeField.GetValue(itemDef);
                    xSize = size.x;
                    ySize = size.y;
                }

                // Create ItemAttributeCollectionData — populated if we have attrs, empty otherwise
                object attrCollData = (attrIds != null && attrIds.Length > 0)
                    ? CreateAttributeCollectionData(attrIds, attrValues)
                    : CreateEmptyAttributeCollectionData();

                // Call constructor: (ItemId, int x, int y, int quantity, int currentAmmo,
                //   CaliberTypes, ItemAttributeCollectionData, ItemId[], ItemId[], int boughtFor,
                //   int xSize, int ySize, bool rotated, bool selected)
                var args = new object[]
                {
                    itemId, 0, 0, (int)quantity, currentAmmo,
                    caliberEnum, attrCollData, attachArr, enchantArr, boughtFor,
                    xSize, ySize, false, false
                };

                return _invDataCtor.Invoke(args);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ItemSync: Failed to create InventoryData: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Create an ItemId[] (game type array) from ushort values.
        /// </summary>
        private object CreateItemIdArray(ushort[] values)
        {
            if (values == null || values.Length == 0 || _itemIdType == null)
            {
                // Return empty array of the correct type
                return _itemIdType != null ? Array.CreateInstance(_itemIdType, 0) : null;
            }

            var arr = Array.CreateInstance(_itemIdType, values.Length);
            for (int i = 0; i < values.Length; i++)
            {
                var itemId = Activator.CreateInstance(_itemIdType, new object[] { values[i] });
                arr.SetValue(itemId, i);
            }
            return arr;
        }

        /// <summary>
        /// Create an empty but valid ItemAttributeCollectionData.
        /// The game's ItemStats.LoadAttributesFromData crashes on null — needs a non-null
        /// object with an empty itemAttributes array.
        /// </summary>
        private static object CreateEmptyAttributeCollectionData()
        {
            if (_itemAttrCollDataType == null) return null;

            try
            {
                var attrCollData = Activator.CreateInstance(_itemAttrCollDataType);
                var itemAttributesField = _itemAttrCollDataType.GetField("itemAttributes",
                    BindingFlags.Public | BindingFlags.Instance);
                if (itemAttributesField != null && _itemAttrDataType != null)
                {
                    itemAttributesField.SetValue(attrCollData, Array.CreateInstance(_itemAttrDataType, 0));
                }
                return attrCollData;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ItemSync: Failed to create empty attribute data: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Create a populated ItemAttributeCollectionData from parallel id/value arrays.
        /// </summary>
        private static object CreateAttributeCollectionData(byte[] attrIds, float[] attrValues)
        {
            if (_itemAttrCollDataType == null || _itemAttrDataType == null ||
                _itemAttrCollItemAttrsField == null || _itemAttrDataIdField == null ||
                _itemAttrDataValueField == null || _charStatBaseValueField == null ||
                _itemAttributesEnumType == null)
                return CreateEmptyAttributeCollectionData();

            try
            {
                int count = attrIds.Length;
                var attrArray = Array.CreateInstance(_itemAttrDataType, count);

                for (int i = 0; i < count; i++)
                {
                    // Create ItemAttributeData(ItemAttributes id, CharacterStat value)
                    var enumVal = Enum.ToObject(_itemAttributesEnumType, (int)attrIds[i]);
                    // CharacterStat constructor: CharacterStat(float baseValue)
                    var charStat = Activator.CreateInstance(_charStatBaseValueField.DeclaringType,
                        new object[] { attrValues[i] });
                    var attrData = Activator.CreateInstance(_itemAttrDataType,
                        new object[] { enumVal, charStat });
                    attrArray.SetValue(attrData, i);
                }

                var attrCollData = Activator.CreateInstance(_itemAttrCollDataType);
                _itemAttrCollItemAttrsField.SetValue(attrCollData, attrArray);
                return attrCollData;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ItemSync: Failed to create attribute collection data: {ex}");
                return CreateEmptyAttributeCollectionData();
            }
        }

        /// <summary>
        /// Add an item to the local player's inventory via UIManager.PlayerBackpackGrid.AddItem.
        /// </summary>
        private void AddItemToLocalInventory(object itemDef, object inventoryData)
        {
            if (_addItemMethod == null || _uiInstanceProp == null || _uiPlayerBackpackGridProp == null)
                return;

            try
            {
                var uiManager = _uiInstanceProp.GetValue(null, null);
                if (uiManager == null) return;

                var backpackGrid = _uiPlayerBackpackGridProp.GetValue(uiManager, null);
                if (backpackGrid == null) return;

                _addItemMethod.Invoke(backpackGrid,
                    new object[] { itemDef, true, true, inventoryData });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ItemSync: AddItem failed: {ex}");
            }
        }

        /// <summary>
        /// Call Unit.ConsumeItem(ItemDefinition) on the local player's unit.
        /// Used for shared gold distribution.
        /// </summary>
        private void ConsumeItemOnLocalPlayer(object itemDef)
        {
            if (itemDef == null || _consumeItemMethod == null || _gmInstanceProp == null || _gmPlayerUnitProp == null)
                return;

            try
            {
                var gm = _gmInstanceProp.GetValue(null, null);
                if (gm == null) return;

                var playerUnit = _gmPlayerUnitProp.GetValue(gm, null);
                if (playerUnit == null) return;

                _consumeItemMethod.Invoke(playerUnit, new object[] { itemDef });
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"ItemSync: ConsumeItem failed: {ex}");
            }
        }

        /// <summary>
        /// Get a singleton instance via its static Instance property.
        /// </summary>
        private static object GetSingletonInstance(PropertyInfo instanceProp)
        {
            if (instanceProp == null) return null;
            try
            {
                return instanceProp.GetValue(null, null);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Find a Container component near the given world position.
        /// </summary>
        private object FindContainerAtPosition(Vector3 pos)
        {
            if (_containerType == null) return null;

            var containers = UnityEngine.Object.FindObjectsOfType(_containerType);
            if (containers == null || containers.Length == 0) return null;

            float bestDist = 2f; // 2m tolerance
            UnityEngine.Object best = null;

            foreach (var c in containers)
            {
                var comp = c as Component;
                if (comp == null) continue;
                float dist = Vector3.Distance(comp.transform.position, pos);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    best = c;
                }
            }

            return best;
        }

        /// <summary>
        /// Destroy a local Pickup by its network ID and remove from registry.
        /// </summary>
        private void DestroyPickupByNetId(ushort pickupId)
        {
            if (Registry.TryGetPickup(pickupId, out var go))
            {
                Registry.Remove(pickupId);
                if (go != null)
                {
                    // Try to use RemovePickup for proper pool cleanup
                    var im = GetSingletonInstance(_imInstanceProp);
                    var pickupComp = go.GetComponent(_pickupType);
                    if (im != null && pickupComp != null && _removePickupMethod != null)
                    {
                        try
                        {
                            _removePickupMethod.Invoke(im, new object[] { pickupComp });
                            return;
                        }
                        catch { }
                    }

                    // Fallback: destroy directly
                    UnityEngine.Object.Destroy(go);
                }
            }
        }

        #endregion
    }
}
