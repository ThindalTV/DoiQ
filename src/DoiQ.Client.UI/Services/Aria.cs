namespace DoiQ.Client.Services;

/// <summary>
/// Blazor renders a bool attribute value as a bare attribute (true) or drops it (false). ARIA states such as
/// aria-pressed, aria-expanded and aria-checked need the literal strings "true" and "false".
/// </summary>
public static class Aria
{
    public static string Bool(bool value) => value ? "true" : "false";
}
