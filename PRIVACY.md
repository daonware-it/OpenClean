# Privacy in OpenClean

OpenClean collects **no telemetry**, requires **no account**, and runs without a
background service. During normal operation the app makes **no network connection
whatsoever**.

## The only exception: Premium license (optional)

Buying and activating OpenClean Premium triggers the app's only network requests —
exclusively to `daonware.de`:

| When | What is transmitted |
|---|---|
| Activate license (manual, in the dialog) | License key, anonymous device hash*, app version |
| License check on startup and on the scheduled cleanup run (**only if a license is already present** – renews the token and detects a server-side revocation; free users never trigger this) | License key, anonymous device hash*, app version |
| Deactivate device (manual) | License key, anonymous device hash* |
| Premium module download (after activation) | short-lived download token, app version |

\* The device hash is a SHA-256 hash of the Windows MachineGuid with an app prefix.
It cannot be reversed back to the machine or the person, and no raw identifier is ever
transmitted. It serves solely to enforce the "license on max. 3 devices" limit.

## What the license server stores

- E-mail address (from the purchase – for key delivery and support)
- SHA-256 hash of the license key (never the key itself)
- Device hashes and timestamps of activations

No accounts, no passwords, no usage or system data.

## Offline use

The license is verified locally via a signed file (`license.json`) and works
**fully offline for up to 30 days**. When an internet connection is available, the app
briefly validates the license against the server on startup (token renewal; a revoked
key is detected and removed locally). Without internet, Premium keeps working until the
last successful server contact is 30 days old; after that, a single online check is
required.

Payment is handled by Stripe (see their privacy policy); OpenClean itself only receives
the e-mail address and order reference.
