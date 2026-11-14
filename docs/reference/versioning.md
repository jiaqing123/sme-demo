# API Versioning

- **Status:** accepted rule
- **Owner:** Architecture
- **Last updated:** 2026-11-11
- **Decision:** keep the URL path version as the `ApiRoutes.Prefix` constant
  (`/api/v1`); do not add the `Asp.Versioning` package now.

## 1. Current rule

The version is part of the path and is defined in one place:

```csharp
// src/SmeDemo.Api/Endpoints/ApiRoutes.cs
public const string Version = "v1";
public const string Prefix = "/api/" + Version;
```

[`ApiRoutes.cs`](../../src/SmeDemo.Api/Endpoints/ApiRoutes.cs) is the only source
of the prefix. Endpoint files use `ApiRoutes.Prefix` for the group; the
`art_url` exception is tracked as a P1 in the
[handoff](../history/handoff-reference-baseline.md) §5.3 and must
move to the API route constant or `LinkGenerator` when it is fixed. Do not add a second
literal `/api/v1` string in new code.

[`Directory.Packages.props`](../../Directory.Packages.props) does not include
`Asp.Versioning.*`, and no header, media-type, or query-string versioning is
implemented. That is deliberate: one version and a handful of routes do not
justify the package and its conventions.

The OpenAPI document is versioned by the same path. The committed
[`openapi/v1.json`](../../openapi/v1.json) describes v1 only; there is no second
document while only one version exists.

## 2. Additive changes inside v1

These are allowed without a version change, provided the contract tests and
`openapi/v1.json` are updated and clients can ignore what they do not know:

- a new endpoint;
- a new optional response field;
- a new optional query parameter with a documented default;
- a new error code for a new failure, while existing codes keep their meaning.

Additive does not mean untested. A new optional field still needs the contract
tests and an OpenAPI regeneration. If a client cannot safely ignore the addition,
it is not additive.

## 3. Breaking changes

A change is breaking when an existing v1 client can no longer work unchanged.
Treat at least these as breaking:

| Change | Why it breaks |
|--------|---------------|
| Remove or rename a route | The client calls a path that no longer exists. |
| Remove or rename a request or response field | Deserialisation or client code breaks. |
| Change a field's type, format, unit, or nullability | The same JSON value means something else. |
| Add a required request field | Existing requests become invalid. |
| Change a status code or the error code for an existing failure | Clients switch on these values. |
| Tighten validation on an existing field | Previously accepted requests start returning `400`. |
| Change pagination or cursor semantics | Stored cursors and client paging break. |
| Change default values or ordering guarantees clients rely on | Behaviour changes without a contract change. |
| Change authentication or authorisation requirements on an existing route | A client that worked without a token or scope starts getting `401`/`403`. |
| Remove a response header a client relies on (`Cache-Control`, `Retry-After`) | Cache and retry behaviour changes. |

When in doubt, ask whether a client generated from the previous
`openapi/v1.json` would still work. If not, it is breaking.

## 4. What a breaking change requires

1. **A new version segment.** Add a second version constant and prefix (for
   example `Version2 = "v2"`, `PrefixV2 = "/api/" + Version2`) and mount the new
   endpoint set, keeping `Prefix` on v1 for the deprecation window. Do not put two
   versions behind one prefix or negotiate by header.
2. **Both versions live at once.** v1 stays in the codebase and in
   `openapi/v1.json` while the deprecation window runs. A new version does not
   authorise deleting v1 in the same pull request.
3. **A documented deprecation window.** The pull request that introduces v2
   records the window in the ADR and release notes: start date, the last date v1
   will be served, and the migration note for clients. The window is agreed at
   that point; there is no default duration in the code, and v1 is not removed
   before the recorded date. Announce the deprecation in the release notes and
   the API documentation. Response `Deprecation` / `Sunset` headers are not
   implemented yet; if they are added, they must agree with the recorded window.
4. **A migration note.** Name the removed or changed fields and the replacement
   path. Keep the old contract tests running for v1 until v1 is removed.
5. **Threat-model review.** A new version is a new attack surface. Run the
   triggers in [`../threat-model.md`](../threat-model.md) §8 before merge.

Removing v1 after the window is itself a breaking change only for clients that
ignored the deprecation; it is still released with a note and a final
`openapi/v1.json` removal.

## 5. When to revisit the package decision

The no-package rule holds while there is one deployed version and no need for
header or media-type negotiation. Revisit it, with an ADR, when:

- a second version is actually implemented and maintained in the same host;
- a consumer requires version selection by header or media type;
- the number of versions or the deprecation overlap makes hand-written route
  groups error-prone.

Until then, do not add `Asp.Versioning` speculatively. It changes routing,
OpenAPI generation, and tests for a problem this repository does not have.

## 6. Checklist for a version change

- [ ] The route constant is the only place the version appears.
- [ ] `openapi/v1.json` (and a future `openapi/v2.json`) is regenerated and
      committed.
- [ ] Contract and architecture tests cover the new version and still cover the
      old one.
- [ ] The deprecation window and migration note are recorded in the ADR and
      release notes.
- [ ] The threat-model review is recorded.
- [ ] [`definition-of-done.md`](definition-of-done.md) is fully ticked.
