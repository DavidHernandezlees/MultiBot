# License service API

All API responses are JSON. Successful calls return `{ "success": true, "data": ... }`.
Failures use an appropriate HTTP status and return `success: false`, a safe `message`,
and `error.code`. Bot failures additionally return a false license-state object.
Clients must check HTTP status and typed Boolean fields; the string `"true"` is not
accepted as a Boolean. Passwords and tokens are never placed in URLs.

## Owner initialization

Run `MultiAiRobot.LicenseServer.exe init-admin` at the server console. Username and
password are prompted. Existing administrators are not replaced. For automation,
`--username NAME` and the process environment `MULTIAIROBOT_ADMIN_PASSWORD` can be
used; passwords are not accepted as command-line arguments. There is no anonymous
web endpoint for creating an administrator or resetting its password.

## Administrator APIs

Except setup-status and login, these routes require `Authorization: Bearer TOKEN`
from an administrator login. Bot tokens cannot access them.

| Method and path | Input or result |
| --- | --- |
| GET `/api/admin/setup-status` | `data.requiresSetup` |
| POST `/api/admin/login` | `{username,password}` → `data.token`, `username`, `expiresAtUtc` |
| POST `/api/admin/logout` | Revoke the current administrator session |
| GET `/api/admin/overview` | `users`, `licenses`, `activeLicenses`, `onlineDevices` counts |
| GET `/api/admin/users` | Account list with IDs, names, enabled state and creation times |
| POST `/api/admin/users` | `{username,password}` |
| PATCH `/api/admin/users/{id}` | `{enabled?,password?}`; account disable/password change revoke sessions |
| GET `/api/admin/licenses` | License metadata and registered devices; no full keys |
| POST `/api/admin/licenses` | `{username,days,maxDevices}` → `{license,key}`; key returned once |
| PATCH `/api/admin/licenses/{id}` | `{enabled?,expiresAtUtc?,maxDevices?}` |
| DELETE `/api/admin/licenses/{id}/devices/{instanceId}` | Remove the binding and all its sessions |
| GET `/api/admin/audit` | Latest 100 audit events |

Usernames contain 3–64 ASCII letters/digits or `. _ @ -`, and start with a letter or
digit. Passwords contain 12–256 characters. New licenses permit 1–3650 days and
1–100 registered devices. Dates are UTC ISO 8601. Usernames are matched without
case sensitivity. Device identifiers are case-sensitive; URL-encode them in paths.

## Desktop/bot APIs

Login request:

```json
{
  "username": "application-user",
  "password": "the-account-password",
  "license_key": "the-issued-key",
  "instance_id": "the-persistent-client-id",
  "balance": 0,
  "profit": 0
}
```

Send this to `POST /api/bot/login`. A successful response includes `data.token` and
`data.expires_at_utc`. The service validates credentials, key assignment, enabled
state, expiry, and available device slots before creating a session. Device binding
and session creation occur in one immediate SQLite transaction. A new login for the
same registered device replaces its previous bearer session.

For all other bot calls, include the bearer token and this JSON body:

```json
{
  "license_key": "the-issued-key",
  "instance_id": "the-persistent-client-id"
}
```

| POST path | Behavior |
| --- | --- |
| `/api/bot/license/verify` | Verify current authorization and mark the bound device online |
| `/api/bot/license/status` | Read current authorization and presence |
| `/api/bot/license/heartbeat` | Verify authorization and refresh presence |
| `/api/bot/license/offline` | Mark the device offline while retaining its binding |

Successful state responses include `valid`, `enabled`, `online`, `status`, `reason`,
and `online_timeout_seconds`. The new desktop checks the typed state fields.
The server revalidates session role/expiry/revocation, user state, license assignment,
license state/expiry, instance identity, and device limits on protected requests.
Administrator tokens are not accepted as bot sessions.

## Persistence and transport

SQLite stores password hashes, license-key hashes, and session-token hashes. It
stores only a short key prefix for display. Opaque random tokens and license keys
are created using .NET cryptographic randomness. Optional balance/profit and unknown
legacy bookmaker credential fields are not persisted. The updated desktop omits
bookmaker credentials entirely.

The default listener is loopback HTTP. Non-loopback listeners/connections require
HTTPS. Explicit allowed hosts protect the local listener; client origin parsing
rejects credentials, paths, queries, fragments, and insecure remote HTTP. Request
bodies are limited to 16 KiB. Login requests have IP rate limits, a bounded
concurrency gate, and account lockout.

`GET /health` returns a basic service liveness response. The package does not
implement `/api/bot/version` or a tip-feed server. A missing update feed is reported
by the desktop as unconfigured, rather than falsely claiming an update check passed.
