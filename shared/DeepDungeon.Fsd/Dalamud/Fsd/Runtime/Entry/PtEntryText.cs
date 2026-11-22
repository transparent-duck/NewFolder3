using DeepDungeon.Fsd.Core;
using Lumina.Excel;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry;

/// <summary>Client-localized vocabulary, loaded per supported dungeon. No translated fallback.</summary>
internal sealed class PtEntryText
{
    private sealed record EntrySheetSpec(
        uint DungeonId,
        string SheetName,
        string Prefix,
        IReadOnlyDictionary<string, string> CanonicalKeys,
        IReadOnlyDictionary<int, string> FloorKeys,
        string FloorBackKey);

    private static readonly EntrySheetSpec[] Specs =
    [
        new(
            DungeonId: 1,
            SheetName: "custom/008/CtsDdd1Entrance_00821",
            Prefix: "TEXT_CTSDDD1ENTRANCE_00821_",
            CanonicalKeys: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["START_MENU_ENTER"] = "TEST_START_MENU_ENTER",
                ["START_MENU_RESET"] = "TEST_START_MENU_RESET",
                ["ENTER_MENU_TITLE"] = "TEST_ENTER_MENU_TITLE",
                ["ENTER_MENU_FIXED"] = "TEST_ENTER_MENU_FIXED",
                ["ENTER_MENU_MATCHING"] = "TEST_ENTER_MENU_MATCHING",
                ["ENTER_MENU_BACK"] = "TEST_ENTER_MENU_BACK",
                ["ENTER_FIXED_Q"] = "TEST_ENTER_FIXED_Q",
                ["ENTER_SOLO"] = "TEST_ENTER_SOLO",
                ["SOLO_EXPLAIN"] = "WOODWAILERCAPTAIN01556_000_050",
                ["ENTER_WHICH_Q"] = "ENTER_WHICH_Q",
            },
            FloorKeys: new Dictionary<int, string> { [1] = "ENTER_A1", [51] = "ENTER_A2" },
            FloorBackKey: "ENTER_MENU_BACK"),
        new(
            DungeonId: 2,
            SheetName: "custom/008/CtsDdd2Entrance_00824",
            Prefix: "TEXT_CTSDDD2ENTRANCE_00824_",
            CanonicalKeys: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["START_MENU_ENTER"] = "TEST_START_MENU_ENTER",
                ["START_MENU_RESET"] = "TEST_START_MENU_RESET",
                ["ENTER_MENU_TITLE"] = "TEST_ENTER_MENU_TITLE",
                ["ENTER_MENU_FIXED"] = "TEST_ENTER_MENU_FIXED",
                ["ENTER_MENU_MATCHING"] = "TEST_ENTER_MENU_MATCHING",
                ["ENTER_MENU_BACK"] = "TEST_ENTER_MENU_BACK",
                ["ENTER_FIXED_Q"] = "TEST_ENTER_FIXED_Q",
                ["ENTER_SOLO"] = "TEST_ENTER_SOLO",
                ["SOLO_EXPLAIN"] = "KYUSEI_000_010",
                ["ENTER_WHICH_Q"] = "ENTER_WHICH_Q",
            },
            FloorKeys: new Dictionary<int, string> { [1] = "ENTER_A1", [21] = "ENTER_A2" },
            FloorBackKey: "ENTER_MENU_BACK"),
        new(
            DungeonId: 4,
            SheetName: "custom/009/CtsDdd4Entrance_00942",
            Prefix: "TEXT_CTSDDD4ENTRANCE_00942_",
            CanonicalKeys: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["START_MENU_ENTER"] = "START_MENU_ENTER",
                ["START_MENU_RESET"] = "START_MENU_RESET",
                ["ENTER_MENU_TITLE"] = "ENTER_MENU_TITLE",
                ["ENTER_MENU_FIXED"] = "ENTER_MENU_FIXED",
                ["ENTER_MENU_MATCHING"] = "ENTER_MENU_MATCHING",
                ["ENTER_MENU_BACK"] = "ENTER_MENU_BACK",
                ["ENTER_FIXED_Q"] = "ENTER_FIXED_Q",
                ["ENTER_SOLO"] = "ENTER_SOLO",
                ["SOLO_EXPLAIN"] = "VANTHAU_000_010",
                ["ENTER_WHICH_Q"] = "ENTER_WHICH_Q",
            },
            FloorKeys: new Dictionary<int, string>
            {
                [1] = "ENTER_A1", [21] = "ENTER_A2", [31] = "ENTER_A4", [51] = "ENTER_A5", [71] = "ENTER_A6"
            },
            FloorBackKey: "ENTER_A3"),
    ];

    private static readonly Dictionary<uint, PtEntryText> CachedByDungeon = new();
    private static global::Dalamud.Game.ClientLanguage? _cachedLanguage;

    public static PtEntryText Current => ForDungeon(4);

    public static PtEntryText ForDungeon(uint dungeonId)
    {
        EnsureCache();
        if (CachedByDungeon.TryGetValue(dungeonId, out var text))
            return text;
        var spec = Specs.FirstOrDefault(s => s.DungeonId == dungeonId)
            ?? throw new InvalidOperationException("此地宮尚未提供內容識別契約。");
        text = new PtEntryText(spec);
        CachedByDungeon[dungeonId] = text;
        return text;
    }

    public static bool TryForDungeon(uint dungeonId, out PtEntryText text)
    {
        if (Specs.Any(s => s.DungeonId == dungeonId))
        {
            text = ForDungeon(dungeonId);
            return true;
        }
        text = null!;
        return false;
    }

    public static IReadOnlyList<PtEntryText> AllSupported
    {
        get
        {
            EnsureCache();
            var list = new List<PtEntryText>(Specs.Length);
            foreach (var spec in Specs)
                list.Add(ForDungeon(spec.DungeonId));
            return list;
        }
    }

    private static void EnsureCache()
    {
        var language = Service.DataManager.Language;
        if (_cachedLanguage != language)
        {
            CachedByDungeon.Clear();
            _cachedLanguage = language;
        }
    }

    private readonly EntrySheetSpec _spec;
    private readonly Dictionary<string, string> _raw = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _text = new(StringComparer.Ordinal);
    private readonly Dictionary<int, (uint Id, string Name)> _duties = new();
    private readonly string[] _soloExplainLines;
    public uint DungeonId => _spec.DungeonId;
    public string ReadSaveTitle { get; }
    public string DeleteSaveTitle { get; }
    public string NpcName { get; }

    public PtEntryText() : this(Specs[^1]) { }

    private PtEntryText(EntrySheetSpec spec)
    {
        _spec = spec;
        var dungeon = GameState.DungeonCatalog.TryGetByDungeonId(spec.DungeonId, out var found)
            ? found
            : throw new InvalidOperationException($"Unknown dungeon id: {spec.DungeonId}");
        var sheet = Service.DataManager.Excel.GetSheet<RawRow>(name: spec.SheetName);
        foreach (var row in sheet)
        {
            var key = row.ReadStringColumn(0).ExtractText();
            if (!key.StartsWith(spec.Prefix, StringComparison.Ordinal))
                continue;
            var suffix = key[spec.Prefix.Length..];
            var extracted = row.ReadStringColumn(1).ExtractText();
            _raw[suffix] = extracted;
            _text.Add(suffix, Normalize(extracted));
        }
        foreach (var (canonical, sheetKey) in spec.CanonicalKeys)
        {
            if (!_text.TryGetValue(sheetKey, out var value) || value.Length == 0)
                throw new InvalidOperationException($"Dungeon {spec.DungeonId} entry text unavailable: {sheetKey}");
            _text[canonical] = value;
        }
        foreach (var key in spec.FloorKeys.Values.Append(spec.FloorBackKey))
            if (!_text.TryGetValue(key, out var value) || value.Length == 0)
                throw new InvalidOperationException($"Dungeon {spec.DungeonId} floor text unavailable: {key}");

        var soloSheetKey = spec.CanonicalKeys["SOLO_EXPLAIN"];
        _soloExplainLines = _raw[soloSheetKey]
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize)
            .Where(l => l.Length > 0)
            .ToArray();

        var addons = Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Addon>();
        ReadSaveTitle = Normalize(addons.GetRow(10463).Text.ExtractText());
        DeleteSaveTitle = Normalize(addons.GetRow(10464).Text.ExtractText());
        NpcName = Normalize(Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ENpcResident>()
            .GetRow(dungeon.NpcDataId).Singular.ExtractText());
        foreach (var row in Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ContentFinderCondition>())
            if (dungeon.TerritoryFloorRanges.TryGetValue(row.TerritoryType.RowId, out var range) && range.endFloor > range.startFloor)
                _duties.TryAdd(range.startFloor, (row.RowId, Normalize(row.Name.ExtractText())));
        if (ReadSaveTitle.Length == 0 || DeleteSaveTitle.Length == 0 || NpcName.Length == 0)
            throw new InvalidOperationException($"Dungeon {spec.DungeonId} entry/save vocabulary is incomplete.");
    }

    public string this[string key] => _text[key];
    public bool SupportsStartFloor(int floor) => _spec.FloorKeys.ContainsKey(floor);
    public string FloorLabel(int floor) => _spec.FloorKeys.TryGetValue(floor, out var key)
        ? this[key]
        : throw new InvalidOperationException($"Unsupported new-save starting floor: {floor}");
    public bool MatchesDuty(uint id, int floor) => _duties.TryGetValue(floor, out var duty) && duty.Id == id;
    public bool MatchesDutyTitle(string title, int floor) => _duties.TryGetValue(floor, out var duty) &&
        duty.Name.Length > 0 && duty.Name == Normalize(title);
    public bool IsKnownFloorLabel(string label) =>
        _spec.FloorKeys.Values.Any(key => this[key] == label) || this[_spec.FloorBackKey] == label;
    public bool MatchesSoloExplanation(IReadOnlyList<string> strings)
    {
        if (!strings.Contains(NpcName)) return false;
        string full = this["SOLO_EXPLAIN"];
        if (strings.Contains(full)) return true;
        if (_soloExplainLines.Length < 2) return false;
        foreach (var candidate in strings)
        {
            int matched = 0;
            int searchStart = 0;
            foreach (var line in _soloExplainLines)
            {
                int idx = candidate.IndexOf(line, searchStart, StringComparison.Ordinal);
                if (idx >= 0) { matched++; searchStart = idx + line.Length; }
            }
            // Allow lines containing runtime parameters (e.g. player name in ObjectParameter) to differ.
            if (matched >= _soloExplainLines.Length - 1) return true;
        }
        return false;
    }
    public static string Normalize(string value) => string.Concat(value.Where(c => !char.IsWhiteSpace(c)));
}
