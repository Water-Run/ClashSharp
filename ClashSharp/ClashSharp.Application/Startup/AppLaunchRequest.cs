namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Describes one process launch after framework activation reaches the application boundary.</summary>
public sealed record AppLaunchRequest
{
    /// <summary>Initializes a launch request.</summary>
    /// <param name="arguments">Command-line activation arguments; never null.</param>
    public AppLaunchRequest(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        Arguments = arguments;
    }

    /// <summary>Gets the command-line activation arguments.</summary>
    public string Arguments { get; }
}
