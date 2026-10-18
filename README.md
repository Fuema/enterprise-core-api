# Enterprise Core API (AngularCrudApi)

ASP.NET Core 10 minimal API + SQL Server backend for the **enterprise-core-app** React template.
It implements the template's backend contract (see `agents.md` → *Backend Contract* in the frontend repo:
https://github.com/Fuema/enterprise-core-app). Contract changes are committed in both repositories.

## Run locally

```powershell
dotnet restore
dotnet build
dotnet run --launch-profile http        # http://localhost:7077  (Swagger: /swagger)
```

On the first Development run the API:

1. applies `Database/Scripts/*.sql` (idempotent): creates `AppUsers`, `RefreshTokens` and the unique
   index on `tblEmployeeMaster.EmailId` (skipped with a log warning if duplicates exist); script `003` adds
   roles, direct grants, account-security and session columns and `AuditLog`, and migrates the legacy
   comma-separated `AppUsers.Roles` / `Permissions` once (see *Authorization*);
2. seeds roles **Administrator** (system, `*`), **HR Manager** (employees read/write, countries + states read,
   cities read/write), **Auditor** (employees read) and **User** (employees read, signup default), and demo users
   when `AppUsers` is empty: **admin/admin** (Administrator), **manager/manager** (HR Manager),
   **viewer/viewer** (Auditor).

Both are controlled by `Database:ApplyBootstrapScripts` / `Database:SeedDevelopmentUsers`
(on in `appsettings.Development.json`, off by default). You can also run the script yourself in SSMS.

### JWT signing key

Development works without one (an ephemeral key is generated at startup). To keep tokens valid across
restarts, or for any other environment:

```powershell
dotnet user-secrets set "Jwt:SigningKey" "<at least 32 random characters>"
# production: environment variable Jwt__SigningKey or a vault — never appsettings.json
```

### Point the React app at this API

Create `.env.development.local` in the frontend (not committed) with:

```
VITE_API_BASE_URL=http://localhost:7077/api
VITE_ENABLE_MOCKS=false
```

## Endpoints

| Method | Route | Permission |
|---|---|---|
| POST | `/api/auth/register` `{username,displayName,email,password,confirmPassword}` → 201 `{accessToken,user}` + refresh cookie | anonymous (rate-limited; `Signup:Enabled`) |
| POST | `/api/auth/login` `{username,password}` → `{accessToken,user}` + refresh cookie | anonymous (rate-limited 10/min/IP) |
| POST | `/api/auth/refresh` (cookie) → `{accessToken}`, cookie rotated | anonymous |
| GET | `/api/auth/me` → `{id,displayName,email,roles[],permissions[],mustChangePassword}` (effective permissions) | signed in |
| POST | `/api/auth/logout` → 204 (ends the session) | anonymous |
| POST | `/api/auth/change-password` `{currentPassword,newPassword,confirmPassword}` → `{accessToken,user,otherSessionsSignedOut}` | signed in (allowed while `mustChangePassword`) |
| GET / DELETE | `/api/auth/sessions[/{sessionId}]` → `[{id,startedAtUtc,lastSeenAtUtc,expiresAtUtc,ipAddress,userAgent,isCurrent}]` | signed in |
| GET | `/api/employees?page&pageSize&search&sort=lastName.asc,dateOfBirth.desc&gender&countryId` → `{data,totalCount,pageSize,currentPage}` | `employees:read` |
| GET | `/api/employees/{id}` | `employees:read` |
| POST | `/api/employees` → 201 + entity | `employees:write` |
| PUT | `/api/employees/{id}` → 200 + entity | `employees:write` |
| DELETE | `/api/employees/{id}` → 204 | `employees:delete` |
| POST | `/api/employees/bulk-delete` `{ids}` → `{deletedCount}` | `employees:delete` |
| GET | `/api/users?page&pageSize&search&sort&isActive&roleId` → `{id,username,displayName,email,isActive,lockoutEndUtc,mustChangePassword,lastLoginAtUtc,createdAtUtc,roles[{id,name,isSystem}],directPermissionCount}` | `users:read` |
| GET | `/api/users/{id}` (+ `directPermissions`, `effectivePermissions`, lockout fields), `/api/users/role-options`, `/api/users/{id}/sessions` | `users:read` |
| POST | `/api/users` `{username,displayName,email,roleIds[],directPermissions[]}` → 201 `{user,temporaryPassword}` | `users:write` |
| PUT | `/api/users/{id}` `{displayName,email,roleIds[],directPermissions[],isActive}` → 200 + user | `users:write` |
| POST | `/api/users/{id}/reset-password` → `{temporaryPassword}` (409 for yourself), `/api/users/{id}/unlock` → 204 | `users:write` |
| DELETE | `/api/users/{id}/sessions[/{sessionId}]` | `users:write` |
| GET / GET {id} | `/api/roles?page&pageSize&search&sort` → `{id,name,description,isSystem,permissions[],userCount}`; detail adds `members` | `roles:read` |
| POST / PUT / DELETE | `/api/roles[/{id}]` `{name,description,permissions[]}`; system role read-only; delete → 409 while assigned | `roles:write` / `roles:delete` |
| GET | `/api/audit?page&pageSize&sort&action&targetType&targetId&from&to`, `/api/audit/actions` | `audit:read` |
| GET | `/api/lookups/permissions` → `{scopes}` (grantable catalog) | signed in |
| GET | `/api/lookups/countries`, `/api/lookups/countries/{id}/states`, `/api/lookups/states/{id}/cities` → `[{id,name}]` (dropdowns) | signed in |
| GET / GET {id} | `/api/countries?page&pageSize&search&sort` → `{id,name,stateCount,employeeCount}` | `countries:read` |
| POST / PUT / DELETE | `/api/countries[/{id}]` `{name}`; delete → 409 while states or employees use it | `countries:write` / `countries:delete` |
| GET / GET {id} | `/api/states?…&countryId` → `{id,name,countryId,countryName,cityCount,employeeCount}` | `states:read` |
| POST / PUT / DELETE | `/api/states[/{id}]` `{name,countryId}`; cannot change country while it has cities/employees; delete → 409 while in use | `states:write` / `states:delete` |
| GET / GET {id} | `/api/cities?…&countryId&stateId` → `{id,name,stateId,stateName,countryId,countryName,employeeCount}` | `cities:read` |
| POST / PUT / DELETE | `/api/cities[/{id}]` `{name,stateId}` (country copied from the state); delete → 409 while employees use it | `cities:write` / `cities:delete` |

