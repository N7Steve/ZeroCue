using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Nefarius.ViGEm.Client.Targets.Xbox360;
using ZeroCue.DataProbe.Models;

namespace ZeroCue.DataProbe.Services
{
    public partial class ScufDeviceService
    {
        private const long DoubleTapWindowMs = 220;
        private const long HoldThresholdMs = 450;
        private const long PulseMs = 55;
        private const ushort TriggerPressedThreshold = 30;

        private sealed class GestureState
        {
            public bool WasPressed;
            public long PressStartedAt;
            public bool PendingSingle;
            public long PendingSingleDueAt;
            public bool SimpleHoldActive;
            public bool LongTriggered;
            public bool DoubleActive;
            public string? DoubleTarget;
            public string? LatchedAction;
        }

        private readonly Dictionary<string, GestureState> _gestureStates = new Dictionary<string, GestureState>();
        private readonly HashSet<string> _activeActions = new HashSet<string>();
        private readonly HashSet<string> _previousActiveActions = new HashSet<string>();
        public event Action<string, bool>? OnActionTriggered;
        private readonly Dictionary<string, long> _pulseTargets = new Dictionary<string, long>();
        private readonly Dictionary<string, string> _profileActionSuppressedSources = new Dictionary<string, string>();
        private readonly Dictionary<string, (string GestureType, long Until)> _gestureFeedback = new Dictionary<string, (string GestureType, long Until)>();
        private readonly object _gestureFeedbackLock = new object();
        private readonly object _macroPlaybackLock = new object();
        private readonly Dictionary<string, bool> _lastMacroControllerStates = new Dictionary<string, bool>();
        private readonly HashSet<string> _heldMacroTargets = new HashSet<string>();
        private readonly Dictionary<string, CancellationTokenSource> _macroPlaybackCancellations = new Dictionary<string, CancellationTokenSource>();
        private readonly Dictionary<string, int> _macroOutputRefs = new Dictionary<string, int>();
        private readonly HashSet<string> _macroFrameTargets = new HashSet<string>();
        private long _lastXInputVerifyLogMs;
        private string _lastXInputVerifySignature = string.Empty;

        public Dictionary<string, Dictionary<string, string>> AdvancedRemapTable { get; private set; } = new Dictionary<string, Dictionary<string, string>>();
        public Dictionary<string, Dictionary<string, string>> ShiftAdvancedRemapTable { get; private set; } = new Dictionary<string, Dictionary<string, string>>();
        public Dictionary<string, Dictionary<string, int>> AdvancedGestureDelayMs { get; private set; } = new Dictionary<string, Dictionary<string, int>>();
        public Dictionary<string, Dictionary<string, int>> ShiftAdvancedGestureDelayMs { get; private set; } = new Dictionary<string, Dictionary<string, int>>();

        public void SetMacroDefinition(MacroDefinition macro)
        {
            if (string.IsNullOrWhiteSpace(macro.Id))
            {
                return;
            }

            Macros[macro.Id] = new MacroDefinition
            {
                Id = macro.Id,
                Name = string.IsNullOrWhiteSpace(macro.Name) ? "Macro" : macro.Name,
                RepeatWhileHeld = macro.RepeatWhileHeld,
                Steps = macro.Steps
                    .Where(step => !string.IsNullOrWhiteSpace(step.Target))
                    .Select(step => new MacroStep
                    {
                        InputKind = string.IsNullOrWhiteSpace(step.InputKind) ? MacroInputKinds.Keyboard : step.InputKind,
                        Target = NormalizeVirtualTarget(step.Target),
                        Action = step.Action == MacroActions.Up ? MacroActions.Up : MacroActions.Down,
                        DelayMs = Math.Clamp(step.DelayMs, 0, 60000)
                    })
                    .ToList()
            };
        }

        public MacroDefinition? GetMacroDefinition(string macroId)
        {
            return Macros.TryGetValue(macroId, out var macro) ? macro : null;
        }

        public string GetRemapTarget(string sourceName, string gestureType, bool isShiftLayer)
        {
            var canonicalSourceName = NormalizeVirtualTarget(sourceName);

            if (gestureType == RemapGestureTypes.Simple)
            {
                var table = GetSimpleRemapTable(canonicalSourceName, isShiftLayer);
                if (table.TryGetValue(sourceName, out var target))
                {
                    return target;
                }
                if (table.TryGetValue(canonicalSourceName, out target))
                {
                    return target;
                }
                return GetDefaultSimpleTarget(canonicalSourceName);
            }

            var advanced = isShiftLayer ? ShiftAdvancedRemapTable : AdvancedRemapTable;
            if (advanced.TryGetValue(sourceName, out var gestures)
                && gestures.TryGetValue(gestureType, out var advancedTarget))
            {
                return advancedTarget;
            }

            return advanced.TryGetValue(canonicalSourceName, out gestures)
                && gestures.TryGetValue(gestureType, out advancedTarget)
                    ? advancedTarget
                    : "Sin Mapeo";
        }

        public void SetRemapTarget(string sourceName, string gestureType, string target, bool isShiftLayer)
        {
            var canonicalSourceName = NormalizeVirtualTarget(sourceName);
            target = NormalizeVirtualTarget(target);

            if (gestureType == RemapGestureTypes.Simple)
            {
                var table = GetSimpleRemapTable(canonicalSourceName, isShiftLayer);
                RemoveAliasEntries(table, canonicalSourceName);
                table[canonicalSourceName] = target;
                ResetGestureState(canonicalSourceName);
                return;
            }

            var advanced = isShiftLayer ? ShiftAdvancedRemapTable : AdvancedRemapTable;
            foreach (var alias in GetSourceAliases(canonicalSourceName))
            {
                MergeAdvancedAlias(advanced, canonicalSourceName, alias);
            }

            if (!advanced.TryGetValue(canonicalSourceName, out var gestures))
            {
                gestures = new Dictionary<string, string>();
                advanced[canonicalSourceName] = gestures;
            }

            gestures[gestureType] = target;
            CleanupAdvancedMap(advanced, canonicalSourceName);
            ResetGestureState(canonicalSourceName);
        }

