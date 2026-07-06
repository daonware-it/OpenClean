# Security Policy

## Supported versions

OpenClean is released as a rolling application; security fixes are provided for
the **latest released version** only. Please make sure you are on the newest
release before reporting.

| Version | Supported |
|---------|-----------|
| Latest release | ✅ |
| Older releases | ❌ |

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, use one of the private channels below:

- Preferred: GitHub's **[private vulnerability reporting](https://github.com/daonware-it/OpenClean/security/advisories/new)**
  (Security → Report a vulnerability).
- Alternatively, email **security@daonware.de**.

Please include:

- a description of the issue and its impact,
- steps to reproduce (proof of concept if possible),
- the OpenClean version and your Windows version.

## What to expect

- We aim to acknowledge your report within **72 hours**.
- We will keep you updated on our progress and coordinate a disclosure timeline.
- With your consent, we are happy to credit you once a fix is released.

## Scope

Because OpenClean can delete files, manage startup entries, and invoke
uninstallers, we are especially interested in reports involving:

- privilege escalation or execution of code with elevated rights,
- deletion of unintended paths / path-traversal in cleanup logic,
- tampering with the Premium license validation or device binding,
- any network behaviour in the free (unlicensed) application, which should make
  **no network connections at all**.
