# Ed-Fi DocumentCache Administration CLI

`EdFi.Api.DocumentCacheAdmin` packages the `dms-document-cache` .NET tool for Ed-Fi DMS
DocumentCache status and administration workflows.

Install from an Ed-Fi package feed:

```bash
dotnet tool install --global EdFi.Api.DocumentCacheAdmin --source "$feed" --version "$version"
```

Run help:

```bash
dms-document-cache --help
```

Exit codes are stable for automation:

| Code | Meaning |
| ---: | --- |
| 0 | Status or administrative command completed according to the shared result DTO. |
| 1 | Unexpected or unclassified CLI/runtime failure. |
| 10 | Administrative command rejected before mutation by a known guard or preflight rule. |
| 11 | Administrative command failed before mutation, or status failed before a complete DTO. |
| 12 | Administrative command is incomplete and retryable after possible mutation. |
| 64 | Command-line argument, confirmation, or JSON request validation error. |
| 78 | Process-wide configuration error before the target registry or shared command contract could be built. |

This initial project scaffold shares DMS DocumentCache contracts, command runner, and provider
adapter projects. The command surface, target resolution, administrative execution, and full
operator documentation are implemented by the follow-on DocumentCache administration CLI tasks.
