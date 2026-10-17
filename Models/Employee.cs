namespace AngularCrudApi.Models;

public class Employee
{
    public int EmpId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public DateOnly DateOfBirth { get; set; }
    public string EmailId { get; set; } = string.Empty;
    public string Gender { get; set; } = string.Empty;
    public int CountryId { get; set; }
    public int StateId { get; set; }
    public int CityId { get; set; }
    public string Address { get; set; } = string.Empty;
    public string Pincode { get; set; } = string.Empty;
}