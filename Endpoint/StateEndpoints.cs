using AngularCrudApi.Auth;
using AngularCrudApi.Data;
using AngularCrudApi.Infrastructure;
using AngularCrudApi.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AngularCrudApi.Endpoint;

/// <summary>
/// States resource (table StateMaster):
///   GET    /api/states?page=&amp;pageSize=&amp;search=&amp;sort=&amp;countryId=   states:read
///   GET    /api/states/{id}                                              states:read
///   POST   /api/states        { name, countryId } -> 201 + entity        states:write
///   PUT    /api/states/{id}   { name, countryId } -> 200 + entity        states:write
///   DELETE /api/states/{id}   -> 204; 409 while cities/employees use it  states:delete
/// </summary>
public static class StateEndpoints
{
    private static readonly SortMap<StateResponse> Sorts = new SortMap<StateResponse>()
        .Add("id", s => s.Id)
        .Add("name", s => s.Name)
        .Add("countryName", s => s.CountryName)
        .Add("cityCount", s => s.CityCount)
        .Add("employeeCount", s => s.EmployeeCount);

    public static void MapStateEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/states").WithTags("States").RequireAuthorization(Permissions.StatesRead);

        group.MapGet("", GetPage);
        group.MapGet("{id:int}", GetById).WithName("GetStateById");
        group.MapPost("", Create).RequireAuthorization(Permissions.StatesWrite).WithValidation<StateRequest>();
        group.MapPut("{id:int}", Update).RequireAuthorization(Permissions.StatesWrite).WithValidation<StateRequest>();
        group.MapDelete("{id:int}", Delete).RequireAuthorization(Permissions.StatesDelete);
    }

    private static async Task<IResult> GetPage(
        [AsParameters] PageRequest paging, int? countryId, AppDbContext db, CancellationToken ct)
    {
        var query = Project(db, db.States);
        foreach (var term in paging.SearchTerms)
        {
            query = query.Where(s => s.Name.Contains(term) || s.CountryName.Contains(term));
        }
        if (countryId is > 0) query = query.Where(s => s.CountryId == countryId);

        var ordered = Sorts.Apply(query, paging.Sort, "name.asc").ThenBy(s => s.Id);
        return Results.Ok(await ordered.ToPagedAsync(paging, ct));
    }

    private static async Task<IResult> GetById(int id, AppDbContext db, CancellationToken ct)
    {
        var state = await FindAsync(db, id, ct);
        return state is null ? NotFound() : Results.Ok(state);
    }

    private static async Task<IResult> Create([FromBody] StateRequest request, AppDbContext db, CancellationToken ct)
    {
        var name = request.Name.Trim();
        if (await ValidateAsync(db, name, request.CountryId, exceptId: 0, ct) is { } error) return error;

        var state = new State { StateName = name, CountryId = request.CountryId };
        db.States.Add(state);
        await db.SaveChangesAsync(ct);
        return Results.CreatedAtRoute("GetStateById", new { id = state.StateId }, await FindAsync(db, state.StateId, ct));
    }

    private static async Task<IResult> Update(int id, [FromBody] StateRequest request, AppDbContext db, CancellationToken ct)
    {
        var state = await db.States.FindAsync([id], ct);
        if (state is null) return NotFound();

        var name = request.Name.Trim();
        if (await ValidateAsync(db, name, request.CountryId, exceptId: id, ct) is { } error) return error;

        if (state.CountryId != request.CountryId)
        {
            // Cities and employees store the country too: moving would leave them inconsistent.
            var inUse = await db.Cities.AnyAsync(c => c.StateId == id, ct) || await db.Employees.AnyAsync(e => e.StateId == id, ct);
            if (inUse) return ApiResults.FieldError("countryId", "A state with cities or employees cannot move to another country");
        }

        state.StateName = name;
        state.CountryId = request.CountryId;
        await db.SaveChangesAsync(ct);
        return Results.Ok(await FindAsync(db, id, ct));
    }

    private static async Task<IResult> Delete(int id, AppDbContext db, CancellationToken ct)
    {
        var usage = await FindAsync(db, id, ct);
        if (usage is null) return NotFound();
        if (usage.CityCount > 0 || usage.EmployeeCount > 0)
        {
            return ApiResults.Conflict("State is in use",
                $"{usage.Name} has {ApiResults.Plural(usage.CityCount, "city", "cities")} and " +
                $"{ApiResults.Plural(usage.EmployeeCount, "employee", "employees")}. Remove or move them first.");
        }

        await db.States.Where(s => s.StateId == id).ExecuteDeleteAsync(ct);
        return Results.NoContent();
    }

    // -------------------------------------------------------------------------

    private static IQueryable<StateResponse> Project(AppDbContext db, IQueryable<State> states) =>
        from s in states.AsNoTracking()
        join country in db.Countries on s.CountryId equals country.CountryId into countryGroup
        from c in countryGroup.DefaultIfEmpty()
        select new StateResponse
        {
            Id = s.StateId,
            Name = s.StateName,
            CountryId = s.CountryId,
            CountryName = c != null ? c.CountryName : "",
            CityCount = db.Cities.Count(ci => ci.StateId == s.StateId),
            EmployeeCount = db.Employees.Count(e => e.StateId == s.StateId),
        };

    private static Task<StateResponse?> FindAsync(AppDbContext db, int id, CancellationToken ct) =>
        Project(db, db.States.Where(s => s.StateId == id)).FirstOrDefaultAsync(ct);

    /// <summary>Parent exists + name unique within the country.</summary>
    private static async Task<IResult?> ValidateAsync(AppDbContext db, string name, int countryId, int exceptId, CancellationToken ct)
    {
        if (!await db.Countries.AnyAsync(c => c.CountryId == countryId, ct))
        {
            return ApiResults.FieldError("countryId", "Country not found");
        }
        var taken = await db.States.AnyAsync(s => s.CountryId == countryId && s.StateName == name && s.StateId != exceptId, ct);
        return taken ? ApiResults.FieldError("name", "This country already has a state with this name") : null;
    }

    private static IResult NotFound() => ApiResults.NotFound("State not found");
}
