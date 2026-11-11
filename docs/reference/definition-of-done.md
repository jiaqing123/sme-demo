# Definition of Done

- **Status:** required for every pull request
- **Owner:** Architecture
- **Last updated:** 2026-11-11

Run this list before opening a pull request and again before merging. A box that
does not apply is ticked with a one-line reason in the pull request, not silently
skipped. The security section is a release gate: an unchecked security box blocks
the merge.

## 1. Build and tests

- [ ] Tools are restored: `dotnet tool restore`.
- [ ] The solution builds clean in Release:
      `dotnet build SmeDemo.slnx --configuration Release`.
- [ ] All tests pass in Release:
      `dotnet test SmeDemo.slnx --configuration Release --no-build`.
- [ ] Formatting is clean: `dotnet format SmeDemo.slnx --verify-no-changes`.
      The formatter may warn that `docker-compose.dcproj` has no associated
      language; that warning is harmless, and the command must exit 0.
- [ ] The change adds or updates tests for the behaviour it introduces; a bug fix
      has a test that fails without the fix.

## 2. Schema and generated SQL

- [ ] The API model has no pending migration:
      `dotnet tool run dotnet-ef migrations has-pending-model-changes --project src/SmeDemo.Infrastructure --startup-project src/SmeDemo.Infrastructure`.
- [ ] The auth model has no pending migration:
      `dotnet tool run dotnet-ef migrations has-pending-model-changes --project src/SmeDemo.Auth --startup-project src/SmeDemo.Auth`.
