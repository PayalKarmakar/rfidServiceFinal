using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace RfidManagementSystem.Hardware.Rfid;

public class RfidTcpClient
{
    private TcpClient? _client;
    private NetworkStream? _stream;

    private string? _readerIp;
    private int _readerPort;

    private readonly SemaphoreSlim _commandLock =
        new(1, 1);

    public bool IsConnected =>
        _client != null &&
        _client.Connected &&
        _stream != null;

    public bool IsRunning { get; private set; }

    public event Action<string>? StatusChanged;

    public event Action<string, int, byte[]>? DataReceived;


    // =========================================================
    // CONNECT
    // =========================================================

    public async Task<bool> ConnectAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _readerIp = host;
            _readerPort = port;

            StatusChanged?.Invoke(
                $"RFID_READER_CONNECTING|" +
                $"IP={host}|" +
                $"PORT={port}");

            _client = new TcpClient();

            await _client.ConnectAsync(
                host,
                port,
                cancellationToken);

            _stream = _client.GetStream();

            IsRunning = true;

            StatusChanged?.Invoke(
                $"RFID_READER_CONNECTED|" +
                $"IP={host}|" +
                $"PORT={port}");

            return true;
        }
        catch (OperationCanceledException)
        {
            Disconnect();
            return false;
        }
        catch (Exception ex)
        {
            IsRunning = false;

            StatusChanged?.Invoke(
                $"RFID_READER_CONNECTION_FAILED|" +
                $"IP={host}|" +
                $"PORT={port}|" +
                $"ERROR={ex.Message}");

            Disconnect();

            return false;
        }
    }


    // =========================================================
    // SEND COMMAND
    // =========================================================

    public async Task<byte[]?> SendCommandAsync(byte[] command,CancellationToken cancellationToken = default)
    {
        if (!IsConnected ||_stream == null)
        {
            StatusChanged?.Invoke(
                $"RFID_READER_NOT_CONNECTED|" +
                $"IP={_readerIp}|" +
                $"PORT={_readerPort}");

            return null;
        }

        await _commandLock.WaitAsync(cancellationToken);

        try
        {
            // =====================================================
            // ADD CRC
            // =====================================================

            byte[] packet = BuildPacketWithCrc(command);


            // =====================================================
            // LOG TX
            // =====================================================

            StatusChanged?.Invoke(
                $"RFID_TX|" +
                $"{BitConverter.ToString(packet)
                    .Replace("-", " ")}");


            // =====================================================
            // SEND
            // =====================================================

            await _stream.WriteAsync(
                packet,
                cancellationToken);

            await _stream.FlushAsync(
                cancellationToken);


            // =====================================================
            // READ RESPONSE
            // =====================================================

            byte[] response = await ReadResponseAsync(cancellationToken);
            // =========================================================
            // NO RESPONSE
            // =========================================================

            if (response == null)
            {
                return null;
            }


            // =====================================================
            // LOG RX
            // =====================================================

            StatusChanged?.Invoke(
                $"RFID_RX|" +
                $"{BitConverter.ToString(response)
                    .Replace("-", " ")}");


            return response;
        }
        catch (IOException ex)
        {
            StatusChanged?.Invoke(
                $"RFID_TCP_ERROR|" +
                $"{ex.Message}");

            Disconnect();

            return null;
        }
        catch (SocketException ex)
        {
            StatusChanged?.Invoke(
                $"RFID_SOCKET_ERROR|" +
                $"{ex.Message}");

            Disconnect();

            return null;
        }
        finally
        {
            _commandLock.Release();
        }
    }


    // =========================================================
    // BUILD PACKET + CRC
    // =========================================================

    private byte[] BuildPacketWithCrc(byte[] command)
    {
        if (command == null || command.Length < 2)
            throw new ArgumentException("Invalid RFID command.");

        byte[] packet = new byte[command.Length + 2];

        Buffer.BlockCopy(
            command,
            0,
            packet,
            0,
            command.Length);

        int crc = 0xFFFF;

        // IMPORTANT:
        // Same as old working MainWindow code.
        //
        // command:
        // 03 2F 01
        //
        // CRC is calculated over:
        // 03 2F 01
        //
        for (int i = 0; i < packet[0]; i++)
        {
            crc ^= packet[i];

            for (int bit = 0; bit < 8; bit++)
            {
                if ((crc & 0x8000) != 0)
                {
                    crc = (crc << 1) ^ 0x1021;
                }
                else
                {
                    crc <<= 1;
                }

                crc &= 0xFFFF;
            }
        }

        crc = (~crc) & 0xFFFF;

        packet[packet.Length - 2] =
            (byte)(crc >> 8);

        packet[packet.Length - 1] =
            (byte)crc;

        return packet;
    }


    // =========================================================
    // CRC
    // =========================================================

    private ushort CalculateCrc(
        byte[] buffer,
        int offset,
        int length)
    {
        int crc = 0xFFFF;

        for (
            int i = offset;
            i < offset + length;
            i++)
        {
            crc ^= buffer[i];

            for (
                int bit = 0;
                bit < 8;
                bit++)
            {
                if ((crc & 0x8000) != 0)
                {
                    crc =
                        (crc << 1) ^
                        0x1021;
                }
                else
                {
                    crc <<= 1;
                }

                crc &= 0xFFFF;
            }
        }

        crc =
            (~crc) & 0xFFFF;

        return (ushort)crc;
    }


    // =========================================================
    // READ RESPONSE
    // =========================================================

    private async Task<byte[]?> ReadResponseAsync(
    CancellationToken cancellationToken)
    {
        if (_stream == null)
            throw new IOException(
                "RFID network stream is unavailable.");

        // Same timing as old working application
        await Task.Delay(
            100,
            cancellationToken);

        if (!_stream.DataAvailable)
        {
            StatusChanged?.Invoke(
                $"RFID_NO_RESPONSE|IP={_readerIp}|PORT={_readerPort}");

            return null;
        }

        byte[] buffer = new byte[256];

        int bytesRead =
            await _stream.ReadAsync(
                buffer.AsMemory(
                    1,
                    buffer.Length - 1),
                cancellationToken);

        if (bytesRead <= 0)
            return null;

        byte[] response =
            new byte[bytesRead + 1];

        Buffer.BlockCopy(
            buffer,
            0,
            response,
            0,
            bytesRead + 1);

        return response;
    }

    // =========================================================
    // READ EXACTLY
    // =========================================================

    private static async Task ReadExactlyAsync(NetworkStream stream,byte[] buffer,CancellationToken cancellationToken)
    {
        int totalRead = 0;

        while (totalRead < buffer.Length)
        {
            int read =
                await stream.ReadAsync(
                    buffer.AsMemory(
                        totalRead,
                        buffer.Length - totalRead),
                    cancellationToken);

            if (read == 0)
            {
                throw new IOException(
                    "RFID reader closed the TCP connection.");
            }

            totalRead += read;
        }
    }


    // =========================================================
    // DISCONNECT
    // =========================================================

    public void Disconnect()
    {
        try
        {
            _stream?.Close();
            _stream?.Dispose();
        }
        catch
        {
        }

        try
        {
            _client?.Close();
            _client?.Dispose();
        }
        catch
        {
        }

        _stream = null;
        _client = null;

        IsRunning = false;

        StatusChanged?.Invoke(
            $"RFID_READER_DISCONNECTED|" +
            $"IP={_readerIp}|" +
            $"PORT={_readerPort}");
    }
}