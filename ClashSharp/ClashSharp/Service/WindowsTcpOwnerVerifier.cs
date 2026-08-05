using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace ClashSharp.Service;

/// <summary>Authenticates IPv4 TCP endpoints against the owning process reported by Windows.</summary>
internal interface IWindowsTcpOwnerVerifier
{
    /// <summary>Checks that the server side of an established loopback connection has one exact owner.</summary>
    bool IsConnectedServerOwnedBy(Socket connectedClient, int expectedPid);

    /// <summary>Checks that an exact IPv4 loopback listening endpoint has one exact owner.</summary>
    bool IsLoopbackListenerOwnedBy(int port, int expectedPid);
}

/// <summary>Reads the bounded Windows IPv4 owner-PID table without using connection heuristics.</summary>
internal sealed class WindowsTcpOwnerVerifier : IWindowsTcpOwnerVerifier
{
    private const int AddressFamilyInternet = 2;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint NoError = 0;
    private const int MaximumTableBytes = 8 * 1024 * 1024;
    private const int MaximumTableRows = 262_144;
    private const int MaximumSnapshotAttempts = 3;
    private const uint TcpStateListen = 2;
    private const uint TcpStateEstablished = 5;
    private const int TcpTableOwnerPidAll = 5;

    internal static WindowsTcpOwnerVerifier Instance { get; } = new();

    private WindowsTcpOwnerVerifier()
    {
    }

    /// <inheritdoc />
    public bool IsConnectedServerOwnedBy(Socket connectedClient, int expectedPid)
    {
        ArgumentNullException.ThrowIfNull(connectedClient);
        if (expectedPid < 1
            || connectedClient.AddressFamily != AddressFamily.InterNetwork
            || !connectedClient.Connected
            || connectedClient.LocalEndPoint is not IPEndPoint clientEndpoint
            || connectedClient.RemoteEndPoint is not IPEndPoint serverEndpoint
            || clientEndpoint.AddressFamily != AddressFamily.InterNetwork
            || serverEndpoint.AddressFamily != AddressFamily.InterNetwork
            || !IPAddress.Loopback.Equals(clientEndpoint.Address)
            || !IPAddress.Loopback.Equals(serverEndpoint.Address))
        {
            return false;
        }

        if (!TryReadRows(out IReadOnlyList<TcpOwnerRow> rows))
        {
            return false;
        }

        return HasOneExactOwner(
            rows,
            row => row.State == TcpStateEstablished
                && AddressEquals(row.LocalAddress, serverEndpoint.Address)
                && DecodePort(row.LocalPort) == serverEndpoint.Port
                && AddressEquals(row.RemoteAddress, clientEndpoint.Address)
                && DecodePort(row.RemotePort) == clientEndpoint.Port,
            expectedPid);
    }

    /// <inheritdoc />
    public bool IsLoopbackListenerOwnedBy(int port, int expectedPid)
    {
        if (port is < IPEndPoint.MinPort or > IPEndPoint.MaxPort || expectedPid < 1)
        {
            return false;
        }

        if (!TryReadRows(out IReadOnlyList<TcpOwnerRow> rows))
        {
            return false;
        }

        return HasOneExactOwner(
            rows,
            row => row.State == TcpStateListen
                && AddressEquals(row.LocalAddress, IPAddress.Loopback)
                && DecodePort(row.LocalPort) == port,
            expectedPid);
    }

    /// <summary>Decodes the low 16 bits of a MIB TCP port, ignoring its unspecified upper bits.</summary>
    internal static int DecodePort(uint networkOrderPort) =>
        BinaryPrimitives.ReverseEndianness((ushort)networkOrderPort);

    private static bool HasOneExactOwner(
        IEnumerable<TcpOwnerRow> rows,
        Func<TcpOwnerRow, bool> matchesEndpoint,
        int expectedPid)
    {
        HashSet<uint> owners = [];
        foreach (TcpOwnerRow row in rows)
        {
            if (matchesEndpoint(row))
            {
                owners.Add(row.OwningProcessId);
                if (owners.Count > 1)
                {
                    return false;
                }
            }
        }

        return owners.Count == 1 && owners.Contains((uint)expectedPid);
    }

    private static bool AddressEquals(uint tableAddress, IPAddress expectedAddress)
    {
        Span<byte> addressBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(addressBytes, tableAddress);
        return expectedAddress.Equals(new IPAddress(addressBytes));
    }

    private static bool TryReadRows(out IReadOnlyList<TcpOwnerRow> rows)
    {
        rows = Array.Empty<TcpOwnerRow>();
        uint requiredBytes = 0;
        uint result = GetExtendedTcpTable(
            nint.Zero,
            ref requiredBytes,
            sort: false,
            AddressFamilyInternet,
            TcpTableOwnerPidAll,
            reserved: 0);
        if (result != ErrorInsufficientBuffer || !IsValidTableSize(requiredBytes))
        {
            return false;
        }

        for (int attempt = 0; attempt < MaximumSnapshotAttempts; attempt++)
        {
            nint buffer = Marshal.AllocHGlobal(checked((int)requiredBytes));
            try
            {
                uint returnedBytes = requiredBytes;
                result = GetExtendedTcpTable(
                    buffer,
                    ref returnedBytes,
                    sort: false,
                    AddressFamilyInternet,
                    TcpTableOwnerPidAll,
                    reserved: 0);
                if (result == ErrorInsufficientBuffer)
                {
                    if (returnedBytes <= requiredBytes || !IsValidTableSize(returnedBytes))
                    {
                        return false;
                    }

                    requiredBytes = returnedBytes;
                    continue;
                }

                if (result != NoError
                    || returnedBytes > requiredBytes
                    || !TryParseRows(buffer, returnedBytes, out TcpOwnerRow[] parsedRows))
                {
                    return false;
                }

                rows = parsedRows;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return false;
    }

    private static bool TryParseRows(
        nint buffer,
        uint bufferBytes,
        out TcpOwnerRow[] rows)
    {
        rows = [];
        int rowBytes = Marshal.SizeOf<TcpOwnerRow>();
        if (buffer == nint.Zero || bufferBytes < sizeof(uint))
        {
            return false;
        }

        uint rowCount = unchecked((uint)Marshal.ReadInt32(buffer));
        if (rowCount > MaximumTableRows)
        {
            return false;
        }

        long requiredBytes = sizeof(uint) + ((long)rowCount * rowBytes);
        if (requiredBytes > bufferBytes || requiredBytes > MaximumTableBytes)
        {
            return false;
        }

        rows = new TcpOwnerRow[checked((int)rowCount)];
        for (int index = 0; index < rows.Length; index++)
        {
            nint rowAddress = nint.Add(buffer, checked(sizeof(uint) + (index * rowBytes)));
            rows[index] = Marshal.PtrToStructure<TcpOwnerRow>(rowAddress);
        }

        return true;
    }

    private static bool IsValidTableSize(uint byteCount) =>
        byteCount is >= sizeof(uint) and <= MaximumTableBytes;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct TcpOwnerRow
    {
        internal readonly uint State;
        internal readonly uint LocalAddress;
        internal readonly uint LocalPort;
        internal readonly uint RemoteAddress;
        internal readonly uint RemotePort;
        internal readonly uint OwningProcessId;
    }

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(
        nint tcpTable,
        ref uint outputBufferLength,
        [MarshalAs(UnmanagedType.Bool)] bool sort,
        int ipVersion,
        int tableClass,
        uint reserved);
}
