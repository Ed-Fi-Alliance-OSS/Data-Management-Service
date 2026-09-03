---
jira: TBD
source_spike: DMS-1413
depends_on: 01, DMS-1498, DMS-1499
---

# Story: Declare the Identity Plugin Contract Registry Entry

## Description

Add `IIdentityService` to the DMS plugin contract registry as a replace-cardinality contract.
This story depends on the plugin recording wrapper and startup loading work from DMS-1498 and DMS-1499.

## Acceptance Criteria

- `DmsPluginContracts.Registry` declares `IIdentityService` as `Replace`.
- `ContractAssemblyNames` includes the assembly name `EdFi.DataManagementService.Identity`, not the package id `EdFi.Api.Identity`.
- A DMS image build after DMS-1499 carries `EdFi.DataManagementService.Identity.dll` with `AssemblyVersion` equal to the identity contract package version.
- Host default plus no plugin is valid.
- Host default plus one plugin replacement is valid.
- Two plugin replacements are fatal and the error names both plugins.
- A plugin registering `IIdentityService` is admitted by the declared-contract exemption.
- A plugin that uses `TryAdd` and silently keeps the host default is documented as an implementer error; the registry does not pretend to observe a descriptor the wrapper cannot see.

## Tasks

1. Add the registry entry after the plugin foundation exists.
2. Add cardinality, assembly-name, and runtime image assembly-version tests.
3. Add startup failure tests for duplicate replacements.
4. Add implementer documentation hooks for `Add` versus `TryAdd`.
