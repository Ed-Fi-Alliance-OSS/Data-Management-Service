# Design: Runtime Serilog Sink Plugin Loading

## 1. Purpose and Goals

DMS ships as open-source software. Operators have diverse observability stacks —
Elasticsearch, Seq, Splunk, Datadog, OpenTelemetry, etc. — and it is not
practical for DMS to compile against every possible Serilog sink. This document
proposes a mechanism by which operators can add a Serilog sink at deploy time by
dropping assemblies into a well-known directory, without recompiling DMS.

Goals:

- Zero new NuGet dependencies in DMS.
- No C# code changes required from the operator to add a sink.
- Sink selection and configuration remain entirely in `appsettings.json` /
  environment variables.
- Provide meaningful security controls without making the feature operationally
  burdensome.

Non-goals:

- Hot-reload of sinks while the process is running.
- Version conflict resolution between plugin dependencies and DMS dependencies.
- An official catalog or vetting process for third-party sinks.

## 2. Background

Serilog is configured via `ReadFrom.Configuration()` in
`frontend/EdFi.DataManagementService.Frontend.AspNetCore/Infrastructure/WebApplicationBuilderExtensions.cs`.
This reads the `Serilog` section of `appsettings.json` at startup, including the
`Using` array, which lists the assembly names of sink packages. Serilog resolves
those names by calling `Assembly.Load()`, which probes the .NET default
`AssemblyLoadContext`.

The constraint today: the sink assembly must have been referenced at compile
time so that it appears in the published output directory. Operators who want a
different sink must either recompile DMS or build a custom Docker image.

## 3. Proposed Design

### 3.1 Plugin Directory

A well-known directory, defaulting to `serilog-plugins` under
`AppContext.BaseDirectory`, is scanned at startup. If `PluginsPath` is set to a
relative path, it is resolved against `AppContext.BaseDirectory`. All `.dll`
files found are loaded into the default `AssemblyLoadContext` before
`ReadFrom.Configuration()` runs.

The directory path is configurable:

```json
"DMS": {
  "Logging": {
    "PluginsPath": "./serilog-plugins"
  }
}
```

If the directory does not exist, startup proceeds normally with no warning.

### 3.2 Assembly Allowlist

To limit the blast radius of a compromised or misconfigured plugins directory,
only assemblies whose names match an explicit allowlist are loaded. Assemblies
not on the list are logged as skipped warnings and never loaded.

```json
"DMS": {
  "Logging": {
    "PluginsPath": "./serilog-plugins",
    "AllowedPlugins": [
      "Elastic.Serilog.Sinks",
      "Serilog.Sinks.Seq"
    ],
    "PluginHashes": {
      "Elastic.Serilog.Sinks": "8478c5...",
      "Serilog.Sinks.Seq":     "D72B04..."
    }
  }
}
```

`PluginHashes` is optional. When an entry is present for an assembly, its
SHA-256 hash is verified before loading; a mismatch causes the DLL to be
skipped with a logged error. Assemblies with no hash entry are loaded on
allowlist membership alone. The hash values are hex-encoded (case-insensitive)
 SHA-256 digests of the DLL file:

```shell
# Linux / macOS - returns upper case
sha256sum Elastic.Serilog.Sinks.dll

# Windows PowerShell - returns lower case
Get-FileHash Elastic.Serilog.Sinks.dll -Algorithm SHA256
```

The allowlist is checked against the `AssemblyName` read from the DLL's
metadata, not the filename, to prevent simple filename spoofing. The allowlist
alone does not prevent a crafted DLL from passing; `PluginHashes` closes that
gap (see §4.2).

### 3.3 Startup Behavior

Plugin loading failures are **not silent**, provided a bootstrap logger is
configured before `LoadSerilogPlugins` runs (see §3.5). The behavior depends on
the failure type:

| Failure                                              | Behavior                                                         |
| ---------------------------------------------------- | ---------------------------------------------------------------- |
| Plugins directory missing                            | Continue silently                                                |
| Assembly not on allowlist                            | Log warning, skip                                                |
| Assembly hash does not match `PluginHashes` entry    | Log error, skip                                                  |
| Assembly fails to load (bad binary, wrong runtime)   | Log error with full exception, skip                              |
| Assembly loads but Serilog cannot find the sink type | Serilog's existing behavior — configuration exception at startup |

