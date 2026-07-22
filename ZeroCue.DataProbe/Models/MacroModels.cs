using System.Collections.Generic;

namespace ZeroCue.DataProbe.Models
{
    public static class MacroTarget
    {
        public const string Prefix = "Macro:";

        public static bool IsMacroTarget(string? target)
        {
            return !string.IsNullOrWhiteSpace(target) && target.StartsWith(Prefix, System.StringComparison.Ordinal);
        }

        public static string Create(string macroId)
        {
            return $"{Prefix}{macroId}";
        }

        public static string GetId(string macroTarget)
        {
            return IsMacroTarget(macroTarget) ? macroTarget.Substring(Prefix.Length) : string.Empty;
        }
    }

    public class MacroDefinition
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = "Macro";
        public bool RepeatWhileHeld { get; set; }
        public List<MacroStep> Steps { get; set; } = new();
    }

    public class MacroStep
    {
        public string InputKind { get; set; } = MacroInputKinds.Keyboard;
        public string Target { get; set; } = string.Empty;
        public string Action { get; set; } = MacroActions.Down;
        public int DelayMs { get; set; }
    }

    public class MacroInputEvent
    {
        public string InputKind { get; set; } = MacroInputKinds.Gamepad;
        public string Target { get; set; } = string.Empty;
        public string Action { get; set; } = MacroActions.Down;
    }

    public static class MacroInputKinds
    {
        public const string Gamepad = "Gamepad";
        public const string Keyboard = "Keyboard";
        public const string Mouse = "Mouse";
    }

    public static class MacroActions
    {
        public const string Down = "Down";
        public const string Up = "Up";
    }
}
