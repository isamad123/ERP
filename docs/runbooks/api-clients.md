# API clients and keys (S1.27c)

For the person who looks after connected systems (clock-machine gateways, data feeds) and for the operator of the API hosts.

## Giving a system a key
1. Administration, API clients, New API client. Name it for what it serves (for example "Lahore office gateway").
2. Tick only the permissions it needs. Today the only choice is "send punches from a device or its gateway" (`hrm.attendance_devices.push`).
3. List the addresses it calls from, one address or range a line (`203.0.113.7`, `10.1.2.0/24`). With none, every call is refused.
4. Set the calls a minute it may make (the customer as a whole has a ceiling, setting `itg.api.tenant_rate_per_minute`, default 600) and, if you want, a date it stops working.
5. Copy the key when it is shown. It is shown once; it is never kept in readable form and cannot be shown again, only replaced.
6. While approval of sensitive changes is on (`iam.maker_checker.sensitive_roles`, the default) a second person with the approval permission must approve the new key in Approvals before it opens anything.

The system sends `Authorization: ApiKey <client id>.<secret>`. The push endpoint of a device still needs the device's own signature (`X-Erp-Signature`); the key only says who is calling.

## Changing and ending a key
- **New secret:** make one; the system has `iam.api_client.rotation_grace_minutes` (default 60) to switch to the new secret, then the old one stops. Set the setting to 0 to stop the old secret at once.
- **Revoke:** the key stops working at once. There is no edit: a new address list, limit or expiry means a new client (and revoking the old one).
- A key that passes its expiry stops working at that moment; the daily job (`kernel.api_client_expiry`, 02:20) marks it expired and tells the people who manage API clients 14 days before (`iam.api_client.expiry_notice_days`).

## What a key can never do
Call anything outside its permissions, anything under `/api/iam`, or sign a person in. An endpoint is reachable by a key only if its permission is flagged `apiClient` in the module's `permissions.json` and the endpoint carries `ApiClientEndpointMetadata`.

## Operator settings (environment variables or configuration)
- `Network:TrustedProxies`: the addresses or CIDR ranges of your own load balancers or reverse proxies, as `Network__TrustedProxies__0=10.0.0.5` and so on. Only requests from these have their caller address read from `X-Forwarded-For`. **Without it, behind a load balancer every caller appears with the balancer's address and keys are refused by their allow list.** List only your own balancers: a wide range lets any caller forge its address.
- `Api:UsageFlushMilliseconds` (default 60000): how often each API host adds its call counts to the database and reads back the other hosts' counts. 0 switches it off (tests only).
- Failed keys are security events (`iam.api_key_refused`, one per kind and address a minute): Audit, security events. Put a per-address limit on failed calls at the reverse proxy as well (R-296).

## Looking at use
API clients, Use: per day the calls, refused or wrong calls, server errors, calls over the limit and the time the slowest 5% took. A client that hit its limit sends one notice a day to the people who manage API clients.
