using AngularCrudApi.Auth;
using AngularCrudApi.Data;
using AngularCrudApi.Infrastructure;
using AngularCrudApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AngularCrudApi.Endpoint;

/// <summary>
/// Countries resource (table CountryMaster):
///   GET    /api/countries?page=&amp;pageSize=&amp;search=&amp;sort=      countries:read
///   GET    /api/countries/{id}                                    countries:read
///   POST   /api/countries        { name } -> 201 + entity         countries:write
///   PUT    /api/countries/{id}   { name } -> 200 + entity         countries:write
///   DELETE /api/countries/{id}   -> 204; 409 while states/employees use it   countries:delete
/// </summary>
public static class CountryEndpoints
{
    private static readonly SortMap<CountryResponse> Sorts = new SortMap<CountryResponse>()
        .Add("id", c => c.Id)
        .Add("name", c => c.Name)
        .Add("stateCount", c => c.StateCount)
        .Add("employeeCount", c => c.EmployeeCount);

    public static void MapCountryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/countries").WithTags("Countries").RequireAuthorization(Permissions.CountriesRead);

        group.MapGet("", GetPage);
        group.MapGet("{id:int}", GetById).WithName("GetCountryById");
        group.MapPost("", Create).RequireAuthorization(Permissions.CountriesWrite).WithValidation<CountryRequest>();
        group.MapPut("{id:int}", Update).RequireAuthorization(Permissions.CountriesWrite).WithValidation<CountryRequest>();
        group.MapDelete("{id:int}", Delete).RequireAuthorization(Permissions.CountriesDelete);
    }

    private static async Task<IResult> GetPage([AsParameters] PageRequest paging, AppDbContext db, CancellationToken ct)
    {
        var query = Project(db, db.Countries);
        foreach (var term in paging.SearchTerms)
        {
            query = query.Where(c => c.Name.Contains(term));
        }
        var ordered = Sorts.Apply(query, paging.Sort, "name.asc").ThenBy(c => c.Id);
        return Results.Ok(await ordered.ToPagedAsync(paging, ct));
    }

    private static async Task<IResult> GetById(int id, AppDbContext db, CancellationToken ct)
    {
        var country = await FindAsync(db, id, ct);
        return country is null ? NotFound() : Results.Ok(country);
    }

    private static async Task<IResult> Create([FromBody] CountryRequest request, AppDbContext db, CancellationToken ct)
    {
        var name = request.Name.Trim();
        if (await NameTakenAsync(db, name, exceptId: 0, ct)) return DuplicateName();

        var country = new Country { CountryName = name };
        db.Countries.Add(country);
        await db.SaveChangesAsync(ct);
        return Results.CreatedAtRoute("GetCountryById", new { id = country.CountryId }, await FindAsync(db, country.CountryId, ct));
    }

    private static async Task<IResult> Update(int id, [FromBody] CountryRequest request, AppDbContext db, CancellationToken ct)
    {
        var country = await db.Countries.FindAsync([id], ct);
        if (country is null) return NotFound();

        var name = request.Name.Trim();
        if (await NameTakenAsync(db, name, exceptId: id, ct)) return DuplicateName();

        country.CountryName = name;
        await db.SaveChangesAsync(ct);
        return Results.Ok(await FindAsync(db, id, ct));
    }

    private static async Task<IResult> Delete(int id, AppDbContext db, CancellationToken ct)
    {
        var usage = await Project(db, db.Countries.Where(c => c.CountryId == id)).FirstOrDefaultAsync(ct);
        if (usage is null) return NotFound();
        if (usage.StateCount > 0 || usage.EmployeeCount > 0)
        {
            return ApiResults.Conflict("Country is in use",
                $"{usage.Name} has {ApiResults.Plural(usage.StateCount, "state", "states")} and " +
                $"{ApiResults.Plural(usage.EmployeeCount, "employee", "employees")}. Remove or move them first.");
        }

        await db.Countries.Where(c => c.CountryId == id).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    // -------------------------------------------------------------------------

    private static IQueryable<CountryResponse> Project(AppDbContext db, IQueryable<Country> countries) =>
        countries.AsNoTracking().Select(c => new CountryResponse
        {
            Id = c.CountryId,
            Name = c.CountryName,
            StateCount = db.States.Count(s => s.CountryId == c.CountryId),
            EmployeeCount = db.Employees.Count(e => e.CountryId == c.CountryId),
        });

    private static Task<CountryResponse?> FindAsync(AppDbContext db, int id, CancellationToken ct) =>
        Project(db, db.Countries.Where(c => c.CountryId == id)).FirstOrDefaultAsync(ct);

    private static Task<bool> NameTakenAsync(AppDbContext db, string name, int exceptId, CancellationToken ct) =>
        db.Countries.AnyAsync(c => c.CountryName == name && c.CountryId != exceptId, ct);

    private static IResult DuplicateName() => ApiResults.FieldError("name", "A country with this name already exists");

    private static IResult NotFound() => ApiResults.NotFound("Country not found");
}
