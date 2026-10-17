/*
  002 - Per-feature location permissions (countries/states/cities read|write|delete).
  Upgrades the demo "manager" seeded by 001-era builds; other accounts are left untouched.
  Idempotent: only rows still holding the old value are changed.
*/
UPDATE dbo.AppUsers
SET Permissions = N'employees:read,employees:write,countries:read,states:read,cities:read,cities:write'
WHERE Username = N'manager' AND Permissions = N'employees:read,employees:write';
GO
