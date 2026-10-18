namespace AngularCrudApi.Auth;

/// <summary>Bound from the "Signup" configuration section.</summary>
public sealed class SignupOptions
{
    public const string SectionName = "Signup";

    /// <summary>false -> POST /api/auth/register answers 404. Hide the UI with VITE_AUTH_SIGNUP_ENABLED=false.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Role assigned to a self-registered account. Created on first signup if it does not exist.</summary>
    public string DefaultRole { get; set; } = "User";

    /// <summary>
    /// Comma-separated permissions given to <see cref="DefaultRole"/> ONLY when the role is created by a signup.
    /// After that, manage the role on the Roles screen. Keep it minimal (least privilege).
    /// </summary>
    public string DefaultRolePermissions { get; set; } = Permissions.EmployeesRead;
}
