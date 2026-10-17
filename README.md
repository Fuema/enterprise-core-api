# Enterprise Core API (AngularCrudApi)

ASP.NET Core 10 minimal API + SQL Server backend for the **enterprise-core-app** React template.
It implements the template's backend contract (see `agents.md` → *Backend Contract* in the frontend repo).

## Run locally

```powershell
dotnet restore
dotnet build
dotnet run --launch-profile http        # http://localhost:7077  (Swagger: /swagger)
```

On the first Development run the API:

1. applies `Database/Scripts/*.sql` (idempotent): creates `AppUsers`, `RefreshTokens` and the unique
   index on `tblEmployeeMaster.EmailId` (skipped with a log warning if duplicates exist);
2. seeds demo users when `AppUsers` is empty: **admin/admin** (`*`), **manager/manager** (employees read/write,
   countries + states read, cities read/write), **viewer/viewer** (employees read). Script `002` upgrades a manager
   seeded by an earlier build.

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
| GET | `/api/auth/me` | signed in |
| POST | `/api/auth/logout` → 204 | anonymous |
| GET | `/api/employees?page&pageSize&search&sort=lastName.asc,dateOfBirth.desc&gender&countryId` → `{data,totalCount,pageSize,currentPage}` | `employees:read` |
| GET | `/api/employees/{id}` | `employees:read` |
| POST | `/api/employees` → 201 + entity | `employees:write` |
| PUT | `/api/employees/{id}` → 200 + entity | `employees:write` |
| DELETE | `/api/employees/{id}` → 204 | `employees:delete` |
| POST | `/api/employees/bulk-delete` `{ids}` → `{deletedCount}` | `employees:delete` |
| GET | `/api/lookups/countries`, `/api/lookups/countries/{id}/states`, `/api/lookups/states/{id}/cities` → `[{id,name}]` (dropdowns) | signed in |
| GET / GET {id} | `/api/countries?page&pageSize&search&sort` → `{id,name,stateCount,employeeCount}` | `countries:read` |
| POST / PUT / DELETE | `/api/countries[/{id}]` `{name}`; delete → 409 while states or employees use it | `countries:write` / `countries:delete` |
| GET / GET {id} | `/api/states?…&countryId` → `{id,name,countryId,countryName,cityCount,employeeCount}` | `states:read` |
| POST / PUT / DELETE | `/api/states[/{id}]` `{name,countryId}`; cannot change country while it has cities/employees; delete → 409 while in use | `states:write` / `states:delete` |
| GET / GET {id} | `/api/cities?…&countryId&stateId` → `{id,name,stateId,stateName,countryId,countryName,employeeCount}` | `cities:read` |
| POST / PUT / DELETE | `/api/cities[/{id}]` `{name,stateId}` (country copied from the state); delete → 409 while employees use it | `cities:write` / `cities:delete` |

Names are unique per parent (country name globally, state per country, city per state) → field error on `name`.

### Signup
`Signup` section: `Enabled` (default `true`), `DefaultRoles` (`User`), `DefaultPermissions` (`employees:read`).
New accounts are signed in immediately with those least-privilege scopes; an administrator grants more by
updating `dbo.AppUsers.Permissions` (e.g. `countries:*,states:*,cities:*`). Passwords: 8+ characters with upper,
lower and digit; stored as ASP.NET Core Identity hashes.

Permissions support wildcards: `employees:*`, `*`. Errors are RFC 7807 ProblemDetails; validation
errors use camelCase keys (`errors.email`, `errors.cityId`...) that the React form maps onto fields.

## Adding a resource

1. Entity in `Models/`, `DbSet` + table mapping in `Data/AppDbContext.cs`, schema change as a new
   idempotent `Database/Scripts/00N_*.sql`.
2. Request class with DataAnnotations (messages are shown to users) and a response class with
   `init` properties (not a positional record, so EF can sort/page the projection).
3. `Endpoint/<Resource>Endpoints.cs`: `MapGroup("/api/<resources>").RequireAuthorization(<read>)`,
   `.WithValidation<TRequest>()` on bodies, a `SortMap<TResponse>` allow-list and `ToPagedAsync`.
4. Add the permission scopes to `Auth/Permissions.cs` (`All`) and to the users that need them.

`_original/` holds the pre-adaptation files for reference; it is not compiled and can be deleted.
