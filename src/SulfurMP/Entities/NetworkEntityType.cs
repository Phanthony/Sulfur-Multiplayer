namespace SulfurMP.Entities
{
    /// <summary>
    /// Categories of network-tracked entities. Extensible for future phases.
    /// </summary>
    public enum NetworkEntityType : byte
    {
        Unknown = 0,
        Npc = 1,
        // Future: Item = 2, Interactable = 3
    }
}
