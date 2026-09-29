using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RfidManagementSystem.Services
{
    public class EmployeeRegistrationService
    {
        private TaskCompletionSource<string>? _waitingForCard;

        private readonly object _lock = new();

        public Task<string> WaitForCardAsync(
            CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                if (_waitingForCard != null)
                {
                    throw new InvalidOperationException(
                        "RFID card scan is already in progress."
                    );
                }

                _waitingForCard =
                    new TaskCompletionSource<string>(
                        TaskCreationOptions
                            .RunContinuationsAsynchronously
                    );

                cancellationToken.Register(() =>
                {
                    lock (_lock)
                    {
                        _waitingForCard?.TrySetCanceled();

                        _waitingForCard = null;
                    }
                });

                return _waitingForCard.Task;
            }
        }

        public void SubmitCardUid(string cardUid)
        {
            lock (_lock)
            {
                if (_waitingForCard == null)
                {
                    return;
                }

                _waitingForCard.TrySetResult(cardUid);

                _waitingForCard = null;
            }
        }
    }

}
