using System.ComponentModel.DataAnnotations;

namespace AngularCrudApi.Models;

/// <summary>Generic id/name pair returned by every /api/lookups endpoint (feeds the React LookupSelect).</summary>
public sealed record LookupItem(int Id, string Name);

// ---------------------------------------------------------------------------
// Countries / States / Cities management (paged CRUD resources)
// Response DTOs are classes with init properties so EF can sort and page the projection.
// ---------------------------------------------------------------------------

public class CountryRequest
{
    [Required(ErrorMessage = "Name is required"), StringLength(100, ErrorMessage = "Max 100 characters")]
    public string Name { get; set; } = string.Empty;
}

public sealed class CountryResponse
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int StateCount { get; init; }
    public int EmployeeCount { get; init; }
}

public class StateRequest
{
    [Required(ErrorMessage = "Name is required"), StringLength(100, ErrorMessage = "Max 100 characters")]
    public string Name { get; set; } = string.Empty;

    [Range(1, int.MaxValue, ErrorMessage = "Select a country")]
    public int CountryId { get; set; }
}

public sealed class StateResponse
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int CountryId { get; init; }
    public string CountryName { get; init; } = string.Empty;
    public int CityCount { get; init; }
    public int EmployeeCount { get; init; }
}

public class CityRequest
{
    [Required(ErrorMessage = "Name is required"), StringLength(100, ErrorMessage = "Max 100 characters")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Only the state is sent; the city's country is always the state's country.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Select a state")]
    public int StateId { get; set; }
}

public sealed class CityResponse
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public int StateId { get; init; }
    public string StateName { get; init; } = string.Empty;
    public int CountryId { get; init; }
    public string CountryName { get; init; } = string.Empty;
    public int EmployeeCount { get; init; }
}
