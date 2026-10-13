namespace ClashSharp.Model;

/// <summary>Controls whether transfer rates are shown as bytes or bits per second.</summary>
public enum RateUnit
{
    /// <summary>Displays bytes per second.</summary>
    Bytes,
    /// <summary>Displays bits per second, multiplying the byte rate by eight.</summary>
    Bits,
}
