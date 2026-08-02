using System;

namespace DeepDungeon.Fsd.Dalamud.Items
{
    public struct ItemInfo
    {
        public uint Id { get; set; }
        public string Name { get; set; }
        public uint Icon { get; set; }
        public bool IsValid { get; set; }

        public ItemInfo(uint id, string name, uint icon, bool isValid = true)
        {
            Id = id;
            Name = name ?? "Unknown";
            Icon = icon;
            IsValid = isValid;
        }
    }
}




