namespace DoiQ.Core.Services;

/// <summary>Checks for enum values that arrive from outside (JSON bodies, query strings, tool arguments).</summary>
public static class EnumInput
{
    /// <summary>The value itself when it is a named member of the enum; otherwise a ValidationException (HTTP 400).</summary>
    public static T Require<T>(T value, string what) where T : struct, Enum =>
        Enum.IsDefined(value)
            ? value
            : throw new ValidationException($"Unknown {what} '{(int)(object)value}'. Use {Names<T>()}.");

    /// <summary>Parses a member name (case-insensitive). Numbers, including numeric strings such as "2", are not names and are rejected.</summary>
    public static T Parse<T>(string? text, string what) where T : struct, Enum
    {
        var s = text?.Trim();
        if (!string.IsNullOrEmpty(s) && !char.IsDigit(s[0]) && s[0] is not ('-' or '+') && !s.Contains(',')
            && Enum.TryParse<T>(s, ignoreCase: true, out var v) && Enum.IsDefined(v))
            return v;
        throw new ValidationException($"Unknown {what} '{text}'. Use {Names<T>()}.");
    }

    /// <summary>"New, Waiting, Doing or Done".</summary>
    private static string Names<T>() where T : struct, Enum
    {
        var names = Enum.GetNames<T>();
        return names.Length < 2 ? string.Concat(names) : $"{string.Join(", ", names[..^1])} or {names[^1]}";
    }
}
