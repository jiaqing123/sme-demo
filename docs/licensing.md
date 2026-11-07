# Third-Party Licensing Notes

- **Prepared for:** management review
- **Prepared by:** architecture
- **Date:** 2026-11-06
- **Scope:** the SME demo (this repository), not the production platform

## 1. Bottom line

- Every component in the demo is **free to use at our size**. No purchase is
  required.
- There are **no copyleft obligations**: no GPL, AGPL, or SSPL component is
  included, so nothing forces us to publish our own source.
- Two libraries — MediatR and AutoMapper — moved to a commercial model in 2025.
  We use the last versions published under permissive licences (Apache-2.0 and
  MIT), so there is **no licence key, no fee, and no registration**.
- The only non-permissive component is the SQL Server Developer edition
  container, which Microsoft licenses for **development and testing only**.
  The demo never leaves a laptop, so this is acceptable — but it must not be
  promoted to production.
- No action is required beyond acknowledging section 7.

## 2. What is in the stack

| Component | Version | Licence | Cost at our size | Obligation |
|-----------|---------|---------|------------------|------------|
| .NET 10, ASP.NET Core, EF Core | current LTS | MIT | none | none |
| MediatR | 12.5.0 (pinned) | Apache-2.0 | none | none |
| AutoMapper | 14.0.0 (pinned) | MIT | none | none |
| FluentValidation | current | Apache-2.0 | none | none |
| Azure SDKs (`Azure.Data.Tables`, `Azure.Storage.Blobs`), `Microsoft.IdentityModel.*` | current | MIT | none | none |
| SQL Server 2022 Developer (container) | 2022 | Microsoft EULA | none for dev/test | not for production |
| Azurite (Azure Storage emulator) | latest | MIT | none | none |
| nginx | stable | BSD-2-Clause | none | none |
| Node.js, React, Vite, Tailwind CSS | current | MIT | none | none |
| Docker Desktop | current | commercial subscription | free for small business | see §6 |

Licence texts verified against the upstream repositories on 2026-11-06:
`MediatR v12.5.0` ships the Apache-2.0 text; `AutoMapper v14.0.0` ships the MIT
text.

## 3. The MediatR and AutoMapper change, explained

**What happened.** In 2025 the author moved both libraries to a dual
RPL-1.5 / commercial licence under a new company, Lucky Penny Software. Starting
with MediatR 13 and AutoMapper 15, a licence key is expected. Older versions are
unchanged: their published licences are not retroactive.

**The free Community edition.** It is free for organisations with gross annual
revenue under US$5M (and, for MediatR, under US$10M in outside capital), but it
requires registering a licence key with the vendor. At that size the cost is
zero; the cost is administrative.

**Why we are not using it for the demo.** A licence key is another secret to
store, rotate, and explain. Pinning the permissive versions gives us the same
result with fewer moving parts. The trade-off is that we do not receive fixes
published only in 13+/15+.

**Known advisory on the pinned AutoMapper.** `CVE-2026-32933`
(GHSA-rvv3-g6hj-g44x, CVSS 7.5) is a denial-of-service bug in AutoMapper's
recursive mapper: a deeply nested object graph can overflow the stack. It is
fixed in 15.1.1, which is RPL-1.5, so the pinned 14.0.0 remains affected. NuGet
audit flags it on every build, and `Directory.Build.props` suppresses that one
advisory with the reason written next to it. The exposure in this codebase is
narrow: mappings are explicit and flat (`Card` → `CardDto`, `TransactionRecord`
→ `TransactionDto`), input never reaches AutoMapper, and each mapping sets
`MaxDepth(64)`, the advisory's recommended mitigation. This is accepted for the
demo and must be revisited before the code is reused in a production system.

**Our options, for the record:**

| Option | Cost | Effort | When it makes sense |
|--------|------|--------|---------------------|
| A. Pin MediatR 12.5.0 / AutoMapper 14.0.0 (chosen) | none | none | Demo, prototypes, anything we may throw away |
| B. Upgrade and register Community keys | none at our revenue | key management | Long-lived internal platform |
| C. Commercial subscription | per vendor tier (reported around US$4k/yr for small teams — confirm current pricing) | procurement | We exceed the revenue threshold, or need support |

**Risk to watch.** RPL-1.5 is a reciprocal licence. If we ever take a MediatR
13+/AutoMapper 15+ build under the RPL path, the reciprocity obligations could
reach our own code. The safe rule: never ship an RPL build; either use the
Community licence with a registered key, or stay on the pinned permissive
versions.

## 4. Copyleft and distribution

Everything listed in §2 is permissive. Redistributing a build would require
including the relevant copyright notices — a `THIRD-PARTY-NOTICES` file added
before shipping externally — but no source disclosure.

## 5. SQL Server Developer edition

The container image is the Developer edition, licensed by Microsoft for
development and testing only. It is not licensed for production workloads. If
this demo ever becomes a production system, that layer needs either SQL Server
Standard/Enterprise licensing or Azure SQL, which is a procurement decision, not
an engineering one.

## 6. Docker Desktop

Docker Desktop's subscription is free for personal use, education, non-commercial
open source, and small businesses (fewer than 250 employees **and** less than
US$10M annual revenue). We fall inside that boundary today. If the company grows
past either limit, a per-seat subscription is required.

## 7. What management is asked to acknowledge

1. The company's gross annual revenue is under US$5M and it has not raised more
   than US$10M in outside capital. (Even if this changes, option A remains
   compliant.)
2. Pinning MediatR 12.x and AutoMapper 14.x is acceptable for the demo, and any
   future upgrade is a deliberate decision, not a side effect of a package
   update.
3. The SQL Server container is development-only and must not be used as a
   production database.

## 8. How this stays true

- Versions are pinned centrally in `Directory.Packages.props`; nothing floats.
- A build check (`dotnet list package --include-transitive`, plus a licence
  scan) fails if MediatR reaches 13+, AutoMapper reaches 15+, or any GPL, AGPL,
  or SSPL package appears in the dependency graph.
- This document is reviewed whenever a package is added or upgraded.
