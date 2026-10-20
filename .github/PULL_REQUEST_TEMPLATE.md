<!-- CONTRIBUTING.md has the detail behind each of these. -->

## What changed, and why

<!-- The why matters more than the what; the diff already says the what. -->

## Checklist

- [ ] `dotnet test tests/Wiki.Tests/Wiki.Tests.csproj -c Release` passes
- [ ] The build is warning-free (AOT trim warnings are runtime failures, not noise)
- [ ] `docs/spec.md` matches the behaviour shipped here — corrections appended
      as a lettered amendment in Appendix B rather than edited into the body
- [ ] New DTOs registered in `WikiJsonContext` (native AOT has no reflection fallback)
- [ ] For a rejection path: the `--json` envelope and the exit code are both asserted
- [ ] For derived state: a reindex assertion covers it
