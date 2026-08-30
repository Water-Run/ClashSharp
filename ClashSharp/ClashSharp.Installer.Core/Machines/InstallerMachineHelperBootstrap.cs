using System.Globalization;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Machines;

/// <summary>
/// Strict process bootstrap that keeps parent identity separate from journal command identity.
/// </summary>
/// <param name="Invocation">Exact first journal-bearing helper invocation.</param>
/// <param name="ParentProcessId">PID of the unelevated pipe-server process.</param>
public sealed record InstallerMachineHelperBootstrap(
    InstallerMachineHelperInvocation Invocation,
    int ParentProcessId)
{
    private const string Mode = "--machine-helper";
    private const string ParentProcessOption = "--machine-parent-pid";

    /// <summary>Creates a validated bootstrap for one parent and one first command.</summary>
    /// <param name="invocation">Exact first journal-bearing helper invocation.</param>
    /// <param name="parentProcessId">Positive PID of the unelevated server process.</param>
    /// <returns>The validated process bootstrap.</returns>
    public static InstallerMachineHelperBootstrap Create(
        InstallerMachineHelperInvocation invocation,
        int parentProcessId)
    {
        var bootstrap = new InstallerMachineHelperBootstrap(invocation, parentProcessId);
        bootstrap.Validate();
        return bootstrap;
    }

    /// <summary>
    /// Parses the exact elevated-process grammar, or returns <see langword="null"/> for UI mode.
    /// </summary>
    /// <param name="arguments">Raw process arguments.</param>
    /// <returns>A validated helper bootstrap, or <see langword="null"/> for ordinary UI arguments.</returns>
    public static InstallerMachineHelperBootstrap? Parse(
        IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Any(static argument => argument is null))
        {
            throw InvalidArguments();
        }

        if (arguments.Count == 0
            || !string.Equals(arguments[0], Mode, StringComparison.Ordinal))
        {
            _ = InstallerMachineHelperInvocation.Parse(arguments);
            return null;
        }

        if (arguments.Count != 8
            || !string.Equals(
                arguments[6],
                ParentProcessOption,
                StringComparison.Ordinal)
            || !int.TryParse(
                arguments[7],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int parentProcessId)
            || parentProcessId <= 0
            || !string.Equals(
                arguments[7],
                parentProcessId.ToString(CultureInfo.InvariantCulture),
                StringComparison.Ordinal))
        {
            throw InvalidArguments();
        }

        InstallerMachineHelperInvocation? invocation =
            InstallerMachineHelperInvocation.Parse(arguments.Take(6).ToArray());
        if (invocation is null)
        {
            throw InvalidArguments();
        }

        return Create(invocation, parentProcessId);
    }

    /// <summary>Validates both command identity and the expected parent process identity.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Invocation);
        Invocation.Validate();
        if (ParentProcessId <= 0)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.parent_process_invalid");
        }
    }

    /// <summary>Returns the exact eight path-free arguments accepted by the helper process.</summary>
    public IReadOnlyList<string> ToArguments()
    {
        Validate();
        return
        [
            .. Invocation.ToArguments(),
            ParentProcessOption,
            ParentProcessId.ToString(CultureInfo.InvariantCulture),
        ];
    }

    private static InstallerProtocolException InvalidArguments() =>
        new("installer.machine_helper.arguments_invalid");
}
