# Third-Party Licensing Notes

- **Prepared for:** management review
- **Prepared by:** architecture
- **Date:** 2026-11-11 (supersedes the 2026-11-06 version)
- **Scope:** the SME demo (this repository), not the production platform

## 1. Bottom line

- Every component in the demo is **free to use at our size**. No purchase is
  required.
- There are **no strong copyleft obligations**: no GPL, AGPL, or SSPL component
  is included, so nothing forces us to publish our own source. The only
  weak-copyleft component is `lightningcss` (MPL-2.0), an unmodified build-time
  tool that is not shipped in the SPA (§2.1).
- **MediatR and AutoMapper are no longer dependencies.** Both moved to a
  commercial model in 2025. Rather than pin old versions or register licence
  keys, the demo now dispatches commands and queries through interfaces it owns
  and writes its projections by hand (§3).
- No licence key, fee, registration, or renewal applies to any library in the
  stack.
- The only non-permissive component is the SQL Server Developer edition
  container, which Microsoft licenses for **development and testing only**.
  The demo never leaves a laptop, so this is acceptable — but it must not be
  promoted to production.
- No ongoing action is required beyond acknowledging section 7.

## 2. What is in the stack

| Component | Version | Licence | Cost at our size | Obligation |
|-----------|---------|---------|------------------|------------|
| .NET 10, ASP.NET Core, EF Core | current LTS | MIT | none | none |
| FluentValidation | current | Apache-2.0 | none | none |
| Azure SDKs (`Azure.Data.Tables`, `Azure.Storage.Blobs`), `Microsoft.IdentityModel.*` | current | MIT | none | none |
| SQL Server 2022 Developer (container) | 2022 | Microsoft EULA | none for dev/test | not for production |
| Azurite (Azure Storage emulator) | latest | MIT | none | none |
| nginx | stable | BSD-2-Clause | none | none |
| Node.js, React, Vite, Tailwind CSS | current | MIT | none | none |
| Docker Desktop | current | commercial subscription | free for small business | see §6 |

Command/query dispatch and object mapping are implemented in this repository
(`src/SmeDemo.Application/Abstractions/Messaging.cs` and the feature handlers),
so they are not third-party components at all.

### 2.1 Inventory check (2026-11-11)

The full package graph was reviewed after the MediatR/AutoMapper removal:

- **Direct .NET packages.** All are Microsoft/Azure (MIT) except
  FluentValidation and its DI extensions (Apache-2.0). Those are the only
  non-Microsoft direct dependencies.
- **Transitive .NET packages.** Three non-Microsoft MIT packages —
  `Humanizer.Core`, `Mono.TextTemplating`, and `Newtonsoft.Json` — arrive
  through `Microsoft.EntityFrameworkCore.Design`, which is referenced with
  `PrivateAssets="all"` and is absent from the API runtime output.
  `Apache.Arrow` (Apache-2.0) is pulled by `Azure.Storage.Blobs` at runtime.
- **npm.** The installed tree has 176 packages; all are MIT, Apache-2.0, ISC,
  BSD, or MIT-0 except `lightningcss` (MPL-2.0) and `caniuse-lite`
  (CC-BY-4.0). Both are build-time only and are not part of the served static
  assets under `web/dist`.
- **No GPL, AGPL, or SSPL** package appears in either graph, and
  `dotnet list package --vulnerable --include-transitive` reports no advisories
  for any project.

Run the same check after any dependency change: inspect the direct references,
run `dotnet nuget why <project> <package>` for anything unexpected, and confirm
the licence of new npm packages before they enter `package.json`.

## 3. Why MediatR and AutoMapper were removed

**What happened upstream.** In 2025 the author moved both libraries to a dual
RPL-1.5 / commercial licence under a new company, Lucky Penny Software. Starting
with MediatR 13 and AutoMapper 15, a licence key is expected. Older versions are
unchanged: their published licences are not retroactive.

**Why pinning was withdrawn.** The previous decision was to freeze the last
permissive releases (MediatR 12.5.0, AutoMapper 14.0.0). Two problems made that
a standing risk rather than a stable choice:

1. Security and bug fixes published only in 13+/15+ are unavailable without
   changing the licence position.
2. The pinned AutoMapper 14.0.0 is affected by `CVE-2026-32933`
   (GHSA-rvv3-g6hj-g44x, CVSS 7.5): a deeply nested object graph can overflow the
   stack in the recursive mapper. The fix is 15.1.1, which is RPL-1.5. The
   repository previously suppressed the NuGet audit warning and relied on
   `MaxDepth(64)` as a mitigation; that suppression is now gone.

**The decision.** Remove both libraries instead of managing keys, renewals, and
accepted advisories. The replacement is deliberately small and owned by the
team:

- `ICommand<TResult>`, `IQuery<TResult>`, `ICommandHandler<,>`,
  `IQueryHandler<,>`, and `Unit` live in
  `src/SmeDemo.Application/Abstractions/Messaging.cs`.
- Handlers are registered one by one in `AddApplication`; endpoints inject the
  handler they call. No assembly scan and no reflection-based dispatch.
- Validation runs in a decorator
  (`src/SmeDemo.Application/Decorators/ValidationQueryHandler.cs`) wired
  explicitly with a DI factory.
- The four API projections (`Card` → `CardDto`, `TransactionRecord` →
  `TransactionDto`, and the two response wrappers) are written at the call site.

**What we give up.** Adding a handler now needs an explicit registration line,
and there is no third-party pipeline ecosystem to lean on. For a codebase this
size that is a fair trade: the call graph is visible in the composition root and
in the endpoint signatures.

**No vendor relationship remains.** There is no key to store or rotate, no
revenue threshold to monitor for these libraries, and no renewal date.

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

1. MediatR and AutoMapper were removed on 2026-11-11; the demo does not use a
   commercial or Community-licensed mediator or mapper, and no licence key is
   stored anywhere.
2. The remaining libraries are permissively licensed; the SQL Server container
   is development-only and must not be used as a production database.
3. Docker Desktop's free small-business tier applies today; growing past 250
   employees or US$10M annual revenue triggers a subscription.

## 8. How this stays true

- Versions are pinned centrally in `Directory.Packages.props`; nothing floats.
- CI fails if a `MediatR` or `AutoMapper` package reference is reintroduced
  (`.github/workflows/ci.yml`).
- There are no `NuGetAuditSuppress` entries: every advisory is either fixed by
  the current version or treated as a reason not to use the package.
- `dotnet list package --vulnerable --include-transitive` is part of the package
  review.
- The GPL/AGPL/SSPL inventory is reviewed by hand whenever a package is added or
  upgraded; there is no automated licence scan.
- The §2.1 inventory check is repeated after any dependency change.
- This document is reviewed whenever a package is added or upgraded.
