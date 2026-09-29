using System.Linq;
using RfidManagementSystem.Models;
using RfidManagementSystem.Services;
using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;

namespace RfidManagementSystem
{
    public partial class MainWindow : Window
    {
        private readonly RfidService _rfidService;

        public MainWindow(RfidService rfidService)
        {
            InitializeComponent();

            // ==========================================
            // USE THE SAME RFID SERVICE INSTANCE
            // CREATED AND STARTED IN App.xaml.cs
            // ==========================================

            _rfidService = rfidService;

            // ==========================================
            // SUBSCRIBE TO RFID SCAN RESULTS
            // ==========================================

            _rfidService.ScanProcessed += OnScanProcessed;
            _rfidService.ReaderConnectionStatusChanged += OnReaderConnectionStatusChanged; // for dashboard Viewing RFID Connected devices
                                                                                           // Load current reader status
            UpdateReaderStatus();
        }

        // ==========================================
        // RFID SCAN RESULT
        // ==========================================

        private void OnScanProcessed(MasterRfidReader reader, string readerIp,int readerPort, RfidScanResult result)
        {
            Dispatcher.Invoke(() =>
            {
                string displayText =
                    $"Reader: {reader.ReaderName}\n" +
                    $"Purpose: {reader.ReaderPurpose}\n\n" +
                    $"Reader IP: {readerIp}\n" +
                    $"Server Port: {readerPort}\n\n" +
                    $"Card UID: {result.CardUid}\n\n" +
                    $"Result: {result.ResultType}\n" +
                    $"Message: {result.Message}";

                // ==========================================
                // EMPLOYEE DETAILS
                // ==========================================

                if (result.EmployeeId.HasValue)
                {
                    displayText +=
                        $"\n\nEmployee ID: {result.EmployeeId}";
                }

                if (!string.IsNullOrWhiteSpace(
                    result.EmployeeName))
                {
                    displayText +=
                        $"\nEmployee Name: {result.EmployeeName}";
                }

                if (!string.IsNullOrWhiteSpace(
                    result.EmployeeCode))
                {
                    displayText +=
                        $"\nEmployee Code: {result.EmployeeCode}";
                }

                // ==========================================
                // ALERT / ANNOUNCEMENT STATUS
                // ==========================================

                displayText +=
                    $"\n\nDashboard Alert: " +
                    $"{result.ShouldAlertDashboard}";

                displayText +=
                    $"\nAnnouncement Required: " +
                    $"{result.ShouldAnnounce}";

                // ==========================================
                // LATEST RFID DATA
                // ==========================================

                txtReceivedData.Text = displayText;

                // ==========================================
                // ALERT SECTION
                // ==========================================

                if (result.ShouldAlertDashboard)
                {
                    txtAlerts.Text =
                        $"[{DateTime.Now:HH:mm:ss}]\n" +
                        $"Reader: {reader.ReaderName}\n" +
                        $"Purpose: {reader.ReaderPurpose}\n\n" +
                        $"{result.ResultType}: {result.Message}";
                }

                // ==========================================
                // ERROR SECTION
                // ==========================================

                if (result.ResultType ==
                    RfidScanResultType.ERROR)
                {
                    txtErrors.Text =
                        $"[{DateTime.Now:HH:mm:ss}]\n" +
                        $"Reader: {reader.ReaderName}\n\n" +
                        $"Error: {result.Message}";
                }
            });
        }

        // ==========================================
        // CODEINQ WEBSITE LINK
        // ==========================================

        private void CodeInQHyperlink_RequestNavigate(object sender,RequestNavigateEventArgs e)
        {
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = e.Uri.AbsoluteUri,
                    UseShellExecute = true
                }
            );

            e.Handled = true;
        }

        private void OnReaderConnectionStatusChanged()
        {
            Dispatcher.Invoke(() =>
            {
                UpdateReaderStatus();
            });
        }

        private void UpdateReaderStatus()
        {
            var connectedReaders =
                _rfidService.GetConnectedReaders();

            var disconnectedReaders =
                _rfidService.GetDisconnectedReaders();

            // ==========================================
            // CONNECTED READERS
            // ==========================================

            if (connectedReaders.Count == 0)
            {
                txtConnectedReaders.Text =
                    "No readers connected.";
            }
            else
            {
                txtConnectedReaders.Text =
                    $"Connected Readers: {connectedReaders.Count}\n\n" +
                    string.Join(
                        "\n\n",
                        connectedReaders.Select(reader =>
                            $"● {reader.ReaderName}\n" +
                            $"   IP: {reader.IpAddress}\n" +
                            $"   Port: {reader.Port}\n" +
                            $"   Purpose: {reader.ReaderPurpose}"
                        )
                    );
            }

            // ==========================================
            // DISCONNECTED READERS
            // ==========================================

            if (disconnectedReaders.Count == 0)
            {
                txtDisconnectedReaders.Text =
                    "All readers are connected.";
            }
            else
            {
                txtDisconnectedReaders.Text =
                    $"Disconnected Readers: {disconnectedReaders.Count}\n\n" +
                    string.Join(
                        "\n\n",
                        disconnectedReaders.Select(reader =>
                            $"● {reader.ReaderName}\n" +
                            $"   IP: {reader.IpAddress}\n" +
                            $"   Port: {reader.Port}\n" +
                            $"   Purpose: {reader.ReaderPurpose}"
                        )
                    );
            }
        }

        // ==========================================
        // CLOSE RFID TCP SERVICE
        // ==========================================

        protected override void OnClosed(EventArgs e)
        {
            // Remove event subscription
            _rfidService.ScanProcessed -= OnScanProcessed;

            // Stop RFID TCP servers
            _rfidService.Stop();

            base.OnClosed(e);
        }
    }
}