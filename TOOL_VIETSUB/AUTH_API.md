# TOOL VIETSUB authentication and account API

## Runtime boundary

```text
React WebView UI
    -> strict native message contract
WinForms host
    -> HTTPS + Bearer access token
TOOL_VIETSUB_SERVER
    -> Entity Framework Core
SQL Server
```

The desktop app does not contain a SQL connection string. Access and refresh
tokens are never posted back into the WebView.

## First-time setup

1. Deploy `TOOL_VIETSUB_V1.sql`.
2. Deploy `TOOL_VIETSUB_AUTH_V2.sql`.
3. Deploy `TOOL_VIETSUB_REGISTRATION_V3.sql`.
4. Deploy `TOOL_VIETSUB_QUOTA_V4.sql`.
5. Configure the registration and SMTP secrets described below.
6. Start `TOOL_VIETSUB_SERVER` with the HTTPS launch profile.
7. Open `https://localhost:7198/Setup`.
8. Create the first administrator account.
9. Start `TOOL_VIETSUB_APP` and sign in or create a USER account.

The Setup page is available only while the database contains no existing user.
The first administrator receives the PRO plan. Passwords are hashed with the
ASP.NET Core password hasher before they are persisted.

For Development, the Server creates its JWT signing key in the current Windows
user's local application-data directory. Production must provide the signing
key through the `TOOL_VIETSUB_JWT_SIGNING_KEY` environment variable. Do not add
that value to an appsettings file or source control.

The App uses `https://localhost:7198/` by default. A deployment can override
this with `TOOL_VIETSUB_API_BASE_URL`; non-local URLs must use HTTPS.

## Registration secret configuration

Development values belong in .NET User Secrets for `TOOL_VIETSUB_SERVER`:

```powershell
dotnet user-secrets set "Registration:OtpSecret" "<random-secret-at-least-32-characters>" --project TOOL_VIETSUB/TOOL_VIETSUB_SERVER.csproj
dotnet user-secrets set "Smtp:User" "<smtp-account>" --project TOOL_VIETSUB/TOOL_VIETSUB_SERVER.csproj
dotnet user-secrets set "Smtp:Pass" "<smtp-app-password>" --project TOOL_VIETSUB/TOOL_VIETSUB_SERVER.csproj
```

Production must set `TOOL_VIETSUB_REGISTRATION_OTP_SECRET`,
`TOOL_VIETSUB_SMTP_USER`, and `TOOL_VIETSUB_SMTP_PASSWORD`. SMTP host, port,
STARTTLS, timeout, and sender name are non-secret settings in `appsettings.json`.
Never store an SMTP App Password, OTP secret, access token, or refresh token in
source control.

## Endpoints

| Method | Path | Authentication | Purpose |
| --- | --- | --- | --- |
| POST | `/api/v1/auth/login` | Anonymous | Create an access/refresh session |
| POST | `/api/v1/auth/register/start` | Anonymous | Validate registration and email a one-time code |
| POST | `/api/v1/auth/register/verify` | Anonymous | Verify OTP, create USER/FREE, and issue a session |
| POST | `/api/v1/auth/register/resend` | Anonymous | Rotate and resend the one-time code |
| POST | `/api/v1/auth/refresh` | Anonymous | Rotate a refresh token |
| POST | `/api/v1/auth/logout` | Bearer | Revoke the current session |
| GET | `/api/v1/account/me` | Bearer | Get the current account |
| GET | `/api/v1/account/entitlements` | Bearer | Get plan, quota, and features |
| POST | `/api/v1/usage/events` | Bearer | Record an idempotent usage event |
| GET | `/api/v1/usage/history` | Bearer | Get paged desktop usage history |
| POST | `/api/v1/projects` | Bearer | Create or idempotently synchronize a project |
| GET | `/api/v1/projects` | Bearer | List projects owned by the account |
| GET | `/api/v1/projects/{id}` | Bearer | Get one owned project |
| PATCH | `/api/v1/projects/{id}/name` | Bearer | Rename an owned project |
| POST | `/api/v1/usage/reservations` | Bearer | Reserve estimated processing minutes |
| POST | `/api/v1/usage/reservations/{id}/commit` | Bearer | Commit actual processing minutes |
| POST | `/api/v1/usage/reservations/{id}/release` | Bearer | Release held processing minutes |

OpenAPI is available at `/openapi/v1.json` in Development.

## Response envelope

Successful response:

```json
{
  "success": true,
  "data": {},
  "error": null,
  "traceId": "request-trace-id"
}
```

Failed response:

```json
{
  "success": false,
  "data": null,
  "error": {
    "code": "AUTH_INVALID_CREDENTIALS",
    "message": "Email hoặc mật khẩu không chính xác."
  },
  "traceId": "request-trace-id"
}
```

## Session rules

- Access token lifetime: 15 minutes.
- Refresh token lifetime: 30 days.
- Refresh tokens are random 512-bit values; SQL Server stores only SHA-256
  hashes.
- Every successful refresh creates a new session token and revokes the previous
  one.
- Reuse of a revoked refresh token revokes all active sessions for that user.
- Protected API calls validate that the referenced session is still active.
- App refresh tokens are encrypted for the current Windows user with DPAPI.
- Authentication endpoints are rate-limited per source IP.

## Registration rules

- Public registration always creates role `USER` with the active `FREE` plan;
  it can never create an administrator.
- OTP contains six cryptographically generated digits, expires after five
  minutes, and is stored only as a keyed HMAC-SHA256 hash.
- A challenge allows five verification attempts, three resends, and one resend
  per sixty seconds. It is bound to the originating desktop device.
- Account, subscription, authenticated session, and verified challenge are
  committed in one SQL transaction.
- The WinForms host stores refresh tokens with Windows DPAPI. Tokens and SMTP
  credentials are never sent into the WebView UI.
- Completed challenges are removed after seven days; pending expired challenges
  are marked `EXPIRED` by the background cleanup service.

## Usage idempotency

The App generates one GUID for each usage event. The Server stores that GUID as
the external request ID under provider `DESKTOP_APP`. Retrying the same event
returns success with `duplicate: true` and does not increase quota twice.

Quota reservation requests also use a GUID idempotency key. The Server serializes
quota changes per user, subtracts active held minutes from the displayed balance,
and expires abandoned reservations. The App persists pending commit/release state
and reconciles it when the project is opened again after a network failure.
