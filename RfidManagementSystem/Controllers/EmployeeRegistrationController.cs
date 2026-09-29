using Microsoft.AspNetCore.Mvc;
using RfidManagementSystem.Services;

namespace RfidManagementSystem.Controllers
{
    [ApiController]
    [Route("api/employee-registration")]
    public class EmployeeRegistrationController
        : ControllerBase
    {
        private readonly
            EmployeeRegistrationService
            _employeeRegistrationService;

        public EmployeeRegistrationController(
            EmployeeRegistrationService
                employeeRegistrationService)
        {
            _employeeRegistrationService =
                employeeRegistrationService;
        }

        // ==========================================
        // TEST CONTROLLER IN BROWSER
        // ==========================================

        [HttpGet]
        public IActionResult GetStatus()
        {
            return Ok(new
            {
                success = true,
                message = "Employee Registration Controller is working!"
            });
        }

        // ==========================================
        // DASHBOARD SERVICE CALLS THIS API
        //
        // POST:
        // /api/employee-registration/start
        // ==========================================

        [HttpPost("scan")]
        public async Task<IActionResult> Start(
            CancellationToken cancellationToken)
        {
            try
            {
                // Wait until employee registration
                // RFID reader scans a card

                string cardUid =await _employeeRegistrationService
                        .WaitForCardAsync(
                            cancellationToken
                        );

                return Ok(
                    new
                    {
                        success = true,

                        message =
                            "RFID card scanned successfully.",

                        cardUid = cardUid
                    }
                );
            }
            catch (OperationCanceledException)
            {
                return StatusCode(
                    408,
                    new
                    {
                        success = false,

                        message =
                            "RFID card scanning timed out."
                    }
                );
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(
                    new
                    {
                        success = false,

                        message = ex.Message
                    }
                );
            }
            catch (Exception ex)
            {
                return StatusCode(
                    500,
                    new
                    {
                        success = false,

                        message =
                            "Error while waiting for RFID card.",

                        error = ex.Message
                    }
                );
            }
        }
    }
}