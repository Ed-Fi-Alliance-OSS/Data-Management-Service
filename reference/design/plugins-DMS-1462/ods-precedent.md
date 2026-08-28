# ODS/API Plugin Precedent

## Purpose

Evidence for [design.md](./design.md).
Ed-Fi ODS/API already solves the problem DMS-1462 poses, and it solves it twice.
This document records what it does, where, and what the DMS design adopts, narrows, or refuses.

It is a survey, not a specification.
Nothing here is a DMS decision; every decision it supports lives in design.md.

## Provenance

Paths beginning `EdFi.Ods.` are relative to `Application/` in the **Ed-Fi-ODS** repository at commit `90c75ffed3fc2bc0dafa14a2600b3a0d050f82e9`.
One seam's invocation lives in the separate **Ed-Fi-ODS-Implementation** repository, at commit `37ff595c171b73e524d96b13103ef9ae01712beb`, and is marked where it appears.
Every line number below was read at the pinned commit of the repository it belongs to.

---

## Two Seams, One Folder

The easiest mistake when porting this to DMS is to notice one seam and miss the other.
Both read from the same plugin folder and fire at different points in host startup.

### 1. Post-host-build DI composition

Settings bind from `Plugin:Folder` and `Plugin:Scripts`.

| Element | Location |
| --- | --- |
| The bound settings object | `EdFi.Ods.Common/Configuration/Plugin.cs` |
| Where it binds | `EdFi.Ods.Api/Startup/OdsStartupBase.cs:137` |
| Folder resolution, four fallbacks | `EdFi.Ods.Api/Helpers/AssemblyLoaderHelper.cs:90-131` - rooted at `:97`, project-relative at `:104`, executable-relative at `:115`, working-directory-relative at `:123`, then the raw setting returned unresolved at `:130` |
| Recursive assembly glob | `AssemblyLoaderHelper.cs:274`, `Directory.GetFiles(pluginFolder, "*.dll", SearchOption.AllDirectories)` |
| Discovery by marker interface | `AssemblyLoaderHelper.cs:353`, comparing `AssemblyQualifiedName` against `IPluginMarker` |
| The marker itself, an empty interface | `EdFi.Ods.Common/Extensibility/IPluginMarker.cs` |
| Load into the default context | `OdsStartupBase.cs:562-568`, with the comment "IMPORTANT: Load the plug-in assembly into the Default context" |
| MVC `ApplicationPart` contribution | `OdsStartupBase.cs:209-213` |
| Module failures caught and skipped | `OdsStartupBase.cs:379-386` |
| Replace semantics by a three-value module ordering | `EdFi.Ods.Api/Helpers/TypeHelper.cs:22-38`, the ranking expression at `:32-36`; the assembly enumeration the stable sort falls back on at `:42` |
| Duplicate extension schema names detected | `AssemblyLoaderHelper.cs:255-262`, over `ApiModel-EXTENSION.json` files globbed at `:224` |

### 2. Pre-host-build configuration injection

| Element | Location |
| --- | --- |
| The interface, one method `ConfigureHost(IHostBuilder)` | `EdFi.Ods.Common/IHostConfigurationActivity.cs:13` |
| Its invocation | **Ed-Fi-ODS-Implementation** at `37ff595c171b73e524d96b13103ef9ae01712beb`, `Application/EdFi.Ods.WebApi/Program.cs:78-85`, via `TypeHelper.GetAssemblyTypes<IHostConfigurationActivity>()` at `:78`, `Activator.CreateInstance` at `:84`, and `plugin!.ConfigureHost(hostBuilder)` at `:85`, before the host is built |

