using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using RfidManagementSystem.Services;
using System;
using System.Linq;
using System.Windows;

namespace RfidManagementSystem
{
    public partial class App : Application
    {
        private WebApplication? _webApplication;
        private RfidService? _rfidService;
        private bool _backgroundMode;

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _backgroundMode = e.Args.Any(a =>
                string.Equals(a, "--background", StringComparison.OrdinalIgnoreCase)
                || string.Equals(a, "/background", StringComparison.OrdinalIgnoreCase));

            if (_backgroundMode)
            {
                // No UI; stay alive until process is killed (installer / startup).
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            try
            {
                var builder = WebApplication.CreateBuilder();

                builder.Services.AddControllers();
                builder.Services.AddSingleton<EmployeeRegistrationService>();
                builder.Services.AddSingleton<RfidService>();

                _webApplication = builder.Build();
                _webApplication.MapControllers();

                await _webApplication.StartAsync();

                _rfidService = _webApplication.Services.GetRequiredService<RfidService>();

                await _rfidService.StartAsync();

                if (_backgroundMode)
                {
                    return;
                }

                var mainWindow = new MainWindow(_rfidService)
                {
                    ShowInTaskbar = true
                };
                MainWindow = mainWindow;
                mainWindow.Show();
            }
            catch (Exception ex)
            {
                if (!_backgroundMode)
                {
                    MessageBox.Show(
                        ex.ToString(),
                        "RFID Service Startup Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }

                Shutdown();
            }
        }

        protected override async void OnExit(ExitEventArgs e)
        {
            Console.WriteLine(">>> APP OnExit CALLED");
            try
            {
                if (_rfidService != null)
                {
                    Console.WriteLine(">>> CALLING RFID SERVICE STOP");
                    _rfidService.Stop();
                }

                if (_webApplication != null)
                {
                    await _webApplication.StopAsync();
                    await _webApplication.DisposeAsync();
                }
            }
            catch
            {
                // Ignore shutdown errors
            }

            base.OnExit(e);
        }
    }
}
