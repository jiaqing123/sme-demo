## Summary

<!-- What does this change do, and why? -->

## Scope

- **Included:** <!-- the behaviour, contracts, or files this pull request changes -->
- **Explicitly not included:** <!-- follow-ups and adjacent work left out on purpose -->

## Verification

```text
# Commands run, with their results:
dotnet build SmeDemo.slnx --configuration Release
dotnet test SmeDemo.slnx --configuration Release --no-build
dotnet format SmeDemo.slnx --verify-no-changes
docker compose config -q
python tools/smoke/check-vulnerabilities.py   # python3 on Linux/macOS
```

- [ ] Build is clean in Release.
- [ ] Tests pass in Release.
- [ ] Formatting is clean.
- [ ] `docker compose config -q` passes.
- [ ] `python tools/smoke/check-vulnerabilities.py` is clean (`python3` on Linux/macOS).
- [ ] The relevant smoke or acceptance run passes, or the pull request explains why it cannot be exercised.

## Checklist

- [ ] I read `docs/reference/definition-of-done.md` and ticked every box that applies, with a reason for any box that does not.
- [ ] Requirements, threat model, ADRs, or `development.md` are updated where this change affects them.
- [ ] No secrets, `bin/`, `obj/`, or other generated junk is included.
- [ ] Any image or dependency change is noted above.
- [ ] CI is expected to be green on the final commit.

See `docs/reference/definition-of-done.md` for the full checklist.
