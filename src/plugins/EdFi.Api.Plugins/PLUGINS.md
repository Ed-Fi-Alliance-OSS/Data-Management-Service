# Ed-Fi API Plugins

This package defines `EdFiApiPlugin`, the base class a district or vendor implements to extend the
Ed-Fi Data Management Service or the Ed-Fi DMS Configuration Service without rebuilding either one.

> **This readme is a placeholder.**
>
> The plugin mechanism is being built one story at a time, and this file is packed from the first of
> them so that the package metadata is complete and asserted from the outset. It is replaced with the
> real implementer guide by the documentation story of the same epic, which is also the story that
> writes the operator-facing chapter this file will link to.
>
> Nothing that loads a plugin ships yet. No published Ed-Fi image contains a plugin loader, so a
> subclass of `EdFiApiPlugin` compiles today but is loaded by nothing. Build against this package to
> pin the contract and to compile early, and expect the release notes of a Data Management Service
> release to announce when plugin loading is supported and to state which contract versions that
> release carries.

## What is here

One public type, `EdFiApiPlugin`, an abstract class with:

- `Name`, an abstract property that must return the name of the directory the plugin is deployed
  into. The host verifies this at load time and a mismatch is fatal.
- `ContributeServices`, a virtual method the host calls before it builds its container. The base
  implementation does nothing, so a plugin overrides only what it needs.

## Versioning

This package carries its own semantic version, independent of the Data Management Service release
version, because the host compares contract assembly versions when it decides whether a plugin can
run. The version moves when the public surface moves, and only then.

The compatibility policy is additive-only for the life of the package: new virtual members with no-op
bodies, never a new abstract member, never a signature change, never a removal. A plugin compiled
against an older version of this contract runs on a newer host without being rebuilt. A plugin
compiled against a newer version than the host carries is refused at load, by name, rather than
failing later inside a hook.

## License

Licensed under the Apache License, Version 2.0. See the LICENSE and NOTICES files in the project root
for more information.
