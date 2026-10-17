using System.ComponentModel.DataAnnotations;

namespace AngularCrudApi.Models;

/// <summary>
/// Body of POST /api/employees and PUT /api/employees/{id}.
/// Messages are shown verbatim under the matching form field in the React app.
/// Cross-field rules (DOB not in the future, location consistency, unique email) live in EmployeeEndpoints.
/// </summary>
public class EmployeeRequest
{
    [Required(ErrorMessage = "First name is required"), StringLength(50, ErrorMessage = "Max 50 characters")]
    public string FirstName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Last name is required"), StringLength(50, ErrorMessage = "Max 50 characters")]
    public string LastName { get; set; } = string.Empty;

    [Required(ErrorMessage = "Date of birth is required")]
    public DateOnly? DateOfBirth { get; set; }

    [Required(ErrorMessage = "Email is required"), EmailAddress(ErrorMessage = "Enter a valid email address"), StringLength(100, ErrorMessage = "Max 100 characters")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Select a gender"), RegularExpression("^(Male|Female)$", ErrorMessage = "Select a gender")]
    public string Gender { get; set; } = string.Empty;

    [Range(1, int.MaxValue, ErrorMessage = "Select a country")]
    public int CountryId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Select a state")]
    public int StateId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Select a city")]
    public int CityId { get; set; }

    [Required(ErrorMessage = "Address is required"), StringLength(100, ErrorMessage = "Max 100 characters")]
    public string Address { get; set; } = string.Empty;

    [Required(ErrorMessage = "Pincode is required"), RegularExpression(@"^\d{6}$", ErrorMessage = "Pincode must be 6 digits")]
    public string Pincode { get; set; } = string.Empty;
}
