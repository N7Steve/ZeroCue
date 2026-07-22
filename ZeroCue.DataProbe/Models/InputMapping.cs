namespace ZeroCue.DataProbe.Models
{
    public class InputMapping
    {
        public string Name { get; set; } = "";
        public string Type { get; set; } = ""; // "button_bc", "button_bd", "button_be", "dpad", "axis", "trigger"
        public int ByteIndex { get; set; }
        public int BitMask { get; set; }
        public string Xbox360Target { get; set; } = "";
    }
}
