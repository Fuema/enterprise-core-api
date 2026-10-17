namespace AngularCrudApi.Models;

/// <summary>
/// Wire contract of an employee (template convention: `id`, `email`; lookup names denormalised).
/// A class with init-only properties (not a positional record) so EF Core can keep composing
/// Where/OrderBy/Skip/Take on the projection and translate everything to SQL.
/// </summary>
public sealed class EmployeeResponse
{
    public int Id { get; init; }
    public string FirstName { get; init; } = string.Empty;
    public string LastName { get; init; } = string.Empty;
    public DateOnly DateOfBirth { get; init; }
    public string Email { get; init; } = string.Empty;
    public string Gender { get; init; } = string.Empty;
    public int CountryId { get; init; }
    public int StateId { get; init; }
    public int CityId { get; init; }
    public string Address { get; init; } = string.Empty;
    public string Pincode { get; init; } = string.Empty;
    public string CountryName { get; init; } = string.Empty;
    public string StateName { get; init; } = string.Empty;
    public string CityName { get; init; } = string.Empty;
}
