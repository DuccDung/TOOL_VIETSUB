# TOOL_VIETSUB - Authentication and account API checklist

Scope: secure communication between `TOOL_VIETSUB_APP` and
`TOOL_VIETSUB_SERVER`. Video processing remains local to the desktop app.

## Foundation and database

- [x] AUTH-01 Confirm the App -> HTTPS API -> Server -> SQL boundary.
- [x] AUTH-02 Define the `/api/v1` contract and common response envelope.
- [x] AUTH-03 Add an idempotent V2 database deployment for plans,
  subscriptions, refresh sessions, and security audit records.
- [x] AUTH-04 Map V2 tables without placing business logic in scaffolded files.
- [x] AUTH-05 Add secure JWT, password hashing, refresh rotation, and rate limits.

## Server API

- [x] AUTH-06 Implement login, refresh, and logout endpoints.
- [x] AUTH-07 Implement the current-account endpoint.
- [x] AUTH-08 Implement plan, entitlement, and quota calculation.
- [x] AUTH-09 Implement idempotent usage reporting and paged history.
- [x] AUTH-10 Add consistent validation, problem responses, and audit logging.
- [x] AUTH-11 Publish the OpenAPI document in Development.

## Desktop integration

- [x] AUTH-12 Add the native HTTPS API client.
- [x] AUTH-13 Keep the access token in memory and protect the refresh token with
  Windows DPAPI.
- [x] AUTH-14 Add a strict WebView/native authentication message contract.
- [x] AUTH-15 Build the sign-in screen in the existing dark-blue design system.
- [x] AUTH-16 Build the account, plan, quota, permission, and history view.
- [x] AUTH-17 Add automatic session restoration, refresh, and logout handling.

## Quality gate

- [x] AUTH-18 Test invalid credentials, disabled users, expired tokens, refresh
  reuse, duplicate usage events, and unauthorized access.
- [x] AUTH-19 Build the frontend, both .NET projects, and the complete solution.
- [x] AUTH-20 Visually review the sign-in and account states at supported window
  sizes.

## Security decisions

- SQL credentials and the JWT signing key never ship with the desktop app.
- The WebView never receives an access token or refresh token.
- Refresh tokens are stored as SHA-256 hashes on the Server.
- Refresh token rotation revokes the previous session token.
- The Server is authoritative for plans, permissions, and quota checks.
- Usage events use a client-generated event ID to prevent double counting.