DMS does not fail fast on a single bad plugin DLL; operators may deploy multiple
sinks and a corrupt single file should not prevent all logging. However, if
Serilog cannot initialize (e.g., a named sink in `WriteTo` cannot be resolved),
normal Serilog startup failure behavior applies.

**Load ordering:** `Directory.GetFiles` provides no guaranteed ordering. When
multiple plugin DLLs are present, loading order is filesystem-dependent and may
vary across operating systems or deployments. Plugins must not depend on each
other or on a specific loading sequence.

### 3.4 Code Location

A new private static method `LoadSerilogPlugins(IConfiguration)` is added to
`WebApplicationBuilderExtensions`, called as the first line of the existing
`ConfigureLogging()` local function, before the `LoggerConfiguration` is built.

```csharp
static void LoadSerilogPlugins(IConfiguration configuration)
{
    var pluginsPath = configuration.GetValue<string>("DMS:Logging:PluginsPath")
        ?? Path.Combine(AppContext.BaseDirectory, "serilog-plugins");

    if (!Directory.Exists(pluginsPath))
        return;

    var allowedPlugins = configuration
        .GetSection("DMS:Logging:AllowedPlugins")
        .Get<string[]>() ?? [];

    if (allowedPlugins.Length == 0)
    {
        Log.Warning(
            "[Serilog plugins] DMS:Logging:AllowedPlugins is empty — set it to load plugins from {PluginsPath}",
            pluginsPath
        );
        return;
    }

    var pluginHashes = new Dictionary<string, string>(
        configuration.GetSection("DMS:Logging:PluginHashes").Get<Dictionary<string, string>>() ?? [],
        StringComparer.OrdinalIgnoreCase
    );

    foreach (var dll in Directory.GetFiles(pluginsPath, "*.dll"))
    {
        try
        {
            var assemblyName = AssemblyName.GetAssemblyName(dll).Name;
            if (!allowedPlugins.Contains(assemblyName, StringComparer.OrdinalIgnoreCase))
            {
                Log.Warning("[Serilog plugins] Skipping {File} — not in AllowedPlugins", dll);
                continue;
            }
            if (pluginHashes.TryGetValue(assemblyName!, out var expectedHash))
            {
                using var stream = File.OpenRead(dll);
                var actualHash = Convert.ToHexString(SHA256.HashData(stream));
                if (!actualHash.Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Error(
                        "[Serilog plugins] Hash mismatch for {AssemblyName} — expected {Expected}, got {Actual}. Skipping.",
                        assemblyName, expectedHash, actualHash
                    );
                    continue;
                }
            }
            AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
            Log.Information("[Serilog plugins] Loaded {AssemblyName}", assemblyName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Serilog plugins] Failed to load {File}", dll);
        }
    }
}
```

### 3.5 Bootstrap Logger Requirement

`LoadSerilogPlugins` calls `Log.Warning`, `Log.Information`, and `Log.Error` —
Serilog's static logger — before `ReadFrom.Configuration()` runs. To uphold the
"no silent failures" guarantee in §3.3, **the host must configure a bootstrap
logger before calling `ConfigureLogging`**. Without one, plugin-load diagnostics
are silently swallowed. Note: DMS currently configures Serilog via
`webAppBuilder.Logging.AddSerilog(logger)` without setting `Log.Logger`, so the
implementation must either set `Log.Logger` (bootstrap + final) or avoid
`Log.*` calls in `LoadSerilogPlugins`.