- [ ] Any entity change ships with one migration and the regenerated idempotent
      script (see [`../development.md`](../development.md#changing-a-schema)).
- [ ] The committed SQL still matches the model:

      ```powershell
      dotnet tool run dotnet-ef migrations script --idempotent `
        --project src/SmeDemo.Infrastructure `
        --startup-project src/SmeDemo.Infrastructure `
        --output db/migrations/api/sme_api.sql

      dotnet tool run dotnet-ef migrations script --idempotent `
        --project src/SmeDemo.Auth `
        --startup-project src/SmeDemo.Auth `
        --output db/migrations/auth/sme_auth.sql

      git diff --exit-code -- db/migrations
      ```

- [ ] The generated script has been read. Any `DROP`, `ALTER COLUMN`, or
      `DELETE` has a second reviewer and a written reason
      ([`../requirements.md`](../requirements.md) §8.8).

## 3. Contracts and architecture

- [ ] The build regenerated the committed OpenAPI document, and the diff is
      clean: `dotnet build src/SmeDemo.Api/SmeDemo.Api.csproj --configuration Release`
      then `git diff --exit-code -- openapi/`. Do not hand-edit
      [`../../openapi/v1.json`](../../openapi/v1.json).
- [ ] New or changed routes, request/response fields, status codes, or security
      requirements appear in `openapi/v1.json` and in the contract tests.
- [ ] The architecture suite in
      [`../../tests/SmeDemo.Architecture.Tests`](../../tests/SmeDemo.Architecture.Tests)
      is green (it runs as part of `dotnet test`), including the endpoint
      metadata, layer-direction, endpoint-purity, and auth-metadata rules. In
      particular, every endpoint under `/api/v1` has:
      `IAuthorizeData` with the `ApiUser` policy, `IEndpointNameMetadata`, and at
      least one `IProducesResponseTypeMetadata`. The executable rule is
      [`ApiEndpointContractTests.cs`](../../tests/SmeDemo.Architecture.Tests/ApiEndpointContractTests.cs);
      the same assertions are mirrored for the running host in
      [`EndpointContractTests.cs`](../../tests/SmeDemo.Api.Tests/EndpointContractTests.cs).
- [ ] The endpoint purity rules in [`../requirements.md`](../requirements.md)
      §5.5 still hold: no `DbContext`, `TableClient`, `BlobClient`, or
      `HttpClient` in endpoint files, and `Application` does not reference `Api`
      or `Infrastructure`
      ([`EndpointPurityTests.cs`](../../tests/SmeDemo.Architecture.Tests/EndpointPurityTests.cs),
      [`ProjectDirectionTests.cs`](../../tests/SmeDemo.Architecture.Tests/ProjectDirectionTests.cs)).

## 4. Security DoD (release gate)

- [ ] A threat-model review is recorded in the pull request: link to
      [`../threat-model.md`](../threat-model.md) and name the section that
      changed. The triggers in §8 decide whether a review is required; when in
      doubt, review.
- [ ] Every new endpoint, data store, third-party integration, and new class of
      PII or financial data has abuse-case tests from
      [`../threat-model.md`](../threat-model.md) §5. At minimum:
  - [ ] object-level authorisation (IDOR/BOLA): another user's object returns
        `403`, an unknown ID returns `404`, and the response does not confirm
        existence;
  - [ ] authentication paths return `401` with the documented error code and no
        stack trace;
  - [ ] wrong `org` returns `403`; malformed or expired tokens return `401`;
  - [ ] logs contain no password, OTP, token, phone number, or PAN;
  - [ ] a new dependency or outbound call has a timeout and a documented failure
        mode.
- [ ] No new secrets or PII: inspect `git diff --cached`. There is no new value
      in `.env`, no signing key or certificate, no access token, no real phone
      number, and no PAN/CVV/PIN. Test fixtures use obviously fictional values.
- [ ] The dependency scan is clean:
      `python3 tools/smoke/check-vulnerabilities.py` after a Release build. It
      scans every project under `src/`, `tests/`, and `tools/` (currently 16),
      and fails on any advisory, command error, or unparseable output. Raw
      `dotnet list package --vulnerable` is not a gate because it exits 0 even
      when it reports findings.
- [ ] The secret scan is clean with the pinned gitleaks 8.30.1 release:
      `gitleaks detect --source . --no-git --redact --config .gitleaks.toml --exit-code 1`.
      `.gitleaks.toml` allowlists only the documented Azurite emulator key and
      local build/tool caches. The download URL, checksum, and local setup are in
      [`../development.md`](../development.md#dependency-and-secret-scans).
- [ ] Every new demo shortcut is registered in
      [`../threat-model.md`](../threat-model.md) §7 and
      [`../requirements.md`](../requirements.md) §11.3, with its production
      replacement. A shortcut that is not written down must not ship.
- [ ] The change does not weaken an existing control (default-deny policy,
      `no-store` on account data, org policy, redaction, least-privilege database
      logins, non-root containers).

## 5. Documentation

- [ ] Behaviour or contract changes update
      [`../requirements.md`](../requirements.md) (resource table, data model,
      errors, acceptance) in the same pull request.
- [ ] New endpoints or data flows update the relevant threat-model sections, not
      only the code.
- [ ] Run and migration changes update [`../development.md`](../development.md).
- [ ] A new or changed architectural rule is added to
      [`adr/README.md`](adr/README.md), or the existing decision is linked and
      confirmed still valid.
- [ ] The pull request description names the commands run and any box left
      unticked with the reason.

## 6. CI and delivery

- [ ] The full CI pipeline is green on the final commit. The `dotnet`, `web`,
      and `compose` jobs are reproducible locally (see
      [`../development.md`](../development.md#continuous-integration)).
- [ ] The `images` and `compose-smoke` jobs are green. They are CI-only gates:
      the first builds six images (api, auth, web, sms-mock, issuer-mock,
      migrator) tagged with the commit SHA; the second starts the minimal Compose
      stack and runs `tools/smoke/compose-smoke.sh`, which asserts six checks
      including the unauthenticated `401 UNAUTHENTICATED` path.
- [ ] If the change cannot be exercised by the smoke test, say so in the pull
      request and describe the manual check performed.

> **Verification status (2026-11-11):** the full Compose smoke flow passed 6/6
> locally, and two Release builds produced an identical `openapi/v1.json` hash.
> GitHub Actions has not been executed yet; the Linux `dotnet format` run and the
> stale-OpenAPI negative path are unverified in CI. Tick the CI boxes only after
> a real GitHub run.
