using AngularCrudApi.Auth;
using AngularCrudApi.Data;
using AngularCrudApi.Models;
using Microsoft.EntityFrameworkCore;

namespace AngularCrudApi.Endpoint;

/// <summary>
/// Read-only lookups for dropdowns and cascades. Every list returns [{ id, name }] ordered by name.
/// Any signed-in user may read them (the employee form needs them); managing the data is done
/// through the permission-protected resources /api/countries, /api/states and /api/cities.
///   GET /api/lookups/countries
///   GET /api/lookups/countries/{countryId}/states
///   GET /api/lookups/states/{stateId}/cities
///   GET /api/lookups/permissions -> { scopes }   every grantable permission (Users and Roles editors)
/// </summary>
public static class LookupEndpoints
{
    public static void MapLookupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/lookups").WithTags("Lookups").RequireAuthorization();

        group.MapGet("permissions", () => Results.Ok(new PermissionCatalogResponse(Permissions.All)));

        group.MapGet("countries", async (AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Countries.AsNoTracking()
                .OrderBy(c => c.CountryName)
                .Select(c => new LookupItem(c.CountryId, c.CountryName))
                .ToListAsync(ct)));

        group.MapGet("countries/{countryId:int}/states", async (int countryId, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.States.AsNoTracking()
                .Where(s => s.CountryId == countryId)
                .OrderBy(s => s.StateName)
                .Select(s => new LookupItem(s.StateId, s.StateName))
                .ToListAsync(ct)));

        group.MapGet("states/{stateId:int}/cities", async (int stateId, AppDbContext db, CancellationToken ct) =>
            Results.Ok(await db.Cities.AsNoTracking()
                .Where(c => c.StateId == stateId)
                .OrderBy(c => c.CityName)
                .Select(c => new LookupItem(c.CityId, c.CityName))
                .ToListAsync(ct)));
    }
}
