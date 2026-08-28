# Ed-Fi API Custom Validation Abstractions

This package defines `ICustomResourceValidator`, the contract a district or vendor implements to add
custom resource validation to the Ed-Fi Data Management Service.

> **The contract ships ahead of the host support for it.**
>
> The Data Management Service's write pipeline now resolves registered `ICustomResourceValidator`
> instances and invokes those whose `AppliesTo` matches the current request's resource, but no
> supported registration seam ships yet.
> An implementer has no documented way to register one, so in practice nothing runs today.
> Build against it to pin the contract and to compile early, but do not expect a registered
> validator to execute until a Data Management Service release announces custom-validation support.

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

## What is not here yet

This document does not yet describe how to register an implementation with a host, the validation
lifecycle, or the error-reporting model.
A full implementer guide is planned to accompany the release that adds host support.

## Dependencies

None beyond the .NET base class library.
The contract's own inputs are the only types this package carries, so taking it on commits an
implementer to nothing else.