The standard Serilog-in-ASP.NET-Core pattern satisfies this requirement:

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();
```

This call must appear in `Program.cs` before the `WebApplicationBuilder` is
created and before `ConfigureLogging` (which internally calls
`LoadSerilogPlugins`) runs. The bootstrap logger is replaced by the final logger
once `ReadFrom.Configuration()` completes.

Implementations that omit the bootstrap logger violate this contract. If a
future integration context cannot guarantee a bootstrap logger, the call site
should switch the internal `Log.*` calls to `Console.Error.WriteLine` — but
that tradeoff (plain-text output that bypasses structured logging) should be
weighed explicitly and is not the default.

### 3.6 Operator Workflow

To add the Elasticsearch sink:

1. Publish the sink and its transitive dependencies to the plugins directory.
   The simplest approach is a throwaway project:

   ```shell
   dotnet new classlib -n TempPluginCollector
   cd TempPluginCollector
   dotnet add package Elastic.Serilog.Sinks
   dotnet publish --no-self-contained -o ./out
   # Copy all .dll files from ./out to the DMS serilog-plugins directory
   ```

   > [!NOTE]
   > `--no-self-contained` prevents the SDK from copying shared-framework
   > assemblies (`System.*`, `Microsoft.*`, etc.) into the output. Without it,
   > `dotnet publish` may include runtime assemblies that are already loaded by
   > DMS, potentially shadowing them with mismatched versions and producing
   > cryptic load errors.

2. Add the assembly name to `AllowedPlugins` in `appsettings.json` or via
   environment variable.
3. Add `Using` and `WriteTo` entries to the `Serilog` configuration section.
4. Restart DMS.

No DMS source changes. No recompile.

### 3.7 Configuration Management Service

While the proposal above is written in the context of the core DMS code, the
same plugin loading mechanism SHOULD be used by the Configuration Management
Service.

## 4. Security Considerations

### 4.1 Threat Model

The primary threat is an attacker (or misconfiguration) causing arbitrary code
to be loaded and executed in the DMS process. A loaded assembly runs with full
process trust — access to credentials, request data, database connections.

### 4.2 Controls

**File system permissions (primary control)** The plugins directory should be
writable only by the deployment user/service account, not by the DMS runtime
user. This is the strongest available control and is independent of DMS code.
Deployment documentation should make this requirement explicit.

**Assembly allowlist (limited defense in depth)** The allowlist filters by the
assembly name embedded in the DLL's metadata. Because that metadata is
operator-supplied, it does not stop an attacker who can write to the plugins
directory from crafting a DLL with a permitted assembly name (e.g.,
`Elastic.Serilog.Sinks`) and arbitrary code inside. The allowlist raises the
effort required and prevents accidental loading of unintended assemblies, but it
is not a content-integrity check.

> **Note on .NET strong naming:** In .NET Framework, strong-name signatures
> provided a cryptographic check on assembly identity that would have caught this
> attack. .NET Core / .NET 5+ do not enforce strong-name verification on
> `LoadFromAssemblyPath` by default, so this protection is not available here.

**No silent failures** All skipped and failed loads are logged. Operators have a
clear audit trail of what was and was not loaded at startup.

**Hash verification (optional, closes the allowlist gap)** The optional
`PluginHashes` configuration map (§3.2) makes the allowlist content-addressed
rather than name-addressed. When a hash entry is present for an assembly, its
SHA-256 digest is verified before loading; a mismatch causes the DLL to be
skipped with a logged error. Even a DLL crafted with a permitted assembly name
will be rejected unless its bytes match the pre-computed hash. Assemblies with
no entry in `PluginHashes` are still loaded on allowlist membership alone, so
operators can adopt hash verification incrementally.

### 4.3 What This Design Does Not Protect Against

- A supply chain compromise of an explicitly allowlisted package.
- An attacker with write access to the plugins directory forging an allowed
  assembly name (mitigated but not eliminated by the allowlist alone; hash
  verification closes this gap).
- An operator who misconfigures the allowlist (e.g., uses a wildcard or allows
  an unintended package).
- An operator who grants write access to the plugins directory to the DMS
  runtime user.

These are all operator responsibility. The design provides the right primitives;
enforcement is a deployment/ops concern.

## 5. Pros and Cons

### Pros

- **Zero new dependencies.** No new NuGet packages required in DMS.
- **No code changes for operators.** Sink selection and configuration stay in
  `appsettings.json`.
- **Extends existing Serilog conventions.** The `Using` array already supports
  dynamic assembly names; this just ensures those assemblies are present before
  Serilog reads config.
- **Works with all Serilog sinks.** Any sink that supports
  `Serilog.Settings.Configuration` is compatible — no DMS-specific adapter
  needed.
- **Transparent failure modes.** All load attempts are logged; no silent
  degradation.
- **Small implementation surface.** The feature is a single private static
  method of ~30 lines.

### Cons

- **Transitive dependency management falls to the operator.** The operator must
  collect and deploy the sink's full dependency closure. There is no lockfile or
  version pinning, so dependency conflicts between the sink and DMS assemblies
  are possible and may produce cryptic runtime errors.
- **A plugin dependency that shadows a DMS assembly produces a confusing failure
  chain.** If a plugin's dependency closure includes an assembly already loaded
  by DMS at a different version, `LoadFromAssemblyPath` will throw and the
  plugin will be skipped with a logged error. The Serilog sink resolution
  failure that follows surfaces as a separate, unrelated configuration
  exception, making it non-obvious that the root cause was the skipped plugin.
  Operators should compare startup log output for skipped-plugin errors against
  any Serilog initialization failures.
- **No version compatibility guarantee.** A Serilog sink compiled against a
  different version of `Serilog.Settings.Configuration` may fail in non-obvious
  ways.
- **Allowlist requires advance configuration.** Operators must add the assembly
  name to `AllowedPlugins` before the plugin is loaded; a missing entry produces
  a warning, not a helpful error about "add this to AllowedPlugins." This may be
  surprising on first use.
- **Filesystem permission requirement is ops discipline, not code enforcement.**
  DMS cannot verify that the plugins directory has correct permissions.

## 6. Rejected Alternatives

### 6.1 Ship All Known Sinks in the DMS Package

Include `Elastic.Serilog.Sinks`, `Serilog.Sinks.Seq`, etc. as optional NuGet
references in the frontend project.

**Rejected because:** This grows the DMS dependency surface with packages most
operators will never use, increases supply chain risk, and requires DMS
maintainers to track and update each sink's version. It also does not scale —
new sinks would require DMS releases.

### 6.2 Require Operators to Recompile

Document that operators who want a non-default sink should fork or extend DMS
and add the package reference themselves.

**Rejected because:** This is the current state of affairs and the motivation
for this design. It creates a significant adoption barrier for operators who
want observability customization without maintaining a fork.

### 6.3 Isolated AssemblyLoadContext per Plugin

Load each plugin DLL into its own `AssemblyLoadContext` for stronger isolation.

**Rejected because:** `Serilog.Settings.Configuration` resolves sink types by
name using the default ALC. Types loaded into a custom ALC are invisible to it,
so the sink would never be found. Bridging between ALCs is possible but would
require deep hooks into Serilog internals and is disproportionate complexity for
this use case.

### 6.4 NuGet Restore at Runtime

At startup, read a `plugins.json` manifest listing package IDs and versions,
call `dotnet restore` (or use the NuGet client libraries directly) to download
and resolve the packages, then load them.

**Rejected because:** This requires network access from the production container
(often prohibited), introduces the NuGet client as a runtime dependency,
significantly increases startup time, and creates a new class of failure modes
(network partitions, registry outages, yanked packages). It also complicates
air-gapped deployments.

### 6.5 Custom Docker Image Layer

Document a pattern for operators to build a derivative Docker image that adds
`dotnet add package` to the Dockerfile.

**Rejected because:** This still requires a build step and a container registry.
It is a valid approach for operators who already maintain custom images, but it
does not satisfy the goal of deploy-time configuration without recompilation.

## 7. Open Questions

1. **Should `AllowedPlugins` being empty (with `PluginsPath` set) be a startup
   warning or a startup error?** Currently proposed as a warning (with no
   plugins loaded), on the assumption that an operator may set `PluginsPath` in
   a base config file and override `AllowedPlugins` per environment.

2. **Should DMS document a set of "tested compatible" sink versions?** This
   would give operators a known-good combination without implying DMS maintains
   or ships those sinks.

3. **Should hash verification be a v1 feature or deferred?** Including it in v1
   adds complexity but avoids a second release that changes the config schema
   for security-sensitive operators.
