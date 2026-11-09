namespace ClashSharp.Installer.Windows.Tests;

/// <summary>
/// Real-world account and folder names that every path-deriving component must treat as ordinary
/// data: spaces, apostrophes, punctuation Windows permits, scripts that are not ASCII, surrogate
/// pairs, right-to-left text, combining marks and the 8.3 form Windows invents for long names.
/// </summary>
internal static class WindowsPathCorpus
{
    /// <summary>Directory leaf names that Windows can create for a user profile.</summary>
    internal static IReadOnlyList<string> ProfileLeaves { get; } =
    [
        "owner",
        "Jane Doe",
        "Jane  Doe",
        "O'Brien",
        "Smith (work)",
        "Smith [dev]",
        "R&D",
        "joe#1",
        "user@contoso.com",
        "a.b.c",
        ".hidden",
        "user_01-02",
        "100$",
        "!bang",
        "x^y",
        "a;b",
        "a,b",
        "a=b",
        "a+b",
        "张三",
        "李 四",
        "José María",
        "Ольга Иванова",
        "محمد علي",
        "שלום",
        "한국어 사용자",
        "ユーザー",
        "😀",
        "👨‍👩‍👧",
        "e\u0301",
        "ＵＳＥＲ",
        "\u200Fuser",
        new string('长', 100),
        new string('a', 200),
    ];

    /// <summary>Roots a profile can live under, including a relocated profile volume.</summary>
    internal static IReadOnlyList<string> ProfileParents { get; } =
    [
        @"C:\Users",
        @"c:\users",
        @"D:\Profiles",
        @"C:\Documents and Settings",
        @"Z:\Roaming Profiles\Staff",
    ];

    /// <summary>Forms a registry or caller may hand over that Windows would rewrite or reinterpret.</summary>
    internal static IReadOnlyList<string> NonCanonicalProfilePaths { get; } =
    [
        @"C:\Users\owner\..\owner",
        @"C:\Users\.\owner",
        @"C:\Users\owner\.",
        @"C:\Users\owner\..",
        @"C:\Users\\owner",
        @"C:\\Users\owner",
        "C:\\Users\\owner ",
        "C:\\Users\\owner.",
        "C:\\Users\\owner. ",
        "C:\\Users\\ owner\\ ",
        "C:/Users/owner",
        @"C:\Users/owner",
        @"C:Users\owner",
        @"\Users\owner",
        @"Users\owner",
        @".\owner",
        @"..\owner",
        @"\\server\share\owner",
        @"\\?\C:\Users\owner",
        @"\\?\UNC\server\share\owner",
        @"\\.\C:\Users\owner",
        @"\\?\Volume{01234567-89ab-cdef-0123-456789abcdef}\Users\owner",
        @"C:\Users\owner:stream",
        @"C:\Users\owner::$DATA",
        "C:\\Users\\ow\"ner",
        "C:\\Users\\ow*ner",
        "C:\\Users\\ow?ner",
        "C:\\Users\\ow<ner",
        "C:\\Users\\ow>ner",
        "C:\\Users\\ow|ner",
        "C:\\Users\\ow\tner",
        "C:\\Users\\ow\nner",
        "C:\\Users\\ow\0ner",
        "C:\\Users\\ow\u007Fner",
        "C:\\Users\\ow\u0085ner",
        "",
        " ",
        "C:",
        @"C:\",
        @"C:\\",
        "owner",
    ];
}
