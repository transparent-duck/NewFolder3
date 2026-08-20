using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Helpers;

public readonly record struct NativeDeepDungeonLogMessage(
    uint Id,
    uint Value1,
    uint Value2,
    byte ParameterCount);

public sealed unsafe class NativeDeepDungeonLogMessageSource : IDisposable
{
    private Hook<RaptureLogModule.Delegates.Update>? _updateHook;
    private readonly HashSet<nint> _seenLogMessageObjects = new();
    private bool _disposed;

    internal NativeDeepDungeonLogMessageSource(IGameInteropProvider gameInteropProvider)
    {
        ArgumentNullException.ThrowIfNull(gameInteropProvider);

        try
        {
            _updateHook = gameInteropProvider.HookFromAddress<RaptureLogModule.Delegates.Update>(
                RaptureLogModule.Addresses.Update.Value,
                UpdateDetour);

            EnableHook(_updateHook, nameof(RaptureLogModule.Delegates.Update));
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal event Action<NativeDeepDungeonLogMessage>? MessageReceived;

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        MessageReceived = null;
        DisposeHook(_updateHook, nameof(RaptureLogModule.Delegates.Update));
        _updateHook = null;
        _seenLogMessageObjects.Clear();
    }

    private void UpdateDetour(RaptureLogModule* module)
    {
        try
        {
            foreach (ref var item in module->LogMessageQueue)
            {
                var address = (LogMessageQueueItem*)Unsafe.AsPointer(ref item);
                if (_seenLogMessageObjects.Contains((nint)address))
                    continue;

                PublishQueueItem(ref item);
            }
        }
        catch (Exception ex)
        {
            LogDetourException("log message queue scan", ex);
        }

        try
        {
            _updateHook!.Original(module);
        }
        catch (Exception ex)
        {
            LogDetourException($"{nameof(RaptureLogModule.Delegates.Update)} original", ex);
        }

        try
        {
            _seenLogMessageObjects.Clear();
            foreach (ref var item in module->LogMessageQueue)
                _seenLogMessageObjects.Add((nint)Unsafe.AsPointer(ref item));
        }
        catch (Exception ex)
        {
            LogDetourException("log message queue dedup rebuild", ex);
        }
    }

    private void PublishQueueItem(ref LogMessageQueueItem item)
    {
        switch (item.LogMessageId)
        {
            case 7222:
            case 7256:
            case 7272:
            case 7273:
            case 7274:
            case 11251:
                break;
            default:
                return;
        }

        var parameterCount = item.Parameters.Count;
        var value1 = parameterCount > 0 ? unchecked((uint)item.Parameters[0].IntValue) : 0u;
        var value2 = parameterCount > 1 ? unchecked((uint)item.Parameters[1].IntValue) : 0u;
        Publish(new NativeDeepDungeonLogMessage(item.LogMessageId, value1, value2, unchecked((byte)parameterCount)));
    }

    private void Publish(NativeDeepDungeonLogMessage message)
    {
        if (_disposed)
            return;

        MessageReceived?.Invoke(message);
    }

    private void DisposeHook<T>(Hook<T>? hook, string name) where T : Delegate
    {
        if (hook == null)
            return;

        try
        {
            hook.Dispose();
        }
        catch (Exception ex)
        {
            LogDetourException($"{name} disposal", ex);
        }
    }

    private static void EnableHook<T>(Hook<T> hook, string name) where T : Delegate
    {
        hook.Enable();
        if (hook.IsEnabled)
            return;

        MinHookActivationCompatibility.EnableUnderlyingHook(hook, name);
        if (!hook.IsEnabled)
        {
            throw new InvalidOperationException(
                $"[{nameof(NativeDeepDungeonLogMessageSource)}] Hook {name} remained disabled after " +
                "the verified CN Dalamud MinHook compatibility activation.");
        }
    }

    private static class MinHookActivationCompatibility
    {
        // Verified CN Dalamud wrapper defect: MinHookHook<T>.Enable() returns while its
        // MinSharp.Hook<T> is disabled. Keep this compatibility activation narrow and explicit.
        private const string ExpectedDalamudHookTypeName = "Dalamud.Hooking.Internal.MinHookHook`1";
        private const string UnderlyingHookFieldName = "minHookImpl";

        internal static void EnableUnderlyingHook<T>(Hook<T> hook, string hookName) where T : Delegate
        {
            var hookType = hook.GetType();
            if (!string.Equals(hook.BackendName, "MinHook", StringComparison.Ordinal) ||
                !hookType.IsGenericType ||
                !string.Equals(
                    hookType.GetGenericTypeDefinition().FullName,
                    ExpectedDalamudHookTypeName,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"[{nameof(NativeDeepDungeonLogMessageSource)}] Hook {hookName} remained disabled, " +
                    "but it does not match the verified CN Dalamud MinHook wrapper shape.");
            }

            var underlyingHookField = hookType.GetField(
                UnderlyingHookFieldName,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (underlyingHookField == null ||
                !underlyingHookField.IsPrivate ||
                underlyingHookField.IsStatic)
            {
                throw new InvalidOperationException(
                    $"[{nameof(NativeDeepDungeonLogMessageSource)}] Hook {hookName} remained disabled, " +
                    $"but the verified private field '{UnderlyingHookFieldName}' was not found.");
            }

            var underlyingHook = underlyingHookField.GetValue(hook);
            var enableMethod = underlyingHook?.GetType().GetMethod(
                "Enable",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            if (enableMethod == null)
            {
                throw new InvalidOperationException(
                    $"[{nameof(NativeDeepDungeonLogMessageSource)}] Hook {hookName} remained disabled because " +
                    $"the verified field '{UnderlyingHookFieldName}' did not expose a public parameterless Enable method.");
            }

            try
            {
                enableMethod.Invoke(underlyingHook, null);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"[{nameof(NativeDeepDungeonLogMessageSource)}] Compatibility activation failed for hook {hookName}.",
                    ex);
            }
        }
    }

    private static void LogDetourException(string operation, Exception exception)
    {
        try
        {
            Service.Log.Error($"[NativeDeepDungeonLogMessageSource] {operation} failed: {exception}");
        }
        catch
        {
            // A native detour must not allow managed logging failures to escape.
        }
    }
}
