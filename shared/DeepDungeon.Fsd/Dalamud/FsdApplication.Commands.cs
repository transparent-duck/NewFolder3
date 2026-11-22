using System.Globalization;
using System.Numerics;
using System.Text.Json;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Runtime;

namespace DeepDungeon.Fsd.Dalamud;

public sealed partial class FsdApplication
{
    public void HandleCommand(string[] args)
    {
        string operation = "help";
        try
        {
            ArgumentNullException.ThrowIfNull(args);
            if (args.Length > 0)
            {
                if (string.IsNullOrWhiteSpace(args[0]))
                    throw new ArgumentException("FSD operation is empty. Use /cc fsd help.");
                operation = args[0].ToLowerInvariant();
            }
            ThrowIfDisposed();
            object result;
            switch (operation)
            {
                case "help":
                    ReadCommandOptions(args, null);
                    result = new
                    {
                        ok = true,
                        commands = new[]
                        {
                            "/cc fsd help",
                            "/cc fsd status",
                            "/cc fsd los [y=<absolute world Y> | yOffset=<offset from normal ray origin>] (current target)",
                            "/cc fsd los hover=true|false (live sight and player cast-start log)",
                            "/cc fsd start [stopAfterFloor=10..200] [startFloor=1..191] [resumeSlot=0|1] [holdOnFailure=true|false] [holdAfterClear=true|false]",
                            "/cc fsd terrain-capture output=<directory> | enabled=false",
                            "/cc fsd terrain-export",
                            "/cc fsd stop",
                            "/cc fsd preflight [startFloor=21|31] (default: 21)",
                            "/cc fsd close-entry",
                            "/cc fsd leave confirm=leave-deep-dungeon",
                            "/cc fsd delete-save slotNumber=1|2 confirm=delete-pt-save-slot",
                            "/cc fsd start-pt startFloor=21|31 confirm=start-pt-fsd [targetLoops=1..] [infinite=true|false] [leaveMode=default|finish|hoard|immediate|boss|minutes]",
                        },
                        diagnosticFloors = "stopAfterFloor must be a ten-floor boundary; startFloor must begin a ten-floor group. Existing start/save guards apply."
                    };
                    break;
                case "status":
                    ReadCommandOptions(args, null);
                    result = Snapshot();
                    break;
                case "start":
                    result = HandleStartCommand(args);
                    break;
                case "reroll-pt":
                case "map-check":
                case "map-freeze":
                    result = new { ok = false, error = "debug probe commands removed" };
                    break;
                case "stop":
                    ReadCommandOptions(args, null);
                    result = Stop();
                    break;
                case "terrain-capture":
                    result = (Map.FloorMapSource.Live ?? throw new InvalidOperationException("此構建僅使用內建底圖。"))
                        .Configure(ReadCommandOptions(args, null, "compiler", "sqpack", "output", "enabled"));
                    break;
                case "terrain-export":
                {
                    ReadCommandOptions(args, null);
                    var live = Map.FloorMapSource.Live ?? throw new InvalidOperationException("此構建僅使用內建底圖。");
                    live.UpdateExportStatus();
                    if (!live.Exporting) live.Export();
                    result = new { ok = true, status = live.ExportStatus };
                    break;
                }
                case "nav-debug":
                {
                    var options = ReadCommandOptions(args, "goto", "goto");
                    if (options.TryGetValue("goto", out var g) && g != null)
                    {
                        if (g.Equals("stop", StringComparison.OrdinalIgnoreCase))
                        {
                            Coord.NavDebug.StopGoto("stopped");
                            result = new { ok = true, navgoto = "stopped" };
                            break;
                        }
                        var parts = g.Split(',');
                        if (parts.Length != 2
                            || !float.TryParse(parts[0], out var gx)
                            || !float.TryParse(parts[1], out var gz))
                            throw new ArgumentException("goto must be x,z or stop.");
                        result = new
                        {
                            ok = true,
                            navgoto = Coord.NavDebug.StartGoto(
                                new System.Numerics.Vector3(gx, 0, gz)),
                        };
                        break;
                    }
                    result = Coord.NavDebug.Build();
                    break;
                }
                case "los":
                    result = InspectLineOfSight(ReadCommandOptions(args, "y", "y", "yOffset", "hover"));
                    break;
                case "preflight":
                {
                    var options = ReadCommandOptions(args, "startFloor", "startFloor");
                    int startFloor = ReadOptionalCommandInt(options, "startFloor", 21, 31) ?? 21;
                    RequirePilgrimsTraverseStartFloor(startFloor);
                    result = GetPilgrimsTraverseFsdPreflight(startFloor);
                    break;
                }
                case "close-entry":
                    ReadCommandOptions(args, null);
                    result = CloseDeepDungeonEntryWindowsForBridge();
                    break;
                case "leave":
                {
                    var options = ReadCommandOptions(args, null, "confirm");
                    result = StartDeepDungeonLeaveDuty(ReadRequiredCommandString(options, "confirm"));
                    break;
                }
                case "delete-save":
                {
                    var options = ReadCommandOptions(args, "slotNumber", "slotNumber", "confirm");
                    int slotNumber = ReadOptionalCommandInt(options, "slotNumber", 1, 2)
                        ?? throw new ArgumentException("slotNumber is required (1 or 2).");
                    result = StartPilgrimsTraverseDeleteSaveSlot(slotNumber,
                        ReadRequiredCommandString(options, "confirm"));
                    break;
                }
                case "start-pt":
                {
                    var options = ReadCommandOptions(args, "startFloor", "startFloor", "targetLoops", "infinite", "confirm", "leaveMode");
                    int startFloor = ReadOptionalCommandInt(options, "startFloor", 21, 31)
                        ?? throw new ArgumentException("startFloor is required (21 or 31).");
                    RequirePilgrimsTraverseStartFloor(startFloor);
                    int targetLoops = ReadOptionalCommandInt(options, "targetLoops", 1, int.MaxValue)
                        ?? Math.Max(1, _settings.NecromancerFsdLoopCount);
                    bool infinite = ReadOptionalCommandBool(options, "infinite")
                        ?? _settings.NecromancerFsdLoopInfinite;
                    options.TryGetValue("leaveMode", out string? leaveMode);
                    if (leaveMode != null && leaveMode.ToLowerInvariant() is not
                        ("default" or "finish" or "hoard" or "immediate" or "boss" or "minutes"
                         or "afterfinishdungeon" or "afterhoard" or "onbossfloorentry" or "afternminutes"))
                        throw new ArgumentException("leaveMode must be default, finish, hoard, immediate, boss, or minutes.");
                    result = StartPilgrimsTraverseFsd(startFloor, targetLoops, infinite,
                        ReadRequiredCommandString(options, "confirm"), leaveMode);
                    break;
                }
                default:
                    throw new ArgumentException($"Unknown FSD operation '{operation}'. Use /cc fsd help.");
            }

            LogCommandResult(operation, result);
        }
        catch (Exception error)
        {
            LogCommandResult(operation, new
            {
                ok = false,
                error = error.Message,
                exception = error is ArgumentException ? null : error.ToString()
            });
        }
    }

