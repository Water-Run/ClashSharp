namespace ClashSharp.Diagnostics;

/// <summary>Normalizes untrusted runtime log text at presentation and transport boundaries.</summary>
public static class RuntimeLogText
{
    /// <summary>Gets the maximum number of UTF-16 characters retained for one message.</summary>
    public const int MaximumCharacters = 4096;

    /// <summary>Returns bounded, control-free text without splitting a surrogate pair.</summary>
    public static string Normalize(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        int capacity = Math.Min(message.Length, MaximumCharacters);
        Span<char> buffer = stackalloc char[MaximumCharacters];
        int sourceIndex = 0;
        int destinationIndex = 0;
        while (sourceIndex < message.Length && destinationIndex < capacity)
        {
            char character = message[sourceIndex++];
            if (char.IsHighSurrogate(character))
            {
                if (sourceIndex < message.Length && char.IsLowSurrogate(message[sourceIndex]))
                {
                    if (destinationIndex + 1 >= capacity)
                    {
                        break;
                    }

                    buffer[destinationIndex++] = character;
                    buffer[destinationIndex++] = message[sourceIndex++];
                }
                else
                {
                    buffer[destinationIndex++] = '\uFFFD';
                }

                continue;
            }

            buffer[destinationIndex++] = char.IsLowSurrogate(character)
                ? '\uFFFD'
                : char.IsControl(character) || IsDirectionControl(character) ? ' ' : character;
        }

        return new string(buffer[..destinationIndex]).Trim();
    }

    private static bool IsDirectionControl(char character) =>
        character is '\u061C' or '\u200E' or '\u200F'
            or (>= '\u202A' and <= '\u202E')
            or (>= '\u2066' and <= '\u2069');
}
