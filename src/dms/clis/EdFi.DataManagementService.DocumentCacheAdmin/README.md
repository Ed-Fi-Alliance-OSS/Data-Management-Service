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

This initial project scaffold shares DMS DocumentCache contracts, command runner, and provider
adapter projects. The command surface, target resolution, administrative execution, and full
operator documentation are implemented by the follow-on DocumentCache administration CLI tasks.