This is the seam behind the documented [external configuration of ODS connection strings](https://docs.ed-fi.org/reference/ed-fi-api/platform-dev-guide/configuration/external-configuration-of-ods-connection-strings/).
It exists because a configuration provider such as Azure Key Vault or AWS SSM must be added before configuration is read, and no amount of DI extensibility reaches that point.

Its shape feeds `OdsInstances:<id>:ConnectionString` in single-tenant deployments and `Tenants:<name>:OdsInstances:<id>:ConnectionString` in multi-tenant ones, and requires `EdFi_Admin.OdsInstances.ConnectionString` to be NULL for the override to apply.

---

## Disposition

| ODS property | Evidence | Disposition for DMS |
| --- | --- | --- |
| A configured plugin folder is the unit of extensibility | `Plugin.cs`, bound at `OdsStartupBase.cs:137` | **Adopted.** `Plugins:Directory` |
| Folder resolution tries rooted, then project-relative, then executable-relative, then working-directory-relative | `AssemblyLoaderHelper.cs:90-131` | **Refused.** Four fallbacks make "which directory did it actually use" a diagnostic question, and the fourth depends on the process's working directory, which a container entry point can change. DMS resolves relative paths against `AppContext.BaseDirectory` and nothing else |
| Recursive `*.dll` glob, then filter by marker | `AssemblyLoaderHelper.cs:274`, `:353` | **Refused.** Allowlist-driven resolution of one entry assembly per named directory. Nothing unasked-for is opened |
| Empty marker interface for discovery | `IPluginMarker.cs` | **Adopted in spirit.** `EdFiApiPlugin` carries `Name`, so it also asserts identity |
| Probe in a throwaway context, then force a collection to unload it | `AssemblyLoaderHelper.cs:276-320`, `OdsStartupBase.cs:571-578` | **Not needed.** The probe exists because ODS does not know which DLL is the plugin; naming the entry assembly by convention removes the question |
| Load plugin assemblies into `AssemblyLoadContext.Default` | `OdsStartupBase.cs:562-568` | **Refused.** Per-plugin context with host-first resolution. A plugin cannot displace a host assembly, and a plugin-private dependency stays private |
| Module construction and registration failures caught, logged, skipped | `OdsStartupBase.cs:379-386` | **Refused.** Fatal. A silently absent secrets, identity, or validation plugin changes correctness or security posture |
| Replace semantics by module ordering, `ICustomModule` last, then `Override`-prefixed names, then the rest | `TypeHelper.cs:32-36` | **Refused.** The rank has three values and `OrderBy` is stable, so two vendor plugin modules both implementing `ICustomModule` tie and the winner is whichever assembly `AppDomain.CurrentDomain.GetAssemblies()` (`:42`) enumerated first. Class names decide only the `Override` band, not which plugin wins. Two plugins claiming one replace-contract is fatal in DMS and names both |
| A separate pre-host-build seam taking `IHostBuilder` | `IHostConfigurationActivity.cs:13` | **Adopted and narrowed.** Phase A exists and takes `IConfigurationBuilder`, so a plugin can add configuration sources and nothing else |
| The pre-host seam's invocation lives in a different repository from its definition | Implementation repo `EdFi.Ods.WebApi/Program.cs:78-84` | **Refused.** Both phases are invoked from `Program.cs` in this repository, from one load |
| Plugins contribute MVC `ApplicationPart`s | `OdsStartupBase.cs:209-213` | **Deferred.** No type on DMS-1462's table adds endpoints; DMS-1412 states the Identity API surface is DMS-owned with the plugin supplying the implementation behind it |
| Identity: a no-op default behind a feature flag, displaced by a plugin | `EdFi.Ods.Features/Container/Modules/IdentityModule.cs:20` gates on `ApiFeature.IdentityManagement`, `:28` registers `UnimplementedIdentityService` | **Adopted in shape**, with replace-conflict fatal rather than last-wins. The contract belongs to the identity companion document |
| Duplicate extension schema names detected and thrown on | `AssemblyLoaderHelper.cs:255-262` | **Adopted in spirit.** A conflict between two plugins is fatal and names both |

---

## What the Survey Settled

**The two-phase split is the single most useful thing here.**
A design that noticed only the marker-interface scan would have built a DI-composition loader and then discovered, at the point of designing secrets, that configuration providers must be contributed before configuration is read and that no DI extensibility reaches them.
ODS's answer is a second mechanism whose invocation lives in a second repository.
DMS's is a second hook invoked from the same load, in the same file, in this repository.

**ODS's default-context load is the standing objection to drop-in delivery, and it is answerable.**
In the default context the first assembly with a given simple name wins for the life of the process, so a plugin shipping one version of a dependency into a host that has loaded another either gets the host's copy silently or fails to load, depending on an ordering `Directory.GetFiles` does not define.
Both consequences land on the operator.
Per-plugin contexts remove them, and design.md's host-first rule keeps shared type identity intact while doing it.

**Two of ODS's choices are load-bearing and should not be copied.**
Catching and skipping a failed module turns a broken extension into silently absent behavior.
Ordering modules to decide which registration wins makes the answer depend on assembly load order once two vendors are in the same rank band, which is a thing neither the operator nor either vendor can see or set.
Both are refused in design.md, with the reasoning recorded there rather than here.
