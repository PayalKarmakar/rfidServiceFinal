using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RfidManagementSystem.Models
{
    public class MasterRfidReader
    {
        public long ReaderId { get; set; }

        public string ReaderName { get; set; } = string.Empty;

        public string ReaderSerialno { get; set; } = string.Empty;

        public string IpAddress { get; set; } = string.Empty;

        public int Port { get; set; }

        public string ReaderPurpose { get; set; } = string.Empty;

        public bool IsActive { get; set; }

        public DateTime CreatedAt { get; set; }

        public DateTime? UpdatedAt { get; set; }

        public long? LastUpdatedBy { get; set; }
    }
}
