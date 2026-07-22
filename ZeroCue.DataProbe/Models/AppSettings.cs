namespace ZeroCue.DataProbe.Models
{
    public class AppSettings
    {
        public string LanguageCode { get; set; } = "en";
        public string ThemeName { get; set; } = "DefaultTheme";
        public bool StartWithWindows { get; set; } = false;
        public bool StartMinimized { get; set; } = false;
        public string DefaultProfileName { get; set; } = "Default";
    }
}
