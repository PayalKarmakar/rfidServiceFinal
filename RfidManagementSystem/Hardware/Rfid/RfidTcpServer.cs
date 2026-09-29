using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RfidManagementSystem.Hardware.Rfid;

public class RfidTcpServer
{
    private TcpListener? _listener;
    private TcpClient? _client;
    private CancellationTokenSource? _cancellationTokenSource;

    public bool IsRunning { get; private set; }

    public event Action<string>? StatusChanged;
    //public event Action<byte[]>? DataReceived;
    public event Action<string, int, byte[]>? DataReceived;

    public async Task StartAsync(int port)
    {
        if (IsRunning)
        {
            StatusChanged?.Invoke("TCP Server is already running.");
            return;
        }

        try
        {
            _cancellationTokenSource = new CancellationTokenSource();

            _listener = new TcpListener(IPAddress.Any, port);
            _listener.Start();

            IsRunning = true;

            StatusChanged?.Invoke(
                $"TCP Server started. Waiting for RFID reader on port {port}..."
            );

            while (!_cancellationTokenSource.Token.IsCancellationRequested)
            {
                _client = await _listener.AcceptTcpClientAsync(
                    _cancellationTokenSource.Token
                );

                //StatusChanged?.Invoke(
                //    $"RFID Reader connected: {_client.Client.RemoteEndPoint}"
                //);
                // RFID Reader IP
                var remoteEndPoint = (IPEndPoint)_client.Client.RemoteEndPoint!;
                // Your application's listening port
                var localEndPoint = (IPEndPoint)_client.Client.LocalEndPoint!;

                StatusChanged?.Invoke(
                    $"RFID_READER_CONNECTED|" +
                    $"IP={remoteEndPoint.Address}|" +
                    $"REMOTE_PORT={remoteEndPoint.Port}|" +
                    $"SERVER_PORT={localEndPoint.Port}"
                );

                _ = HandleClientAsync(
                    _client,
                    _cancellationTokenSource.Token
                );
            }
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke("TCP Server stopped.");
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke($"TCP Server error: {ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        try
        {
            using NetworkStream stream = client.GetStream();

            byte[] buffer = new byte[4096];

            while (!cancellationToken.IsCancellationRequested &&
                   client.Connected)
            {
                int bytesRead = await stream.ReadAsync(
                    buffer.AsMemory(0, buffer.Length),
                    cancellationToken
                );

                if (bytesRead == 0)
                {
                    break;
                }

                byte[] receivedData = new byte[bytesRead];

                Array.Copy(
                    buffer,
                    receivedData,
                    bytesRead
                );

                var remoteEndPoint =
    (IPEndPoint)client.Client.RemoteEndPoint!;

                var localEndPoint =
                    (IPEndPoint)client.Client.LocalEndPoint!;

                string readerIp = remoteEndPoint.Address.ToString();
                int serverPort = localEndPoint.Port;

                DataReceived?.Invoke(
                    readerIp,
                    serverPort,
                    receivedData
                );
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(
                $"RFID connection error: {ex.Message}"
            );
        }
        finally
        {
            client.Close();

            StatusChanged?.Invoke(
                "RFID_READER_DISCONNECTED|reason=client_closed"
            );
        }
    }

    public async Task SendAsync(string message)
    {
        if (_client == null || !_client.Connected)
        {
            StatusChanged?.Invoke("No RFID reader is connected.");
            return;
        }

        try
        {
            byte[] data = Encoding.UTF8.GetBytes(message);

            NetworkStream stream = _client.GetStream();

            await stream.WriteAsync(data, 0, data.Length);

            StatusChanged?.Invoke(
                $"Data sent to RFID reader: {message}"
            );
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke(
                $"Error sending data: {ex.Message}"
            );
        }
    }

    public async Task SendToClientAsync(string message)
    {
        if (_client == null || !_client.Connected)
        {
            throw new Exception("No RFID client is connected.");
        }

        byte[] data = System.Text.Encoding.UTF8.GetBytes(message);

        NetworkStream stream = _client.GetStream();

        await stream.WriteAsync(data, 0, data.Length);

        StatusChanged?.Invoke(
            $"Message sent to client: {message}"
        );
    }

    public void Stop()
    {
        _cancellationTokenSource?.Cancel();

        _client?.Close();

        _listener?.Stop();

        IsRunning = false;

        StatusChanged?.Invoke("TCP Server stopped.");
    }
}