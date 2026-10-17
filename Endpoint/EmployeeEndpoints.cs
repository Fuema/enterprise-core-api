using AngularCrudApi.Auth;
using AngularCrudApi.Data;
using AngularCrudApi.Infrastructure;
using AngularCrudApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace AngularCrudApi.Endpoint;

/// <summary>
/// Employees resource (template contract):
///   GET    /api/employees?page=&amp;pageSize=&amp;search=&amp;sort=&amp;gender=&amp;countryId=   employees:read
///   GET    /api/employees/{id}                                                  employees:read
///   POST   /api/employees              -> 201 + entity                          employees:write
///   PUT    /api/employees/{id}         -> 200 + entity                          employees:write
///   DELETE /api/employees/{id}         -> 204                                   employees:delete
///   POST   /api/employees/bulk-delete  { ids } -> { deletedCount }              employees:delete
/// Every error is ProblemDetails; validation errors carry camelCase field keys.
/// </summary>
public static class EmployeeEndpoints
{
    private const string DuplicateEmailMessage = "This email is already in use";

    /// <summary>Sortable fields. Names MUST match EMPLOYEE_SORTABLE_FIELDS in the React app.</summary>
    private static readonly SortMap<EmployeeResponse> Sorts = new SortMap<EmployeeResponse>()
        .Add("id", e => e.Id)
        .Add("firstName", e => e.FirstName)
        .Add("lastName", e => e.LastName)
        .Add("email", e => e.Email)
        .Add("dateOfBirth", e => e.DateOfBirth)
        .Add("gender", e => e.Gender)
        .Add("countryName", e => e.CountryName)
        .Add("stateName", e => e.StateName)
        .Add("cityName", e => e.CityName)
        .Add("pincode", e => e.Pincode);

    public static void MapEmployeeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/employees")
            .WithTags("Employees")
            .RequireAuthorization(Permissions.EmployeesRead);

