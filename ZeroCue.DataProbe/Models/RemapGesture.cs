using System.Collections.Generic;
using ZeroCue.DataProbe.Services;

namespace ZeroCue.DataProbe.Models
{
    public static class RemapGestureTypes
    {
        public const string Simple = "Simple";
        public const string DoubleTap = "DoubleTap";
        public const string Hold = "Hold";
        public const string PressStart = "PressStart";
        public const string PressRelease = "PressRelease";

        public static readonly IReadOnlyList<string> AdvancedTypes = new[]
        {
            DoubleTap,
            Hold,
            PressStart,
            PressRelease
        };

        public static string GetLabel(string type)
        {
            return type switch
            {
                DoubleTap => LocalizationService.Get("DoubleTap"),
                Hold => LocalizationService.Get("Hold"),
                PressStart => LocalizationService.Get("PressStart"),
                PressRelease => LocalizationService.Get("PressRelease"),
                _ => LocalizationService.Get("SinglePress")
            };
        }
    }
}
