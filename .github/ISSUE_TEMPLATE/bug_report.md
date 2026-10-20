---
name: Bug report
about: A wiki command did something other than what it promised
title: ''
labels: bug
---

## What happened

<!-- The command you ran and what came back. Re-run it with --json if you can:
     the envelope's errors[].code is the most useful single line in a report. -->

```
$ wiki ... --json
```

## What you expected instead

## Environment

- `wiki --version`:
- How you installed it: <!-- install script / manual download / container / dotnet tool / from source -->
- OS and shell: <!-- e.g. Windows 11 + PowerShell, Ubuntu 24.04 + bash -->

## Vault state, if relevant

<!-- `wiki lint --json` and `wiki ingest status` are usually enough. Please
     don't paste private page bodies - slugs and error codes are plenty. -->
