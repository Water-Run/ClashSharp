using System.Buffers.Binary;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Machines;

/// <summary>Length-prefixes strict helper commands and results on one persistent local stream.</summary>
public static class InstallerMachineHelperFraming
{
    private const int HeaderBytes = sizeof(int);

    /// <summary>Writes one complete command frame without closing the caller stream.</summary>
    public static Task WriteCommandAsync(
        Stream stream,
        InstallerMachineHelperCommand command,
        CancellationToken cancellationToken) =>
        WriteFrameAsync(
            stream,
            InstallerMachineHelperCommandCodec.Serialize(command),
            cancellationToken);

    /// <summary>Reads one complete command frame without consuming the following frame.</summary>
    public static async Task<InstallerMachineHelperCommand> ReadCommandAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] payload = await ReadFrameAsync(
                stream,
                InstallerMachineHelperCommandCodec.MaximumCommandBytes,
                cancellationToken)
            .ConfigureAwait(false);
        return InstallerMachineHelperCommandCodec.Parse(payload);
    }

    /// <summary>Writes one complete result frame without closing the caller stream.</summary>
    public static Task WriteResultAsync(
        Stream stream,
        InstallerMachineHelperResult result,
        CancellationToken cancellationToken) =>
        WriteFrameAsync(
            stream,
            InstallerMachineHelperResultCodec.Serialize(result),
            cancellationToken);

    /// <summary>Reads one complete result frame without consuming the following frame.</summary>
    public static async Task<InstallerMachineHelperResult> ReadResultAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        byte[] payload = await ReadFrameAsync(
                stream,
                InstallerMachineHelperResultCodec.MaximumResultBytes,
                cancellationToken)
            .ConfigureAwait(false);
        return InstallerMachineHelperResultCodec.Parse(payload);
    }

    private static async Task WriteFrameAsync(
        Stream stream,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] header = new byte[HeaderBytes];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(
        Stream stream,
        int maximumPayloadBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] header = new byte[HeaderBytes];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int payloadLength = BinaryPrimitives.ReadInt32BigEndian(header);
        if (payloadLength is < 1 || payloadLength > maximumPayloadBytes)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.frame_size_invalid");
        }

        byte[] payload = GC.AllocateUninitializedArray<byte>(payloadLength);
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }
}
