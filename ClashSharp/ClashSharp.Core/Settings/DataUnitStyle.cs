namespace ClashSharp.Model;

/// <summary>Controls the scale and labels used to display byte quantities.</summary>
public enum DataUnitStyle
{
    /// <summary>Uses decimal powers of 1000 with KB, MB and GB labels.</summary>
    Si,
    /// <summary>Uses binary powers of 1024 with KiB, MiB and GiB labels.</summary>
    Binary,
    /// <summary>Uses Windows-style KB, MB and GB labels for powers of 1024.</summary>
    BinaryWithDecimalLabels,
}