        public int GetRemapGestureDelayMs(string sourceName, string gestureType, bool isShiftLayer)
        {
            var canonicalSourceName = NormalizeVirtualTarget(sourceName);
            var delays = isShiftLayer ? ShiftAdvancedGestureDelayMs : AdvancedGestureDelayMs;
            if (delays.TryGetValue(sourceName, out var sourceDelays)
                && sourceDelays.TryGetValue(gestureType, out var delay))
            {
                return ClampGestureDelayMs(gestureType, delay);
            }

            if (delays.TryGetValue(canonicalSourceName, out sourceDelays)
                && sourceDelays.TryGetValue(gestureType, out delay))
            {
                return ClampGestureDelayMs(gestureType, delay);
            }

            return GetDefaultGestureDelayMs(gestureType);
        }

        public void SetRemapGestureDelayMs(string sourceName, string gestureType, int delayMs, bool isShiftLayer)
        {
            if (!IsConfigurableGestureDelay(gestureType))
            {
                return;
            }

            var canonicalSourceName = NormalizeVirtualTarget(sourceName);
            var delays = isShiftLayer ? ShiftAdvancedGestureDelayMs : AdvancedGestureDelayMs;
            foreach (var alias in GetSourceAliases(canonicalSourceName))
            {
                MergeGestureDelayAlias(delays, canonicalSourceName, alias);
            }

            if (!delays.TryGetValue(canonicalSourceName, out var sourceDelays))
            {
                sourceDelays = new Dictionary<string, int>();
                delays[canonicalSourceName] = sourceDelays;
            }

            sourceDelays[gestureType] = ClampGestureDelayMs(gestureType, delayMs);
            ResetGestureState(canonicalSourceName);
        }

        public void RemoveRemapTarget(string sourceName, string gestureType, bool isShiftLayer)
        {
            var canonicalSourceName = NormalizeVirtualTarget(sourceName);

            if (gestureType == RemapGestureTypes.Simple)
            {
                var table = GetSimpleRemapTable(canonicalSourceName, isShiftLayer);
                RemoveAliasEntries(table, canonicalSourceName);
                table[canonicalSourceName] = GetDefaultSimpleTarget(canonicalSourceName);

                ResetGestureState(canonicalSourceName);
                return;
            }

            var advanced = isShiftLayer ? ShiftAdvancedRemapTable : AdvancedRemapTable;
            if (advanced.TryGetValue(sourceName, out var gestures))
            {
                gestures.Remove(gestureType);
                CleanupAdvancedMap(advanced, sourceName);
            }
            if (sourceName != canonicalSourceName && advanced.TryGetValue(canonicalSourceName, out gestures))
            {
                gestures.Remove(gestureType);
                CleanupAdvancedMap(advanced, canonicalSourceName);
            }
            foreach (var alias in GetSourceAliases(canonicalSourceName))
            {
                if (advanced.TryGetValue(alias, out gestures))
                {
                    gestures.Remove(gestureType);
                    CleanupAdvancedMap(advanced, alias);
                }
            }

            RemoveGestureDelay(sourceName, gestureType, isShiftLayer);
            ResetGestureState(canonicalSourceName);
        }

        public bool HasAdvancedRemap(string sourceName, string gestureType, bool isShiftLayer)
        {
            var canonicalSourceName = NormalizeVirtualTarget(sourceName);
            var advanced = isShiftLayer ? ShiftAdvancedRemapTable : AdvancedRemapTable;
            return HasAdvancedRemapForSource(advanced, sourceName, gestureType)
                || HasAdvancedRemapForSource(advanced, canonicalSourceName, gestureType);
        }

        private static bool HasAdvancedRemapForSource(
            Dictionary<string, Dictionary<string, string>> advanced,
            string sourceName,
            string gestureType)
        {
            return advanced.TryGetValue(sourceName, out var gestures)
                && gestures.TryGetValue(gestureType, out var target)
                && IsUsableTarget(target);
        }

        public IReadOnlyList<string> GetConfiguredAdvancedGestures(string sourceName, bool isShiftLayer)
        {
            var canonicalSourceName = NormalizeVirtualTarget(sourceName);
            var advanced = isShiftLayer ? ShiftAdvancedRemapTable : AdvancedRemapTable;
            if (!advanced.TryGetValue(sourceName, out var gestures)
                && !advanced.TryGetValue(canonicalSourceName, out gestures))
            {
                return Array.Empty<string>();
            }

            return RemapGestureTypes.AdvancedTypes
                .Where(type => gestures.TryGetValue(type, out var target) && IsUsableTarget(target))
                .ToArray();
        }

        public int GetAdvancedRemapCount(string sourceName, bool isShiftLayer)
        {
            return GetConfiguredAdvancedGestures(sourceName, isShiftLayer).Count;
        }

        public IReadOnlyDictionary<string, string> GetActiveGestureFeedback()
        {
            var now = Environment.TickCount64;
            lock (_gestureFeedbackLock)
            {
                foreach (var key in _gestureFeedback.Keys.ToArray())
                {
                    if (now > _gestureFeedback[key].Until)
                    {
                        _gestureFeedback.Remove(key);
                    }
                }

                return _gestureFeedback.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.GestureType);
            }
        }