        group.MapGet("", GetPage);
        group.MapGet("{id:int}", GetById).WithName("GetEmployeeById");
        group.MapPost("", Create).RequireAuthorization(Permissions.EmployeesWrite).WithValidation<EmployeeRequest>();
        group.MapPut("{id:int}", Update).RequireAuthorization(Permissions.EmployeesWrite).WithValidation<EmployeeRequest>();
        group.MapDelete("{id:int}", Delete).RequireAuthorization(Permissions.EmployeesDelete);
        group.MapPost("bulk-delete", BulkDelete).RequireAuthorization(Permissions.EmployeesDelete).WithValidation<DeleteEmployeesRequest>();
    }

    private static async Task<IResult> GetPage(
        [AsParameters] PageRequest paging, string? gender, int? countryId, AppDbContext db, CancellationToken ct)
    {
        var query = Project(db, db.Employees);

        foreach (var term in paging.SearchTerms)
        {
            query = query.Where(e =>
                e.FirstName.Contains(term) || e.LastName.Contains(term) || e.Email.Contains(term) ||
                e.CityName.Contains(term) || e.StateName.Contains(term) || e.CountryName.Contains(term) ||
                e.Pincode.Contains(term));
        }
        if (!string.IsNullOrWhiteSpace(gender)) query = query.Where(e => e.Gender == gender);
        if (countryId is > 0) query = query.Where(e => e.CountryId == countryId);

        // Explicit tie-breaker: stable paging when the sort column has duplicates.
        var ordered = Sorts.Apply(query, paging.Sort, "lastName.asc").ThenBy(e => e.Id);
        return Results.Ok(await ordered.ToPagedAsync(paging, ct));
    }

    private static async Task<IResult> GetById(int id, AppDbContext db, CancellationToken ct)
    {
        var employee = await FindResponseAsync(db, id, ct);
        return employee is null ? NotFound() : Results.Ok(employee);
    }

    private static async Task<IResult> Create([FromBody] EmployeeRequest request, AppDbContext db, CancellationToken ct)
    {
        var errors = await ValidateRulesAsync(request, exceptId: null, db, ct);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        var employee = new Employee();
        Apply(request, employee);
        db.Employees.Add(employee);
        if (await TrySaveAsync(db, ct) is { } conflict) return conflict;

        var response = await FindResponseAsync(db, employee.EmpId, ct);
        return Results.CreatedAtRoute("GetEmployeeById", new { id = employee.EmpId }, response);
    }

    private static async Task<IResult> Update(int id, [FromBody] EmployeeRequest request, AppDbContext db, CancellationToken ct)
    {
        var employee = await db.Employees.FindAsync([id], ct);
        if (employee is null) return NotFound();

        var errors = await ValidateRulesAsync(request, exceptId: id, db, ct);
        if (errors.Count > 0) return Results.ValidationProblem(errors);

        Apply(request, employee);
        if (await TrySaveAsync(db, ct) is { } conflict) return conflict;

        // 200 + the saved entity: the client writes it straight into its cache (no refetch).
        return Results.Ok(await FindResponseAsync(db, id, ct));
    }

    private static async Task<IResult> Delete(int id, AppDbContext db, CancellationToken ct)
    {
        var deleted = await db.Employees.Where(e => e.EmpId == id).ExecuteDeleteAsync(ct);
        return deleted == 0 ? NotFound() : Results.NoContent();
    }

    private static async Task<IResult> BulkDelete([FromBody] DeleteEmployeesRequest request, AppDbContext db, CancellationToken ct)
    {
        var ids = request.Ids.Distinct().ToList();
        var deletedCount = await db.Employees.Where(e => ids.Contains(e.EmpId)).ExecuteDeleteAsync(ct);
        return Results.Ok(new DeleteEmployeesResponse(deletedCount));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    /// <summary>Employee + lookup names via LEFT JOINs (rows with broken references still show).</summary>
    private static IQueryable<EmployeeResponse> Project(AppDbContext db, IQueryable<Employee> employees) =>
        from emp in employees.AsNoTracking()
        join country in db.Countries on emp.CountryId equals country.CountryId into countryGroup
        from c in countryGroup.DefaultIfEmpty()
        join state in db.States on emp.StateId equals state.StateId into stateGroup
        from s in stateGroup.DefaultIfEmpty()
        join city in db.Cities on emp.CityId equals city.CityId into cityGroup
        from ci in cityGroup.DefaultIfEmpty()
        select new EmployeeResponse
        {
            Id = emp.EmpId,
            FirstName = emp.FirstName,
            LastName = emp.LastName,
            DateOfBirth = emp.DateOfBirth,
            Email = emp.EmailId,
            Gender = emp.Gender,
            CountryId = emp.CountryId,
            StateId = emp.StateId,
            CityId = emp.CityId,
            Address = emp.Address,
            Pincode = emp.Pincode,
            CountryName = c != null ? c.CountryName : "",
            StateName = s != null ? s.StateName : "",
            CityName = ci != null ? ci.CityName : "",
        };

    private static Task<EmployeeResponse?> FindResponseAsync(AppDbContext db, int id, CancellationToken ct) =>
        Project(db, db.Employees.Where(e => e.EmpId == id)).FirstOrDefaultAsync(ct);

    /// <summary>Rules DataAnnotations cannot express. Keys are form field names.</summary>
    private static async Task<Dictionary<string, string[]>> ValidateRulesAsync(
        EmployeeRequest request, int? exceptId, AppDbContext db, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>();

        if (request.DateOfBirth > DateOnly.FromDateTime(DateTime.Today))
        {
            errors["dateOfBirth"] = ["Date of birth cannot be in the future"];
        }

        var locationIsValid = await db.Cities.AnyAsync(c =>
            c.CityId == request.CityId && c.StateId == request.StateId && c.CountryId == request.CountryId, ct);
        if (!locationIsValid)
        {
            errors["cityId"] = ["The selected country, state and city do not match"];
        }

        var email = request.Email.Trim();
        var emailTaken = await db.Employees.AnyAsync(e => e.EmailId == email && e.EmpId != (exceptId ?? 0), ct);
        if (emailTaken)
        {
            errors["email"] = [DuplicateEmailMessage];
        }

        return errors;
    }

    /// <summary>Turns a unique-index violation (race between check and insert) into a field error.</summary>
    private static async Task<IResult?> TrySaveAsync(AppDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return null;
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return Results.ValidationProblem(ValidationErrors.Field("email", DuplicateEmailMessage));
        }
    }

    private static void Apply(EmployeeRequest request, Employee employee)
    {
        employee.FirstName = request.FirstName.Trim();
        employee.LastName = request.LastName.Trim();
        employee.DateOfBirth = request.DateOfBirth!.Value;
        employee.EmailId = request.Email.Trim();
        employee.Gender = request.Gender;
        employee.CountryId = request.CountryId;
        employee.StateId = request.StateId;
        employee.CityId = request.CityId;
        employee.Address = request.Address.Trim();
        employee.Pincode = request.Pincode;
    }

    private static IResult NotFound() =>
        Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Employee not found");
}
