using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Machines;

/// <summary>Runs the bounded command/result loop inside one authenticated elevated helper.</summary>
public static class InstallerMachineHelperAuthorityLoop
{
    /// <summary>Maximum command frames accepted from one parent session.</summary>
    public const int MaximumCommandsPerSession = 16;

    /// <summary>Processes commands until an exact successful clear receipt closes the transaction.</summary>
    public static async Task RunAsync(
        Stream authenticatedStream,
        InstallerMachineHelperAuthoritySession authority,
        CancellationToken cancellationToken)
    {
        ValidateArguments(authenticatedStream, authority);
        InstallerMachineHelperCommand firstCommand = await InstallerMachineHelperFraming
            .ReadCommandAsync(authenticatedStream, cancellationToken)
            .ConfigureAwait(false);
        await RunAsync(
                authenticatedStream,
                authority,
                firstCommand,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Processes an already-authenticated first command, then reads any remaining commands from the stream.
    /// </summary>
    public static async Task RunAsync(
        Stream authenticatedStream,
        InstallerMachineHelperAuthoritySession authority,
        InstallerMachineHelperCommand firstCommand,
        CancellationToken cancellationToken)
    {
        ValidateArguments(authenticatedStream, authority);
        ArgumentNullException.ThrowIfNull(firstCommand);
        InstallerMachineHelperCommand command = firstCommand;

        for (int commandCount = 0;
             commandCount < MaximumCommandsPerSession;
             commandCount++)
        {
            InstallerMachineHelperResult result = await authority
                .ExecuteAsync(command, cancellationToken)
                .ConfigureAwait(false);
            await InstallerMachineHelperFraming
                .WriteResultAsync(authenticatedStream, result, cancellationToken)
                .ConfigureAwait(false);

            if (command.Verb == InstallerMachineHelperVerb.Clear
                && result.Outcome == InstallerMachineHelperOutcome.Succeeded)
            {
                return;
            }

            if (commandCount + 1 < MaximumCommandsPerSession)
            {
                command = await InstallerMachineHelperFraming
                    .ReadCommandAsync(authenticatedStream, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        throw new InstallerProtocolException(
            "installer.machine_helper.command_limit_exceeded");
    }

    private static void ValidateArguments(
        Stream authenticatedStream,
        InstallerMachineHelperAuthoritySession authority)
    {
        ArgumentNullException.ThrowIfNull(authenticatedStream);
        ArgumentNullException.ThrowIfNull(authority);
        if (!authenticatedStream.CanRead || !authenticatedStream.CanWrite)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.stream_invalid");
        }
    }
}
