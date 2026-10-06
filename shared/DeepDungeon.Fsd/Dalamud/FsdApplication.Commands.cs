using System.Globalization;
using System.Text.Json;
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
                            "/cc fsd start [stopAfterFloor=10..100] [startFloor=1..91] [resumeSlot=0|1] [holdOnFailure=true|false]",
                            "/cc fsd stop",
                            "/cc fsd preflight [startFloor=21|31] (default: 21)",
                            "/cc fsd arm-survey",
                            "/cc fsd close-entry",
                            "/cc fsd leave confirm=leave-deep-dungeon",
                            "/cc fsd delete-save slotNumber=1|2 confirm=delete-pt-save-slot",
                            "/cc fsd start-pt startFloor=21|31 confirm=start-pt-fsd [targetLoops=1..] [infinite=true|false] [leaveMode=default|finish|hoard|immediate|boss|minutes]",
                            "/cc fsd start-controlled confirm=start-controlled-pt-capture [targetLoops=1..] [infinite=true|false]"
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
                case "stop":
                    ReadCommandOptions(args, null);
                    result = Stop();
                    break;
                case "preflight":
                {
                    var options = ReadCommandOptions(args, "startFloor", "startFloor");
                    int startFloor = ReadOptionalCommandInt(options, "startFloor", 21, 31) ?? 21;
                    RequirePilgrimsTraverseStartFloor(startFloor);
                    result = GetPilgrimsTraverseFsdPreflight(startFloor);
                    break;
                }
                case "arm-survey":
                    ReadCommandOptions(args, null);
                    result = ArmControlledReusableSaveSurveyCapture();
                    break;
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
                case "start-controlled":
                {
                    var options = ReadCommandOptions(args, null, "targetLoops", "infinite", "confirm");
                    int targetLoops = ReadOptionalCommandInt(options, "targetLoops", 1, int.MaxValue)
                        ?? Math.Max(1, _settings.NecromancerFsdLoopCount);
                    bool infinite = ReadOptionalCommandBool(options, "infinite")
                        ?? _settings.NecromancerFsdLoopInfinite;
                    result = StartControlledPilgrimsTraverseCapture(targetLoops, infinite,
                        ReadRequiredCommandString(options, "confirm"));
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
        var options = ReadCommandOptions(args, null, "stopAfterFloor", "startFloor", "resumeSlot", "holdOnFailure");
        int? stopAfterFloor = ReadOptionalCommandInt(options, "stopAfterFloor", 10, 100);
        if (stopAfterFloor.HasValue && stopAfterFloor.Value % 10 != 0)
            throw new ArgumentException("stopAfterFloor must be a ten-floor boundary between 10 and 100.");
        int? startFloor = ReadOptionalCommandInt(options, "startFloor", 1, 91);
        if (startFloor.HasValue && startFloor.Value % 10 != 1)
            throw new ArgumentException("startFloor must begin a ten-floor group between 1 and 91.");
        int? resumeSlot = ReadOptionalCommandInt(options, "resumeSlot", 0, 1);
        bool holdOnFailure = ReadOptionalCommandBool(options, "holdOnFailure") ?? false;
        return Start(new FsdStartRequest(stopAfterFloor, startFloor, resumeSlot, holdOnFailure));
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
        Service.Log.Information($"[FSD.Command] operation={operation} data={JsonSerializer.Serialize(result)}");
}
