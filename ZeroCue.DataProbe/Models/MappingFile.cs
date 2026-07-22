using System.Collections.Generic;

namespace ZeroCue.DataProbe.Models
{
    public class MappingFile
    {
        public List<InputMapping> Mappings { get; set; } = new List<InputMapping>();
        public string GeneratedAt { get; set; } = "";
        public List<byte> Baseline { get; set; } = new List<byte>();
    }
}
