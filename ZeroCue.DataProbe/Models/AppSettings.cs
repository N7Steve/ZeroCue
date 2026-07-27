using System.Text.Json.Serialization;

namespace ZeroCue.DataProbe.Models
{
    public enum ApplicationCloseBehavior
    {
        MinimizeToTray,
        ExitApplication
    }

    public class AppSettings
    {
        public string LanguageCode { get; set; } = "en";
        public string ThemeName { get; set; } = "DefaultTheme";
        public bool StartWithWindows { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public ApplicationCloseBehavior CloseBehavior { get; set; } = ApplicationCloseBehavior.MinimizeToTray;
        public bool AskBeforeClosing { get; set; } = true;
        public string DefaultProfileName { get; set; } = "Default";
    }
}
