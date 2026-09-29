using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RfidManagementSystem.Models
{
    public class RfidScanResult
    {
        // What happened with this RFID scan
        public RfidScanResultType ResultType { get; set; }

        // Message describing the result
        public string Message { get; set; } = string.Empty;

        // RFID card UID that was scanned
        public string CardUid { get; set; } = string.Empty;

        // Employee details, if the card belongs to an employee
        public long? EmployeeId { get; set; }

        public string? EmployeeName { get; set; }

        public string? EmployeeCode { get; set; }

        // Whether this result should be sent
        // to Dashboard Service as an alert
        public bool ShouldAlertDashboard { get; set; }

        // Whether Dashboard should trigger
        // an audio announcement
        public bool ShouldAnnounce { get; set; }
    }
}
