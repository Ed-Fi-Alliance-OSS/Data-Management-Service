# Ed-Fi API Custom Validation Abstractions

This package defines `ICustomResourceValidator`, the contract a district or vendor implements to add
custom resource validation to the Ed-Fi Data Management Service.

> **A validator is registered from a plugin.**
>
> The Data Management Service's write pipeline resolves registered `ICustomResourceValidator`
> instances and invokes those whose `AppliesTo` matches the current request's resource, and a
> validator reaches that pipeline through a plugin's `ContributeServices` hook. How a plugin is
> built, published, packaged, and delivered into a host is
> [PLUGINS.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md).
> Registering one is not inert at startup: a startup guard audits every registration and aborts
> startup if a validator is registered in a shape DMS would not resolve, so a registration mistake
> fails the process rather than passing silently.
> The release notes of a Data Management Service release state which contract versions that release
> carries, which is what tells you the version of this package to build against for a given host.
>
> **Links out of this readme point at the current documentation on `main`**, not at the
> documentation for the package version you resolved.

## What is here

- `ICustomResourceValidator` - the validator contract, declaring the resources it applies to and a
  single `ValidateAsync` entry point.
- `CustomValidationFailure` - a closed hierarchy of exactly two failure cases, `OnPath` for a
  failure tied to a JSON path and `OnResource` for a failure about the document as a whole.
- `ValidatedResource` - the applicability declaration, naming a project and a resource.
- `ValidatedResourceInfo`, `ValidationScope`, and `CustomValidationOperation` - the per-invocation
  inputs.

Each type carries its rules in its XML documentation, which ships with this package, so an IDE shows
them at the point of use.

## These types are the contract's own

The inputs a validator receives are declared by this package rather than borrowed from the Data
Management Service's internal model, and they are deliberately plain: strings rather than
branded types.

That is what keeps this package small and its dependency list empty, and it means the Data
Management Service can change its internal model without that being a breaking change to anything
compiled against this contract. `ValidatedResourceInfo` in particular is a projection of what the
service knows about a resource, carrying the fields a validator has a use for and nothing else.

## Registration shape

A validator is registered from a plugin's `ContributeServices` hook, and the shape below is the one
DMS accepts, because a startup guard audits these registrations and terminates the process rather
than letting a validator silently never run:

```csharp
services.TryAddEnumerable(
    ServiceDescriptor.Transient<ICustomResourceValidator, MyValidator>()
);
```

Transient, unkeyed, and an implementation type rather than a shared instance or a factory delegate.
`TryAddEnumerable` is the form this contract requires: it is fan-in, so every registered validator
runs and any number of plugins may contribute one.
To supply configuration, bind an options type and take `IOptions<T>` in the constructor.

## Packaging and delivery

This document is the validator contract. Getting an implementation of it into a running host — the
project settings, the publish command, the package shape, the compatibility surface, and what a
plugin may and may not register — is
[PLUGINS.md](https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/src/plugins/EdFi.Api.Plugins/PLUGINS.md).

## What is not here yet

This document does not yet describe the validation lifecycle or the error-reporting model, and the
registration note above is not a substitute for the full guide.
A full implementer guide is planned to accompany the release that adds host support.

## Dependencies

None beyond the .NET base class library.
The contract's own inputs are the only types this package carries, so taking it on commits an
implementer to nothing else.
