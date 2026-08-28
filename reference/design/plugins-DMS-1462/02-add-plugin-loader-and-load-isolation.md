---
jira: TBD
jira_url: TBD
epic: TBD
source_spike: DMS-1462
---

# Story: Add the Plugin Loader, Discovery, and Load Isolation

## Description

With the contract in place, nothing yet reads `Plugins:Allowed`, finds a plugin directory, or loads an assembly out of it.
This story adds the loader in `EdFi.Api.Plugins.Hosting`: allowlist binding, allowlist-driven discovery, one non-collectible `AssemblyLoadContext` per plugin with host-first resolution, the contract-skew and refused-version fatals, and `Console.Error` diagnostics, per:

- `reference/design/plugins-DMS-1462/design.md` ("### The Unit of Delivery", "### Discovery", "### Load Isolation", "### Configuration Surface", "### Startup Failure Semantics" for the loader-side rows, "### Observability" for the pre-logger channel)

The loader is host-agnostic.
It takes an `IConfiguration`, returns the loaded plugin instances in allowlist order, writes its own outcomes to `Console.Error`, and throws a `PluginLoadException` on any fatal.
It knows nothing about `RunBootstrapPhase`, Serilog, or either host's `Program.cs`; draft 04 wires it in.

Host-first resolution is the only subtle code in the spine, and its two decisions, serve from the host whatever the host can resolve and treat a refused version as fatal rather than falling back, are pinned by probe tests rather than assumed.
This story does not invoke `ContributeServices` through a recording wrapper; it returns the instances and draft 03 owns the invocation.

## Acceptance Criteria

- A `PluginsOptions` type binds the `Plugins` section with `Directory` (default `/app/plugins`, resolved against `AppContext.BaseDirectory` when relative) and `Allowed` as a `string`. `Allowed` is split on commas once at bind time, each name trimmed, empty elements dropped, and the resulting order asserted to be the order written. A name repeated before or after trimming is fatal, naming the duplicate. `Plugins__Allowed` set as an environment variable binds to the same ordered list as the JSON form.
- For each name in order, the loader resolves `<Directory>/<Name>/<Name>.dll` and `<Directory>/<Name>/<Name>.deps.json`. A missing root with a non-empty allowlist, a missing directory, a missing entry assembly, and a missing `.deps.json` are each fatal, naming the expected path. A missing root with an empty or absent allowlist returns an empty result and writes nothing.
- Before loading any type, the loader reads the entry assembly's references through `System.Reflection.Metadata` and compares the referenced `EdFi.Api.Plugins` version to the version the host carries. A reference higher than the host's is fatal, naming the plugin, the version required, and the version the host carries, and a test asserts the failure came from the metadata read rather than from a type load by using a fixture whose type load would produce a different, recognizable error.
- A `.deps.json` whose `runtimeTarget` names a runtime identifier (a self-contained publish) is fatal, naming the plugin.
- Each plugin loads into its own `AssemblyLoadContext` named for the plugin, non-collectible, backed by an `AssemblyDependencyResolver` over the plugin's `.deps.json`. `Load` first calls `Default.LoadFromAssemblyName`; `FileNotFoundException` falls through to the resolver, `FileLoadException` is fatal naming the plugin, the assembly, the version requested, and the version the host carries.
- The loader reflects over the entry assembly's exported types for public non-abstract `EdFiApiPlugin` subclasses with a public parameterless constructor. Zero and more than one are each fatal, naming what was found. The one match is constructed and its `Name` compared to the directory name; a mismatch is fatal naming both.
- The loader writes to `Console.Error` one line per allowlisted name in order, whether it succeeded or failed, and on failure the fatal reason. It also records, for every assembly it served host-first whose version differs from the version the plugin's `.deps.json` declared, the name and both versions, on a `LoadedPlugin` record the caller can log once a logger exists. `LoadedPlugin` also carries the SHA-256 of the entry assembly file as read from disk at load.
- Fixture plugin directories are built as test assets under `src/plugins/EdFi.Api.Plugins.Hosting.Tests.Unit/Fixtures/`: a well-formed plugin; one with no subclass; one with two; one whose `Name` disagrees with its directory; one whose entry assembly is corrupt; one declaring a self-contained runtime target; one with no `.deps.json`; one carrying a private copy of `EdFi.Api.Plugins`, which asserts the host's copy is served; two plugins carrying different major versions of one third-party dependency the host does not carry, which asserts each gets its own; one built against a contract version higher than the host's; and one declaring a higher version of an assembly the host carries. Each fatal row is asserted to throw rather than log, and the well-formed cases are asserted to return one instance each in allowlist order.
- The `IChangeToken` regression test from design.md "### Load Isolation": a fixture plugin that binds options through `IOptions<T>` from a configuration section is loaded, `ContributeServices` is invoked against a real `ServiceCollection`, the provider is built, and the options resolve. A second copy of the loader with an enumerated shared set substituted for host-first is asserted to fail this case with `TypeLoadException`, so the test pins the decision rather than restating it.
- A probe test records what the default binder does when a plugin references a higher version of a `Microsoft.Extensions.*` assembly the host carries, and the fatal-on-refused-version rule is asserted against that recorded behavior.
- A test asserts a plugin compiled against contract `1.0.0` loads and runs against a contract assembly at `1.1.0` carrying an added no-op virtual, built as a test asset.
- `dotnet test src/plugins/EdFi.Api.Plugins.Hosting.Tests.Unit` passes, and the project is added to all three solutions.

## Tasks

1. Add `PluginsOptions` and its binder with the trim-split-reject-duplicates rule.
2. Add `PluginLoadContext` exactly as design.md "### Load Isolation" shows it, including the `FileLoadException` rethrow.
3. Add `PluginLoader.Load(IConfiguration)` returning `IReadOnlyList<LoadedPlugin>`, with the metadata-read skew check ahead of any type load, the `.deps.json` self-contained check, discovery, `Name` verification, and `Console.Error` reporting.
4. Add `PluginLoadException` carrying plugin name and the structured reason.
5. Build the fixture plugin directories as test assets through a small MSBuild target or script that publishes each fixture project `--no-self-contained` into the test output, including the two version-skewed fixtures built against versions the host does not carry.
6. Write the fatal-row tests, the isolation tests, the `IChangeToken` regression test with its enumerated-set control, the binder probe test, and the older-plugin-on-newer-host test.
