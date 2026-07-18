namespace ClashSharp.ApplicationModel.Processes;

/// <summary>Describes one bounded external process invocation.</summary>
public sealed class ProcessRequest
{
    /// <summary>Initializes an immutable process request.</summary>
    /// <param name="fileName">Executable name or path.</param>
    /// <param name="arguments">Individual arguments passed without command-line concatenation.</param>
    /// <param name="timeout">Positive maximum run duration.</param>
    /// <param name="workingDirectory">Optional working directory.</param>
    /// <param name="runElevated">Whether Windows shell elevation is required.</param>
    public ProcessRequest(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        string? workingDirectory = null,
        bool runElevated = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero || timeout == System.Threading.Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), timeout, "Process timeout must be positive and finite.");
        }

        string[] copiedArguments = arguments.ToArray();
        if (copiedArguments.Any(static argument => argument is null))
        {
            throw new ArgumentException("Process arguments cannot contain null values.", nameof(arguments));
        }

        FileName = fileName;
        Arguments = Array.AsReadOnly(copiedArguments);
        Timeout = timeout;
        WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
        RunElevated = runElevated;
    }

    /// <summary>Gets the executable name or path.</summary>
    public string FileName { get; }

    /// <summary>Gets the copied argument list.</summary>
    public IReadOnlyList<string> Arguments { get; }

    /// <summary>Gets the maximum run duration.</summary>
    public TimeSpan Timeout { get; }

    /// <summary>Gets the optional working directory.</summary>
    public string? WorkingDirectory { get; }

    /// <summary>Gets whether Windows shell elevation is required.</summary>
    public bool RunElevated { get; }
}