        private void ObserveMacroControllerStates(IEnumerable<(string Target, bool IsPressed)> states)
        {
            foreach (var (target, isPressed) in states)
            {
                if (string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                if (_lastMacroControllerStates.TryGetValue(target, out var wasPressed) && wasPressed == isPressed)
                {
                    continue;
                }

                _lastMacroControllerStates[target] = isPressed;
                OnMacroControllerInput?.Invoke(new MacroInputEvent
                {
                    InputKind = MacroInputKinds.Gamepad,
                    Target = NormalizeVirtualTarget(target),
                    Action = isPressed ? MacroActions.Down : MacroActions.Up
                });
            }
        }

        private void ProcessMacroTargets(HashSet<string> frameTargets)
        {
            var currentMacroTargets = frameTargets
                .Where(MacroTarget.IsMacroTarget)
                .ToArray();

            foreach (var macroTarget in currentMacroTargets)
            {
                frameTargets.Remove(macroTarget);
            }

            lock (_macroPlaybackLock)
            {
                foreach (var releasedTarget in _heldMacroTargets.Where(target => !currentMacroTargets.Contains(target)).ToArray())
                {
                    _heldMacroTargets.Remove(releasedTarget);
                    if (TryGetMacroFromTarget(releasedTarget, out var releasedMacro) && releasedMacro.RepeatWhileHeld)
                    {
                        CancelMacroPlayback(releasedTarget);
                    }
                }

                foreach (var macroTarget in currentMacroTargets)
                {
                    if (_heldMacroTargets.Add(macroTarget))
                    {
                        StartMacroPlayback(macroTarget);
                    }
                }

                foreach (var target in _macroFrameTargets)
                {
                    frameTargets.Add(target);
                }
            }
        }

        private bool TryGetMacroFromTarget(string macroTarget, out MacroDefinition macro)
        {
            macro = null!;
            var macroId = MacroTarget.GetId(macroTarget);
            return !string.IsNullOrWhiteSpace(macroId) && Macros.TryGetValue(macroId, out macro!);
        }

        private void StartMacroPlayback(string macroTarget)
        {
            if (!TryGetMacroFromTarget(macroTarget, out var macro) || macro.Steps.Count == 0)
            {
                return;
            }

            if (_macroPlaybackCancellations.ContainsKey(macroTarget))
            {
                return;
            }

            var cts = new CancellationTokenSource();
            _macroPlaybackCancellations[macroTarget] = cts;
            _ = Task.Run(() => PlayMacroAsync(macroTarget, macro, cts.Token));
        }

        private void CancelMacroPlayback(string macroTarget)
        {
            if (_macroPlaybackCancellations.TryGetValue(macroTarget, out var cts))
            {
                cts.Cancel();
            }
        }

        private async Task PlayMacroAsync(string macroTarget, MacroDefinition macro, CancellationToken token)
        {
            var ownedOutputs = new HashSet<string>();

            try
            {
                do
                {
                    foreach (var step in macro.Steps)
                    {
                        token.ThrowIfCancellationRequested();
                        var target = ResolveMacroPlaybackTarget(step.Target);
                        if (!IsUsableTarget(target))
                        {
                            continue;
                        }

                        var isDown = step.Action != MacroActions.Up;
                        SetMacroOutput(target, isDown);
                        if (isDown)
                        {
                            ownedOutputs.Add(target);
                        }
                        else
                        {
                            ownedOutputs.Remove(target);
                        }

                        var delay = Math.Clamp(step.DelayMs, 0, 60000);
                        if (delay > 0)
                        {
                            await Task.Delay(delay, token);
                        }
                    }
                }
                while (macro.RepeatWhileHeld && IsMacroTriggerHeld(macroTarget, token));
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                foreach (var target in ownedOutputs.ToArray())
                {
                    SetMacroOutput(target, false);
                }

                lock (_macroPlaybackLock)
                {
                    if (_macroPlaybackCancellations.TryGetValue(macroTarget, out var cts))
                    {
                        _macroPlaybackCancellations.Remove(macroTarget);
                        cts.Dispose();
                    }
                }
            }
        }

        private bool IsMacroTriggerHeld(string macroTarget, CancellationToken token)
        {
            if (token.IsCancellationRequested)
            {
                return false;
            }

            lock (_macroPlaybackLock)
            {
                return _heldMacroTargets.Contains(macroTarget);
            }
        }

        private string ResolveMacroPlaybackTarget(string target)
        {
            target = NormalizeVirtualTarget(target);
            if (IsXboxButton(target) || !IsMacroPhysicalSource(target))
            {
                return target;
            }

            Dictionary<string, string> table;
            if (target.StartsWith("Paddle_") || target.StartsWith("SAX_"))
            {
                table = IsShiftHeld ? ShiftPaddleRemapTable : PaddleRemapTable;
            }
            else
            {
                table = IsShiftHeld ? ShiftGKeyRemapTable : GKeyRemapTable;
            }

            if (!table.TryGetValue(target, out var mappedTarget) || MacroTarget.IsMacroTarget(mappedTarget))
            {
                return "Sin Mapeo";
            }

            return NormalizeVirtualTarget(mappedTarget);
        }

        private static bool IsMacroPhysicalSource(string target)
        {
            return target.StartsWith("Paddle_")
                || target.StartsWith("SAX_")
                || target is "G1" or "G2" or "G3" or "G4" or "G5";
        }

        private void SetMacroOutput(string target, bool isDown)
        {
            lock (_macroPlaybackLock)
            {
                if (isDown)
                {
                    _macroOutputRefs.TryGetValue(target, out var count);
                    _macroOutputRefs[target] = count + 1;
                    _macroFrameTargets.Add(target);
                    return;
                }

                if (!_macroOutputRefs.TryGetValue(target, out var currentCount))
                {
                    return;
                }

                currentCount--;
                if (currentCount <= 0)
                {
                    _macroOutputRefs.Remove(target);
                    _macroFrameTargets.Remove(target);
                }
                else
                {
                    _macroOutputRefs[target] = currentCount;
                }
            }
        }

        private Dictionary<string, string> GetSimpleRemapTable(string sourceName, bool isShiftLayer)
        {
            if (sourceName.StartsWith("Paddle_") || sourceName.StartsWith("SAX_"))
            {
                return isShiftLayer ? ShiftPaddleRemapTable : PaddleRemapTable;
            }

            if (sourceName.StartsWith("G"))
            {
                return isShiftLayer ? ShiftGKeyRemapTable : GKeyRemapTable;
            }

            return isShiftLayer ? ShiftButtonRemapTable : ButtonRemapTable;
        }

        private static string GetDefaultSimpleTarget(string sourceName)
        {
            return sourceName;
        }

        private static IEnumerable<string> GetSourceAliases(string canonicalSourceName)
        {
            return canonicalSourceName switch
            {
                "LeftShoulder" => new[] { "LB" },
                "RightShoulder" => new[] { "RB" },
                "LeftTrigger" => new[] { "LT" },
                "RightTrigger" => new[] { "RT" },
                "LeftThumb" => new[] { "L3" },
                "RightThumb" => new[] { "R3" },
                "Back" => new[] { "View" },
                "Start" => new[] { "Menu" },
                _ => Array.Empty<string>()
            };
        }

        private static void RemoveAliasEntries(Dictionary<string, string> table, string canonicalSourceName)
        {
            foreach (var alias in GetSourceAliases(canonicalSourceName))
            {
                table.Remove(alias);
            }
        }

        private static void MergeAdvancedAlias(
            Dictionary<string, Dictionary<string, string>> advanced,
            string canonicalSourceName,
            string alias)
        {
            if (!advanced.Remove(alias, out var aliasGestures))
            {
                return;
            }

            if (!advanced.TryGetValue(canonicalSourceName, out var existingGestures))
            {
                advanced[canonicalSourceName] = aliasGestures;
                return;
            }

            foreach (var kvp in aliasGestures)
            {
                existingGestures[kvp.Key] = kvp.Value;
            }
        }

        private static void MergeGestureDelayAlias(
            Dictionary<string, Dictionary<string, int>> delays,
            string canonicalSourceName,
            string alias)
        {
            if (!delays.Remove(alias, out var aliasDelays))
            {
                return;
            }

            if (!delays.TryGetValue(canonicalSourceName, out var existingDelays))
            {
                delays[canonicalSourceName] = aliasDelays;
                return;
            }

            foreach (var kvp in aliasDelays)
            {
                existingDelays[kvp.Key] = kvp.Value;
            }
        }

        private void RemoveGestureDelay(string sourceName, string gestureType, bool isShiftLayer)
        {
            var canonicalSourceName = NormalizeVirtualTarget(sourceName);
            var delays = isShiftLayer ? ShiftAdvancedGestureDelayMs : AdvancedGestureDelayMs;
            RemoveGestureDelayForSource(delays, sourceName, gestureType);
            RemoveGestureDelayForSource(delays, canonicalSourceName, gestureType);
            foreach (var alias in GetSourceAliases(canonicalSourceName))
            {
                RemoveGestureDelayForSource(delays, alias, gestureType);
            }
        }

        private static void RemoveGestureDelayForSource(Dictionary<string, Dictionary<string, int>> delays, string sourceName, string gestureType)
        {
            if (!delays.TryGetValue(sourceName, out var sourceDelays))
            {
                return;
            }

            sourceDelays.Remove(gestureType);
            if (sourceDelays.Count == 0)
            {
                delays.Remove(sourceName);
            }
        }

        private static bool IsConfigurableGestureDelay(string gestureType)
        {
            return gestureType == RemapGestureTypes.DoubleTap || gestureType == RemapGestureTypes.Hold;
        }

        private static int GetDefaultGestureDelayMs(string gestureType)
        {
            return gestureType == RemapGestureTypes.Hold ? (int)HoldThresholdMs : (int)DoubleTapWindowMs;
        }

        private static int ClampGestureDelayMs(string gestureType, int delayMs)
        {
            return gestureType == RemapGestureTypes.Hold
                ? Math.Clamp(delayMs, 100, 3000)
                : Math.Clamp(delayMs, 80, 1000);
        }

        private static void CleanupAdvancedMap(Dictionary<string, Dictionary<string, string>> advanced, string sourceName)
        {
            if (!advanced.TryGetValue(sourceName, out var gestures))
            {
                return;
            }

            foreach (var key in gestures.Keys.ToArray())
            {
                if (!IsUsableTarget(gestures[key]))
                {
                    gestures.Remove(key);
                }
            }

            if (gestures.Count == 0)
            {
                advanced.Remove(sourceName);
            }
        }

        private void ResetGestureState(string sourceName)
        {
            _gestureStates.Remove(GetGestureStateKey(sourceName, false));
            _gestureStates.Remove(GetGestureStateKey(sourceName, true));
            foreach (var target in _previousFrameTargets.Where(t => !IsXboxButton(t)).ToArray())
            {
                KeyboardSimulator.KeyUp(target);
            }
        }

        private static string GetGestureStateKey(string sourceName, bool isShiftLayer)
        {
            return $"{(isShiftLayer ? "Shift" : "Standard")}:{sourceName}";
        }

        private static string GetProfileActionSourceKey(string sourceName)
        {
            return NormalizeVirtualTarget(sourceName);
        }

        private static bool IsProfileActionTarget(string? target)
        {
            return target != null && target.StartsWith("Action:LoadProfile", StringComparison.Ordinal);
        }

        private static bool IsActionTarget(string? target)
        {
            return target != null && target.StartsWith("Action:", StringComparison.Ordinal);
        }

        private static bool IsHeldProfileActionTarget(string? target)
        {
            return target != null && target.StartsWith("Action:LoadProfileHeld:", StringComparison.Ordinal);
        }

        private void SuppressSourceForProfileAction(string sourceName, string? actionTarget)
        {
            if (!IsProfileActionTarget(actionTarget))
            {
                return;
            }

            _profileActionSuppressedSources[GetProfileActionSourceKey(sourceName)] = actionTarget!;
        }

        private bool TryGetSuppressedProfileAction(string sourceName, out string actionTarget)
        {
            return _profileActionSuppressedSources.TryGetValue(GetProfileActionSourceKey(sourceName), out actionTarget!);
        }

        private void ReleaseSuppressedProfileActionSource(string sourceName)
        {
            _profileActionSuppressedSources.Remove(GetProfileActionSourceKey(sourceName));
        }

        private bool IsShiftModifierSource(string sourceName)
        {
            return !string.IsNullOrWhiteSpace(ShiftModifierButton) && sourceName == ShiftModifierButton;
        }

        private void UpdateShiftHeld(bool isShiftHeld)
        {
            if (IsShiftHeld == isShiftHeld)
            {
                return;
            }

            if (IsShiftHeld && !isShiftHeld)
            {
                _pulseTargets.Clear();
            }

            IsShiftHeld = isShiftHeld;
        }

        private static void SuppressGestureState(GestureState state, bool isPressed)
        {
            state.WasPressed = isPressed;
            state.PendingSingle = false;
            state.SimpleHoldActive = false;
            state.LongTriggered = false;
            state.DoubleActive = false;
            state.DoubleTarget = null;
        }

        private static bool IsUsableTarget(string? target)
        {
            return !string.IsNullOrWhiteSpace(target) && target != "Sin Mapeo" && target != "Shift";
        }

        private static string NormalizeVirtualTarget(string target)
        {
            return target switch
            {
                "LB" => "LeftShoulder",
                "RB" => "RightShoulder",
                "LT" => "LeftTrigger",
                "RT" => "RightTrigger",
                "L3" => "LeftThumb",
                "R3" => "RightThumb",
                "View" => "Back",
                "Menu" => "Start",
                _ => target
            };
        }

        private static string ResolveSimpleButtonTarget(
            Dictionary<string, string> buttonTable,
            string sourceName,
            string canonicalSourceName)
        {
            if (buttonTable.TryGetValue(sourceName, out var target))
            {
                return target;
            }

            if (buttonTable.TryGetValue(canonicalSourceName, out target))
            {
                return target;
            }

            return sourceName;
        }

        private static bool IsTriggerTarget(string target)
        {
            var normalizedTarget = NormalizeVirtualTarget(target);
            return normalizedTarget == "LeftTrigger" || normalizedTarget == "RightTrigger";
        }

        private static bool HasUsableAdvancedRemap(
            Dictionary<string, Dictionary<string, string>> advancedMap,
            string sourceName,
            string canonicalSourceName)
        {
            return HasUsableAdvancedRemapForSource(advancedMap, sourceName)
                   || HasUsableAdvancedRemapForSource(advancedMap, canonicalSourceName);
        }

        private static bool HasUsableAdvancedRemapForSource(
            Dictionary<string, Dictionary<string, string>> advancedMap,
            string sourceName)
        {
            return advancedMap.TryGetValue(sourceName, out var gestures)
                   && gestures.Values.Any(IsUsableTarget);
        }

        private void CollectTriggerGestureTargets(
            ushort leftRawValue,
            ushort rightRawValue,
            byte leftAnalogOutput,
            byte rightAnalogOutput,
            Dictionary<string, string> buttonTable,
            Dictionary<string, Dictionary<string, string>> advancedMap,
            HashSet<string> frameTargets,
            long now,
            bool isShiftLayer,
            out byte leftTriggerOutput,
            out byte rightTriggerOutput)
        {
            leftTriggerOutput = 0;
            rightTriggerOutput = 0;
            CollectSingleTriggerGestureTarget(
                "LT",
                "LeftTrigger",
                leftRawValue,
                leftAnalogOutput,
                buttonTable,
                advancedMap,
                frameTargets,
                now,
                isShiftLayer,
                ref leftTriggerOutput,
                ref rightTriggerOutput);
            CollectSingleTriggerGestureTarget(
                "RT",
                "RightTrigger",
                rightRawValue,
                rightAnalogOutput,
                buttonTable,
                advancedMap,
                frameTargets,
                now,
                isShiftLayer,
                ref leftTriggerOutput,
                ref rightTriggerOutput);
        }

        private void CollectSingleTriggerGestureTarget(
            string sourceName,
            string canonicalSourceName,
            ushort rawValue,
            byte analogOutput,
            Dictionary<string, string> buttonTable,
            Dictionary<string, Dictionary<string, string>> advancedMap,
            HashSet<string> frameTargets,
            long now,
            bool isShiftLayer,
            ref byte leftTriggerOutput,
            ref byte rightTriggerOutput)
        {
            var simpleTarget = ResolveSimpleButtonTarget(buttonTable, sourceName, canonicalSourceName);
            var hasAdvanced = HasUsableAdvancedRemap(advancedMap, sourceName, canonicalSourceName);
            if (IsTriggerTarget(simpleTarget) && !hasAdvanced)
            {
                var normalizedTarget = NormalizeVirtualTarget(simpleTarget);
                if (normalizedTarget == "LeftTrigger")
                {
                    leftTriggerOutput = leftTriggerOutput > analogOutput ? leftTriggerOutput : analogOutput;
                }
                else
                {
                    rightTriggerOutput = rightTriggerOutput > analogOutput ? rightTriggerOutput : analogOutput;
                }
                return;
            }

            var gestureSourceName = advancedMap.ContainsKey(sourceName) || buttonTable.ContainsKey(sourceName)
                ? sourceName
                : canonicalSourceName;
            CollectGestureTarget(
                gestureSourceName,
                rawValue > TriggerPressedThreshold,
                simpleTarget,
                advancedMap,
                frameTargets,
                now,
                isShiftLayer);
        }

        private void CollectGestureTarget(
            string sourceName,
            bool isPressed,
            string simpleTarget,
            Dictionary<string, Dictionary<string, string>> advancedMap,
            HashSet<string> frameTargets,
            long now,
            bool isShiftLayer)
        {
            var stateKey = GetGestureStateKey(sourceName, isShiftLayer);
            if (!_gestureStates.TryGetValue(stateKey, out var state))
            {
                state = new GestureState();
                _gestureStates[stateKey] = state;
            }

            if (IsShiftModifierSource(sourceName))
            {
                SuppressGestureState(state, isPressed);
                return;
            }

            if (TryGetSuppressedProfileAction(sourceName, out var suppressedProfileAction))
            {
                if (isPressed)
                {
                    AddTarget(frameTargets, suppressedProfileAction);
                }
                else
                {
                    ReleaseSuppressedProfileActionSource(sourceName);
                    state.PendingSingle = false;
                    state.SimpleHoldActive = false;
                    state.LongTriggered = false;
                    state.DoubleActive = false;
                    state.DoubleTarget = null;
                    state.LatchedAction = null;
                }

                state.WasPressed = isPressed;
                return;
            }

            advancedMap.TryGetValue(sourceName, out var gestures);
            string? doubleTarget = GetGestureTarget(gestures, RemapGestureTypes.DoubleTap);
            string? holdTarget = GetGestureTarget(gestures, RemapGestureTypes.Hold);
            string? pressStartTarget = GetGestureTarget(gestures, RemapGestureTypes.PressStart);
            string? pressReleaseTarget = GetGestureTarget(gestures, RemapGestureTypes.PressRelease);
            if (IsHeldProfileActionTarget(simpleTarget))
            {
                holdTarget = null;
                pressReleaseTarget = null;
            }

            if (IsActionTarget(pressStartTarget))
            {
                pressStartTarget = null;
            }

            if (IsActionTarget(pressReleaseTarget))
            {
                pressReleaseTarget = null;
            }

            bool hasDouble = IsUsableTarget(doubleTarget);
            bool hasHold = IsUsableTarget(holdTarget);
            bool hasSpecialTiming = hasDouble || hasHold;
            var doubleTapWindowMs = GetRemapGestureDelayMs(sourceName, RemapGestureTypes.DoubleTap, isShiftLayer);
            var holdThresholdMs = GetRemapGestureDelayMs(sourceName, RemapGestureTypes.Hold, isShiftLayer);

            if (isPressed && !state.WasPressed)
            {
                state.PressStartedAt = now;
                state.LongTriggered = false;
                state.SimpleHoldActive = false;

                if (IsUsableTarget(pressStartTarget))
                {
                    PulseTarget(pressStartTarget!, now, sourceName, isPressed);
                    MarkGestureFeedback(sourceName, RemapGestureTypes.PressStart, now);
                }

                if (state.PendingSingle && hasDouble && now <= state.PendingSingleDueAt)
                {
                    state.PendingSingle = false;
                    state.DoubleActive = true;
                    state.DoubleTarget = doubleTarget;
                }
                else
                {
                    state.PendingSingle = hasDouble;
                    state.PendingSingleDueAt = now + doubleTapWindowMs;
                    state.DoubleActive = false;
                    state.DoubleTarget = null;
                }
            }

            if (!isPressed && state.WasPressed)
            {
                if (IsUsableTarget(pressReleaseTarget))
                {
                    if (!IsHeldProfileActionTarget(pressReleaseTarget))
                    {
                        PulseTarget(pressReleaseTarget!, now, sourceName, isPressed);
                        MarkGestureFeedback(sourceName, RemapGestureTypes.PressRelease, now);
                    }
                }

                if (state.DoubleActive)
                {
                    state.DoubleActive = false;
                    state.DoubleTarget = null;
                }
                else if (state.LongTriggered)
                {
                    state.LongTriggered = false;
                }
                else if (hasDouble)
                {
                    state.PendingSingle = true;
                    state.PendingSingleDueAt = Math.Max(state.PendingSingleDueAt, now + doubleTapWindowMs);
                }
                else if (hasHold)
                {
                    PulseTarget(simpleTarget, now, sourceName, isPressed);
                    MarkGestureFeedback(sourceName, RemapGestureTypes.Simple, now);
                }

                state.SimpleHoldActive = false;
            }

            if (state.PendingSingle && now >= state.PendingSingleDueAt)
            {
                state.PendingSingle = false;
                if (isPressed && !hasHold)
                {
                    state.SimpleHoldActive = true;
                }
                else if (!isPressed)
                {
                    PulseTarget(simpleTarget, now, sourceName, isPressed);
                    MarkGestureFeedback(sourceName, RemapGestureTypes.Simple, now);
                }
            }

            if (isPressed && hasHold && !state.LongTriggered && now - state.PressStartedAt >= holdThresholdMs)
            {
                state.PendingSingle = false;
                state.SimpleHoldActive = false;
                state.DoubleActive = false;
                state.LongTriggered = true;
            }

            if (isPressed)
            {
                if (TryGetSuppressedProfileAction(sourceName, out suppressedProfileAction))
                {
                    AddTarget(frameTargets, suppressedProfileAction);
                }
                else if (state.LatchedAction != null)
                {
                    AddTarget(frameTargets, state.LatchedAction);
                }
                else
                {
                    string? newlyAddedTarget = null;
                    if (state.DoubleActive && IsUsableTarget(state.DoubleTarget))
                    {
                        AddTarget(frameTargets, state.DoubleTarget);
                        newlyAddedTarget = state.DoubleTarget;
                        MarkGestureFeedback(sourceName, RemapGestureTypes.DoubleTap, now);
                    }
                    else if (state.LongTriggered && IsUsableTarget(holdTarget))
                    {
                        AddTarget(frameTargets, holdTarget);
                        newlyAddedTarget = holdTarget;
                        MarkGestureFeedback(sourceName, RemapGestureTypes.Hold, now);
                    }
                    else if (!hasSpecialTiming || state.SimpleHoldActive)
                    {
                        AddTarget(frameTargets, simpleTarget);
                        newlyAddedTarget = simpleTarget;
                        MarkGestureFeedback(sourceName, RemapGestureTypes.Simple, now);
                    }

                    if (newlyAddedTarget != null && newlyAddedTarget.StartsWith("Action:"))
                    {
                        state.LatchedAction = newlyAddedTarget;
                        SuppressSourceForProfileAction(sourceName, newlyAddedTarget);
                    }
                }
            }
            else
            {
                state.LatchedAction = null;
            }

            state.WasPressed = isPressed;
        }

        private static string? GetGestureTarget(Dictionary<string, string>? gestures, string gestureType)
        {
            return gestures != null && gestures.TryGetValue(gestureType, out var target) ? target : null;
        }

        private void MarkGestureFeedback(string sourceName, string gestureType, long now)
        {
            lock (_gestureFeedbackLock)
            {
                _gestureFeedback[sourceName] = (gestureType, now + Math.Max(PulseMs, 120));
            }
        }

        private void PulseTarget(string? target, long now, string? sourceName = null, bool sourceIsPressed = false)
        {
            if (!IsUsableTarget(target))
            {
                return;
            }

            if (sourceIsPressed && sourceName != null)
            {
                SuppressSourceForProfileAction(sourceName, target);
            }

            if (MacroTarget.IsMacroTarget(target))
            {
                lock (_macroPlaybackLock)
                {
                    StartMacroPlayback(target!);
                }
                return;
            }

            if (target == "ScrollUp" || target == "ScrollDown")
            {
                KeyboardSimulator.Pulse(target);
                return;
            }

            _pulseTargets[target!] = now + PulseMs;
        }

        private void AddActivePulseTargets(HashSet<string> frameTargets, long now)
        {
            foreach (var kvp in _pulseTargets.ToArray())
            {
                if (now <= kvp.Value)
                {
                    AddTarget(frameTargets, kvp.Key);
                }
                else
                {
                    _pulseTargets.Remove(kvp.Key);
                }
            }
        }

        private void ProcessActionTargets(HashSet<string> frameTargets)
        {
            _activeActions.Clear();
            foreach (var target in frameTargets)
            {
                if (target != null && target.StartsWith("Action:"))
                {
                    _activeActions.Add(target);
                }
            }

            foreach (var action in _activeActions)
            {
                if (!_previousActiveActions.Contains(action))
                {
                    OnActionTriggered?.Invoke(action, true);
                }
            }

            foreach (var action in _previousActiveActions)
            {
                if (!_activeActions.Contains(action))
                {
                    OnActionTriggered?.Invoke(action, false);
                }
            }

            frameTargets.RemoveWhere(t => t != null && t.StartsWith("Action:"));

            _previousActiveActions.Clear();
            foreach (var action in _activeActions)
            {
                _previousActiveActions.Add(action);
            }
        }

        private static void AddTarget(HashSet<string> frameTargets, string? target)
        {
            if (IsUsableTarget(target))
            {
                frameTargets.Add(NormalizeVirtualTarget(target!));
            }
        }

        private void SubmitVirtualOutput(
            byte leftTrigger,
            byte rightTrigger,
            short leftStickX,
            short leftStickY,
            short rightStickX,
            short rightStickY)
        {
            if (_xbox == null)
            {
                return;
            }

            var leftStickOut = ApplyStickOutput(leftStickX, leftStickY);
            var rightStickOut = ApplyStickOutput(rightStickX, rightStickY);
            ApplyVirtualStickTargets(ref leftStickOut, ref rightStickOut);
            _xbox.SetAxisValue(Xbox360Axis.LeftThumbX, leftStickOut.X);
            _xbox.SetAxisValue(Xbox360Axis.LeftThumbY, leftStickOut.Y);
            _xbox.SetAxisValue(Xbox360Axis.RightThumbX, rightStickOut.X);
            _xbox.SetAxisValue(Xbox360Axis.RightThumbY, rightStickOut.Y);
            var leftTriggerOut = _frameTargets.Contains("LeftTrigger") ? byte.MaxValue : leftTrigger;
            var rightTriggerOut = _frameTargets.Contains("RightTrigger") ? byte.MaxValue : rightTrigger;
            var xinputButtons = BuildExpectedXInputButtons();
            var vigemButtons = BuildExpectedVigemButtons(xinputButtons);

            _xbox.SetSliderValue(Xbox360Slider.LeftTrigger, leftTriggerOut);
            _xbox.SetSliderValue(Xbox360Slider.RightTrigger, rightTriggerOut);
            _xbox.SetButtonState(Xbox360Button.A, _frameTargets.Contains("A"));
            _xbox.SetButtonState(Xbox360Button.B, _frameTargets.Contains("B"));
            _xbox.SetButtonState(Xbox360Button.X, _frameTargets.Contains("X"));
            _xbox.SetButtonState(Xbox360Button.Y, _frameTargets.Contains("Y"));
            _xbox.SetButtonState(Xbox360Button.LeftShoulder, _frameTargets.Contains("LeftShoulder"));
            _xbox.SetButtonState(Xbox360Button.RightShoulder, _frameTargets.Contains("RightShoulder"));
            _xbox.SetButtonState(Xbox360Button.Back, _frameTargets.Contains("Back"));
            _xbox.SetButtonState(Xbox360Button.Start, _frameTargets.Contains("Start"));
            _xbox.SetButtonState(Xbox360Button.Up, _frameTargets.Contains("Up"));
            _xbox.SetButtonState(Xbox360Button.Down, _frameTargets.Contains("Down"));
            _xbox.SetButtonState(Xbox360Button.Left, _frameTargets.Contains("Left"));
            _xbox.SetButtonState(Xbox360Button.Right, _frameTargets.Contains("Right"));
            _xbox.SetButtonState(Xbox360Button.LeftThumb, _frameTargets.Contains("LeftThumb"));
            _xbox.SetButtonState(Xbox360Button.RightThumb, _frameTargets.Contains("RightThumb"));
            _xbox.SetButtonState(Xbox360Button.Guide, _frameTargets.Contains("Guide"));
            _xbox.SubmitReport();
            VerifyXInputOutput(xinputButtons, vigemButtons, leftTriggerOut, rightTriggerOut);

            foreach (var target in _frameTargets)
            {
                if (!_previousFrameTargets.Contains(target) && !IsXboxButton(target))
                {
                    KeyboardSimulator.KeyDown(target);
                }
            }

            foreach (var oldTarget in _previousFrameTargets)
            {
                if (!_frameTargets.Contains(oldTarget) && !IsXboxButton(oldTarget))
                {
                    KeyboardSimulator.KeyUp(oldTarget);
                }
            }

            _previousFrameTargets.Clear();
            foreach (var target in _frameTargets)
            {
                _previousFrameTargets.Add(target);
            }

            LogVigemChanges(
                _frameTargets.Contains("A"),
                _frameTargets.Contains("B"),
                _frameTargets.Contains("X"),
                _frameTargets.Contains("Y"),
                _frameTargets.Contains("LeftShoulder"),
                _frameTargets.Contains("RightShoulder"),
                _frameTargets.Contains("Back"),
                _frameTargets.Contains("Start"),
                _frameTargets.Contains("Up"),
                _frameTargets.Contains("Down"),
                _frameTargets.Contains("Left"),
                _frameTargets.Contains("Right"),
                _frameTargets.Contains("LeftThumb"),
                _frameTargets.Contains("RightThumb"),
                _frameTargets.Contains("Guide"),
                leftTriggerOut,
                rightTriggerOut,
                leftStickX,
                leftStickY,
                rightStickX,
                rightStickY);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputState
        {
            public uint PacketNumber;
            public XInputGamepad Gamepad;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputGamepad
        {
            public ushort Buttons;
            public byte LeftTrigger;
            public byte RightTrigger;
            public short LeftThumbX;
            public short LeftThumbY;
            public short RightThumbX;
            public short RightThumbY;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
        private static extern uint XInputGetState(uint userIndex, out XInputState state);

        private void VerifyXInputOutput(ushort expectedXInputButtons, ushort expectedVigemButtons, byte expectedLt, byte expectedRt)
        {
            if (_xbox == null)
            {
                return;
            }

            int userIndex;
            try
            {
                userIndex = _xbox.UserIndex;
            }
            catch
            {
                return;
            }

            if (userIndex < 0 || userIndex > 3)
            {
                return;
            }

            var code = XInputGetState((uint)userIndex, out var state);
            var currentVigemButtons = _xbox.ButtonState;
            var signature = $"{userIndex}:{code}:{state.PacketNumber}:{state.Gamepad.Buttons:X4}:{state.Gamepad.LeftTrigger}:{state.Gamepad.RightTrigger}:{expectedXInputButtons:X4}:{expectedVigemButtons:X4}:{currentVigemButtons:X4}:{expectedLt}:{expectedRt}";
            var now = Environment.TickCount64;

            if (code != 0)
            {
                LogXInputVerify($"[VIGEM-XINPUT] WARN slot={userIndex} XInputGetState code={code}", signature, now);
                return;
            }

            var buttonsMatch = (state.Gamepad.Buttons & expectedXInputButtons) == expectedXInputButtons;
            var triggerMatch = state.Gamepad.LeftTrigger == expectedLt && state.Gamepad.RightTrigger == expectedRt;
            var vigemButtonsMatch = currentVigemButtons == expectedVigemButtons;

            if (!buttonsMatch || !triggerMatch || !vigemButtonsMatch)
            {
                LogXInputVerify(
                    $"[VIGEM-XINPUT] WARN slot={userIndex} expectedButtons=0x{expectedXInputButtons:X4} vigemExpected=0x{expectedVigemButtons:X4} vigemState=0x{currentVigemButtons:X4} actualButtons=0x{state.Gamepad.Buttons:X4} expectedLT={expectedLt} actualLT={state.Gamepad.LeftTrigger} expectedRT={expectedRt} actualRT={state.Gamepad.RightTrigger} packet={state.PacketNumber}",
                    signature,
                    now);
            }
            else if (expectedXInputButtons != 0 || expectedVigemButtons != 0 || expectedLt != 0 || expectedRt != 0)
            {
                LogXInputVerify(
                    $"[VIGEM-XINPUT] OK slot={userIndex} buttons=0x{state.Gamepad.Buttons:X4} vigemState=0x{currentVigemButtons:X4} LT={state.Gamepad.LeftTrigger} RT={state.Gamepad.RightTrigger} packet={state.PacketNumber}",
                    signature,
                    now);
            }
        }

        private void LogXInputVerify(string message, string signature, long now)
        {
            if (signature == _lastXInputVerifySignature && now - _lastXInputVerifyLogMs < 500)
            {
                return;
            }

            _lastXInputVerifySignature = signature;
            _lastXInputVerifyLogMs = now;
            LogInput(message);
        }

        private ushort BuildExpectedXInputButtons()
        {
            ushort buttons = 0;
            if (_frameTargets.Contains("Up")) buttons |= 0x0001;
            if (_frameTargets.Contains("Down")) buttons |= 0x0002;
            if (_frameTargets.Contains("Left")) buttons |= 0x0004;
            if (_frameTargets.Contains("Right")) buttons |= 0x0008;
            if (_frameTargets.Contains("Start")) buttons |= 0x0010;
            if (_frameTargets.Contains("Back")) buttons |= 0x0020;
            if (_frameTargets.Contains("LeftThumb")) buttons |= 0x0040;
            if (_frameTargets.Contains("RightThumb")) buttons |= 0x0080;
            if (_frameTargets.Contains("LeftShoulder")) buttons |= 0x0100;
            if (_frameTargets.Contains("RightShoulder")) buttons |= 0x0200;
            if (_frameTargets.Contains("A")) buttons |= 0x1000;
            if (_frameTargets.Contains("B")) buttons |= 0x2000;
            if (_frameTargets.Contains("X")) buttons |= 0x4000;
            if (_frameTargets.Contains("Y")) buttons |= 0x8000;
            return buttons;
        }

        private ushort BuildExpectedVigemButtons(ushort xinputButtons)
        {
            return _frameTargets.Contains("Guide")
                ? (ushort)(xinputButtons | 0x0400)
                : xinputButtons;
        }

        private void ApplyVirtualStickTargets(ref (short X, short Y) leftStickOut, ref (short X, short Y) rightStickOut)
        {
            const short axisNegative = short.MinValue + 1;
            const short axisPositive = short.MaxValue;

            if (_frameTargets.Contains("LS_Left")) leftStickOut.X = axisNegative;
            if (_frameTargets.Contains("LS_Right")) leftStickOut.X = axisPositive;
            if (_frameTargets.Contains("LS_Down")) leftStickOut.Y = axisNegative;
            if (_frameTargets.Contains("LS_Up")) leftStickOut.Y = axisPositive;

            if (_frameTargets.Contains("RS_Left")) rightStickOut.X = axisNegative;
            if (_frameTargets.Contains("RS_Right")) rightStickOut.X = axisPositive;
            if (_frameTargets.Contains("RS_Down")) rightStickOut.Y = axisNegative;
            if (_frameTargets.Contains("RS_Up")) rightStickOut.Y = axisPositive;
        }
    }
}