Names are unique per parent (country name globally, state per country, city per state) → field error on `name`.

## Authorization

**Model.** `Roles` → `RolePermissions`; users get roles through `UserRoles` and extra permissions through
`UserPermissions` (direct grants). Effective permissions = union of both (`Auth/AccessService.cs`).
**Administrator** is a system role holding `*`: it cannot be renamed, emptied or deleted, and the API refuses any
change that would leave no active administrator. Nobody can deactivate themselves or remove their own `users:write`.

**Immediate effect.** Every access change (roles, grants, role permissions, deactivation, password, session revocation)
rotates `AppUsers.SecurityStamp`. Access tokens carry `stamp` and `sid`; `Auth/SessionValidator.cs` checks them on each
request (cached `Security:ValidationCacheSeconds`, evicted explicitly on change), so a stale token gets 401 and the
client's silent refresh returns a token with the new permissions.

**Account security** (`Security` section): lockout after `MaxFailedLoginAttempts` (5) failures for `LockoutMinutes`
(15); administrators can unlock. Passwords: 8+ characters with upper, lower and digit (Identity hashes). An admin
password reset returns a one-time temporary password (`TemporaryPasswordLength`) and sets `MustChangePassword`: until
the user changes it, every permission-protected endpoint is refused. Changing the password signs out other sessions.

**Sessions.** All refresh tokens of one sign-in share a `SessionId` (IP and user agent recorded). Users see and end
their own at `/api/auth/sessions`; administrators at `/api/users/{id}/sessions`.

**Audit log.** `AuditLog` (append-only, no FKs) records sign-in success/failure/lockout, signup, password changes and
resets, unlocks, session revocations and user/role create/update/delete, with actor, target, JSON details and IP.

**Migration (script 003).** On an existing database the legacy comma-separated columns are migrated once
(recorded in `SchemaMigrations` as `003_rbac_data`): a role is created per role name in use, it receives the
permissions **all** its members had, and whatever a user's roles do not cover becomes a direct grant, so effective
access is unchanged. The legacy columns stay in the table (no longer read) for rollback.

### Signup
`Signup` section: `Enabled` (default `true`), `DefaultRole` (`User`), `DefaultRolePermissions` (`employees:read`,
used only if signup has to create the role). New accounts are signed in immediately with that least-privilege role;
administrators grant more from the user screen.

Permissions support wildcards: `employees:*`, `*`. Errors are RFC 7807 ProblemDetails; validation
errors use camelCase keys (`errors.email`, `errors.cityId`...) that the React form maps onto fields.

## Adding a resource

1. Entity in `Models/`, `DbSet` + table mapping in `Data/AppDbContext.cs`, schema change as a new
   idempotent `Database/Scripts/00N_*.sql`.
2. Request class with DataAnnotations (messages are shown to users) and a response class with
   `init` properties (not a positional record, so EF can sort/page the projection).
3. `Endpoint/<Resource>Endpoints.cs`: `MapGroup("/api/<resources>").RequireAuthorization(<read>)`,
   `.WithValidation<TRequest>()` on bodies, a `SortMap<TResponse>` allow-list and `ToPagedAsync`.
4. Add the permission scopes to `Auth/Permissions.cs` (`All`); grant them to roles from the Roles screen
   (they appear in `/api/lookups/permissions` automatically).

