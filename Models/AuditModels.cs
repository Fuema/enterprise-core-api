namespace AngularCrudApi.Models;

/// <summary>Row of GET /api/audit. Details is the raw JSON written by AuditService.</summary>
public sealed class AuditEntryResponse
{
    public long Id { get; init; }
    public DateTime OccurredAtUtc { get; init; }
    public int? ActorUserId { get; init; }
    public string? ActorName { get; init; }
    public string Action { get; init; } = string.Empty;
    public string TargetType { get; init; } = string.Empty;
    public string? TargetId { get; init; }
    public string? TargetName { get; init; }
    public string? Details { get; init; }
    public string? IpAddress { get; init; }
}
