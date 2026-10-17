using System.ComponentModel.DataAnnotations;

namespace AngularCrudApi.Models;

/// <summary>Body of POST /api/employees/bulk-delete.</summary>
public class DeleteEmployeesRequest
{
    [Required, MinLength(1, ErrorMessage = "Select at least one employee"), MaxLength(500, ErrorMessage = "At most 500 employees per request")]
    public List<int> Ids { get; set; } = new();
}