    private object HandleStartCommand(string[] args)
    {
        var options = ReadCommandOptions(args, null, "stopAfterFloor", "startFloor", "resumeSlot", "holdOnFailure", "holdAfterClear");
        int? stopAfterFloor = ReadOptionalCommandInt(options, "stopAfterFloor", 10, 200);
        if (stopAfterFloor.HasValue && stopAfterFloor.Value % 10 != 0)
            throw new ArgumentException("stopAfterFloor must be a ten-floor boundary between 10 and 200.");
        int? startFloor = ReadOptionalCommandInt(options, "startFloor", 1, 191);
        if (startFloor.HasValue && startFloor.Value % 10 != 1)
            throw new ArgumentException("startFloor must begin a ten-floor group between 1 and 191.");
        int? resumeSlot = ReadOptionalCommandInt(options, "resumeSlot", 0, 1);
        bool holdOnFailure = ReadOptionalCommandBool(options, "holdOnFailure") ?? false;
        return Start(new FsdStartRequest(stopAfterFloor, startFloor, resumeSlot, holdOnFailure)
        { HoldAfterClear = ReadOptionalCommandBool(options, "holdAfterClear") ?? false });
    }

    private static Dictionary<string, string> ReadCommandOptions(string[] args, string? positionalName,
        params string[] allowedNames)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int index = 1; index < args.Length; index++)
        {
            string argument = args[index];
            if (string.IsNullOrWhiteSpace(argument))
                throw new ArgumentException("Empty FSD command argument.");
            int separator = argument.IndexOf('=');
            string name;
            string value;
            if (separator < 0 && positionalName != null && index == 1)
            {
                name = positionalName;
                value = argument;
            }
            else
            {
                if (separator <= 0 || separator == argument.Length - 1 || argument.IndexOf('=', separator + 1) >= 0)
                    throw new ArgumentException($"Expected name=value, received '{argument}'.");
                name = argument[..separator];
                value = argument[(separator + 1)..];
            }
            if (!Array.Exists(allowedNames, allowed => string.Equals(allowed, name, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException($"Unknown FSD option '{name}'.");
            if (!options.TryAdd(name, value))
                throw new ArgumentException($"Duplicate FSD option '{name}'.");
        }
        return options;
    }

    private LosDebugOverlay? _losDebug;

    private object InspectLineOfSight(Dictionary<string, string> options)
    {
        if (ReadOptionalCommandBool(options, "hover") is { } enabled)
        {
            if (options.Count != 1) throw new ArgumentException("Use hover separately from y or yOffset.");
            if (enabled) _losDebug ??= new LosDebugOverlay();
            else _losDebug = null;
            return new { ok = true, hover = enabled };
        }
        if (options.Count > 1) throw new ArgumentException("Specify either y or yOffset.");
        var player = Service.LocalPlayer;
        var target = Service.TargetManager.Target;
        if (player == null || target == null)
            return new { ok = false, error = "A local player and a selected target are required." };

        var playerPosition = player.Position;
        var origin = CombatPositionNavigation.SightEndpoint(playerPosition);
        var end = CombatPositionNavigation.SightEndpoint(target.Position);
        bool absolute = options.TryGetValue("y", out var value);
        if (absolute || options.TryGetValue("yOffset", out value))
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float height) ||
                !float.IsFinite(height))
                throw new ArgumentException("The ray height must be a finite number.");
            origin.Y = absolute ? height : origin.Y + height;
        }
        if (!DeepDungeon.Fsd.Core.CombatPositionPolicy.IsFinite(origin) ||
            !DeepDungeon.Fsd.Core.CombatPositionPolicy.IsFinite(end))
            return new { ok = false, error = "Ray endpoints must be finite." };

        var sight = CombatPositionNavigation.SightBetween(origin, end, out var hit);
        return new
        {
            ok = sight != CombatSight.Unknown, sight = sight.ToString(), targetId = target.GameObjectId,
            playerY = playerPosition.Y, origin = Point(origin), end = Point(end),
            hit = sight == CombatSight.Blocked ? Point(hit) : null
        };

        static object Point(Vector3 p) => new { x = p.X, y = p.Y, z = p.Z };
    }

    private static int? ReadOptionalCommandInt(Dictionary<string, string> options, string name, int minimum, int maximum)
    {
        if (!options.TryGetValue(name, out string? value))
            return null;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            || number < minimum || number > maximum)
            throw new ArgumentException($"{name} must be an integer between {minimum} and {maximum}.");
        return number;
    }

    private static bool? ReadOptionalCommandBool(Dictionary<string, string> options, string name)
    {
        if (!options.TryGetValue(name, out string? value))
            return null;
        if (!bool.TryParse(value, out bool flag))
            throw new ArgumentException($"{name} must be true or false.");
        return flag;
    }

    private static string ReadRequiredCommandString(Dictionary<string, string> options, string name)
    {
        if (!options.TryGetValue(name, out string? value))
            throw new ArgumentException($"{name} is required.");
        return value;
    }

    private static void RequirePilgrimsTraverseStartFloor(int startFloor)
    {
        if (startFloor is not (21 or 31))
            throw new ArgumentException("startFloor must be 21 or 31.");
    }

    private static void LogCommandResult(string operation, object result) =>
        Service.Log.Information("[FSD.Command] operation={Operation:l} data={Result:l}", operation, JsonSerializer.Serialize(result));
}
