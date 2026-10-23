namespace DoiQ.Core.Options;

/// <summary>
/// Optional settings for the user's actor, bound from the "Auth" configuration section. The password itself is
/// set in the app's first-run setup and stored in the database.
/// </summary>
public sealed class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>Name given to the user's actor when it is first created. The UI always shows the user as "You".</summary>
    public string UserName { get; set; } = "User";

    /// <summary>Initials given to the user's actor when it is first created.</summary>
    public string UserInitials { get; set; } = "U";
}
