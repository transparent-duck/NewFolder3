using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace DeepDungeon.Fsd.Dalamud.Items
{
    public static class ItemManager
    {
        private static readonly Dictionary<uint, ItemInfo> _items = new();
        private static bool _initialized;

        public static bool TryGet(uint id, out ItemInfo info) => _items.TryGetValue(id, out info);
        public static ItemInfo Get(uint id) => _items.TryGetValue(id, out var v) ? v : default;

        public static ItemInfo GetOrRegister(uint id)
        {
            if (_items.TryGetValue(id, out var existing))
                return existing;
            try
            {
                var sheet = Service.DataManager.GetExcelSheet<Item>();
                var row = sheet?.GetRow(id);
                if (row != null)
                {
                    var info = new ItemInfo(id, row.Value.Name.ToString(), row.Value.Icon, true);
                    _items[id] = info;
                    return info;
                }
            }
            catch (Exception ex)
            {
                Service.Log.Error($"[ItemManager] Failed to load item {id}: {ex}");
            }
            return default;
        }

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            // Lazy-load on demand; nothing to prewarm for now
        }
    }
}




