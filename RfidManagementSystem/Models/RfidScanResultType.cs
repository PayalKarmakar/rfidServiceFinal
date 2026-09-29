using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RfidManagementSystem.Models
{
    public enum RfidScanResultType
    {
        ENTRY_RECORDED, // Successful entry

        EXIT_RECORDED, // Successful exit

        SCAN_COOLDOWN,  // Card was scanned again within configured cooldown/gap

        UNAUTHORIZED,  // RFID card is not registered in the system

        ACCESS_DENIED,  // Employee exists but access cannot be allowed

        EMPLOYEE_REGISTRATION, // Card detected for employee registration

        ERROR    // General processing error
    }
}
