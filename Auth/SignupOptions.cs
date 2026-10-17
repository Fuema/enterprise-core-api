namespace AngularCrudApi.Auth;

/// <summary>Bound from the "Signup" configuration section.</summary>
public sealed class SignupOptions
{
    public const string SectionName = "Signup";

    /// <summary>false -> POST /api/auth/register answers 404. Hide the UI with VITE_AUTH_SIGNUP_ENABLED=false.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Comma-separated roles of a self-registered account.</summary>
    public string DefaultRoles { get; set; } = "User";

    /// <summary>
    /// Comma-separated permission scopes of a self-registered account. Keep it minimal (least privilege);
    /// an administrator grants more by updating dbo.AppUsers.Permissions.
    /// </summary>
    public string DefaultPermissions { get; set; } = Permissions.EmployeesRead;
}
