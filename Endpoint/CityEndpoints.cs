using AngularCrudApi.Auth;
using AngularCrudApi.Data;
using AngularCrudApi.Infrastructure;
using AngularCrudApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AngularCrudApi.Endpoint;

/// <summary>
/// Cities resource (table CityMaster):
///   GET    /api/cities?page=&amp;pageSize=&amp;search=&amp;sort=&amp;countryId=&amp;stateId=   cities:read
///   GET    /api/cities/{id}                                                        cities:read
///   POST   /api/cities        { name, stateId } -> 201 + entity                    cities:write
///   PUT    /api/cities/{id}   { name, stateId } -> 200 + entity                    cities:write
///   DELETE /api/cities/{id}   -> 204; 409 while employees use it                   cities:delete
/// The city's CountryId is always copied from its state.
/// </summary>
public static class CityEndpoints
{
    private static readonly SortMap<CityResponse> Sorts = new SortMap<CityResponse>()
        .Add("id", c => c.Id)
        .Add("name", c => c.Name)
        .Add("stateName", c => c.StateName)
        .Add("countryName", c => c.CountryName)
        .Add("employeeCount", c => c.EmployeeCount);

    public static void MapCityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/cities").WithTags("Cities").RequireAuthorization(Permissions.CitiesRead);

        group.MapGet("", GetPage);
        group.MapGet("{id:int}", GetById).WithName("GetCityById");
        group.MapPost("", Create).RequireAuthorization(Permissions.CitiesWrite).WithValidation<CityRequest>();
        group.MapPut("{id:int}", Update).RequireAuthorization(Permissions.CitiesWrite).WithValidation<CityRequest>();
        group.MapDelete("{id:int}", Delete).RequireAuthorization(Permissions.CitiesDelete);
    }

    private static async Task<IResult> GetPage(
        [AsParameters] PageRequest paging, int? countryId, int? stateId, AppDbContext db, CancellationToken ct)
    {
        var query = Project(db, db.Cities);
        foreach (var term in paging.SearchTerms)
        {
            query = query.Where(c => c.Name.Contains(term) || c.StateName.Contains(term) || c.CountryName.Contains(term));
        }
        if (countryId is > 0) query = query.Where(c => c.CountryId == countryId);
        if (stateId is > 0) query = query.Where(c => c.StateId == stateId);

        var ordered = Sorts.Apply(query, paging.Sort, "name.asc").ThenBy(c => c.Id);
        return Results.Ok(await ordered.ToPagedAsync(paging, ct));
    }

    private static async Task<IResult> GetById(int id, AppDbContext db, CancellationToken ct)
    {
        var city = await FindAsync(db, id, ct);
        return city is null ? NotFound() : Results.Ok(city);
    }

    private static async Task<IResult> Create([FromBody] CityRequest request, AppDbContext db, CancellationToken ct)
    {
        var name = request.Name.Trim();
        var state = await db.States.AsNoTracking().FirstOrDefaultAsync(s => s.StateId == request.StateId, ct);
        if (state is null) return ApiResults.FieldError("stateId", "State not found");
        if (await NameTakenAsync(db, name, request.StateId, exceptId: 0, ct)) return DuplicateName();

        var city = new City { CityName = name, StateId = state.StateId, CountryId = state.CountryId };
        db.Cities.Add(city);
        await db.SaveChangesAsync(ct);
        return Results.CreatedAtRoute("GetCityById", new { id = city.CityId }, await FindAsync(db, city.CityId, ct));
    }

    private static async Task<IResult> Update(int id, [FromBody] CityRequest request, AppDbContext db, CancellationToken ct)
    {
        var city = await db.Cities.FindAsync([id], ct);
        if (city is null) return NotFound();

        var name = request.Name.Trim();
        var state = await db.States.AsNoTracking().FirstOrDefaultAsync(s => s.StateId == request.StateId, ct);
        if (state is null) return ApiResults.FieldError("stateId", "State not found");
        if (await NameTakenAsync(db, name, request.StateId, exceptId: id, ct)) return DuplicateName();

        if (city.StateId != request.StateId && await db.Employees.AnyAsync(e => e.CityId == id, ct))
        {
            // Employees store state and country too: moving would leave them inconsistent.
            return ApiResults.FieldError("stateId", "A city with employees cannot move to another state");
        }

        city.CityName = name;
        city.StateId = state.StateId;
        city.CountryId = state.CountryId;
        await db.SaveChangesAsync(ct);
        return Results.Ok(await FindAsync(db, id, ct));
    }

    private static async Task<IResult> Delete(int id, AppDbContext db, CancellationToken ct)
    {
        var usage = await FindAsync(db, id, ct);
        if (usage is null) return NotFound();
        if (usage.EmployeeCount > 0)
        {
            return ApiResults.Conflict("City is in use",
                $"{usage.Name} has {ApiResults.Plural(usage.EmployeeCount, "employee", "employees")}. Move them first.");
        }

        await db.Cities.Where(c => c.CityId == id).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    // -------------------------------------------------------------------------

    private static IQueryable<CityResponse> Project(AppDbContext db, IQueryable<City> cities) =>
        from ci in cities.AsNoTracking()
        join state in db.States on ci.StateId equals state.StateId into stateGroup
        from s in stateGroup.DefaultIfEmpty()
        join country in db.Countries on ci.CountryId equals country.CountryId into countryGroup
        from c in countryGroup.DefaultIfEmpty()
        select new CityResponse
        {
            Id = ci.CityId,
            Name = ci.CityName,
            StateId = ci.StateId,
            StateName = s != null ? s.StateName : "",
            CountryId = ci.CountryId,
            CountryName = c != null ? c.CountryName : "",
            EmployeeCount = db.Employees.Count(e => e.CityId == ci.CityId),
        };

    private static Task<CityResponse?> FindAsync(AppDbContext db, int id, CancellationToken ct) =>
        Project(db, db.Cities.Where(c => c.CityId == id)).FirstOrDefaultAsync(ct);

    private static Task<bool> NameTakenAsync(AppDbContext db, string name, int stateId, int exceptId, CancellationToken ct) =>
        db.Cities.AnyAsync(c => c.StateId == stateId && c.CityName == name && c.CityId != exceptId, ct);

    private static IResult DuplicateName() => ApiResults.FieldError("name", "This state already has a city with this name");

    private static IResult NotFound() => ApiResults.NotFound("City not found");
}
