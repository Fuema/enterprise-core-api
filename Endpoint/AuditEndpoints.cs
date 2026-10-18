using AngularCrudApi.Auth;
using AngularCrudApi.Data;
using AngularCrudApi.Infrastructure;
using AngularCrudApi.Models;
using Microsoft.EntityFrameworkCore;

namespace AngularCrudApi.Endpoint;

/// <summary>
/// Security audit log (read-only; entries are written by AuditService inside each change's transaction):
///   GET /api/audit?page=&amp;pageSize=&amp;search=&amp;sort=&amp;action=&amp;targetType=&amp;targetId=&amp;from=&amp;to=   audit:read
///   GET /api/audit/actions -> string[]   (values for the action filter)                          audit:read
/// </summary>
public static class AuditEndpoints
{
    private static readonly SortMap<AuditEntry> Sorts = new SortMap<AuditEntry>()
        .Add("id", a => a.Id)
        .Add("occurredAtUtc", a => a.OccurredAtUtc)
        .Add("action", a => a.Action)
        .Add("actorName", a => a.ActorName)
        .Add("targetName", a => a.TargetName);

    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/audit").WithTags("Audit").RequireAuthorization(Permissions.AuditRead);
        group.MapGet("", GetPage);
        group.MapGet("actions", () => Results.Ok(AuditActions.All));
    }

    private static async Task<IResult> GetPage(
        [AsParameters] PageRequest paging, string? action, string? targetType, string? targetId,
        DateTime? from, DateTime? to, AppDbContext db, CancellationToken ct)
    {
        var query = db.AuditLog.AsNoTracking();
        foreach (var term in paging.SearchTerms)
        {
            query = query.Where(a =>
                (a.ActorName != null && a.ActorName.Contains(term)) ||
                (a.TargetName != null && a.TargetName.Contains(term)) ||
                a.Action.Contains(term) ||
                (a.IpAddress != null && a.IpAddress.Contains(term)));
        }
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(a => a.Action == action);
        if (!string.IsNullOrWhiteSpace(targetType)) query = query.Where(a => a.TargetType == targetType);
        if (!string.IsNullOrWhiteSpace(targetId)) query = query.Where(a => a.TargetId == targetId);
        // Dates are inclusive calendar days in UTC: from=2026-10-01&to=2026-10-17.
        if (from is { } start) query = query.Where(a => a.OccurredAtUtc >= start.Date);
        if (to is { } end) query = query.Where(a => a.OccurredAtUtc < end.Date.AddDays(1));

        var page = await Sorts.Apply(query, paging.Sort, "occurredAtUtc.desc").ThenByDescending(a => a.Id)
            .Select(a => new AuditEntryResponse
            {
                Id = a.Id,
                OccurredAtUtc = a.OccurredAtUtc,
                ActorUserId = a.ActorUserId,
                ActorName = a.ActorName,
                Action = a.Action,
                TargetType = a.TargetType,
                TargetId = a.TargetId,
                TargetName = a.TargetName,
                Details = a.Details,
                IpAddress = a.IpAddress,
            })
            .ToPagedAsync(paging, ct);
        return Results.Ok(page);
    }
}
