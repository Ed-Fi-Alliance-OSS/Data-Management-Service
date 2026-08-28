// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;

namespace EdFi.DataManagementService.CustomValidation;

/// <summary>
/// A custom, implementer-authored rule to be run against a document on the write path, in addition to
/// DMS's own core validation.
/// The write pipeline (POST and PUT) now resolves registered instances of this interface and invokes
/// those whose <see cref="AppliesTo"/> matches the current request's resource, but no supported
/// registration seam ships yet: an implementer has no documented way to register one, so in practice
/// nothing runs today. This declares the shape that support will be built against.
/// When host support lands, an implementation is compiled into the host deployment and registered
/// into DMS's composition; it is not loaded from a dropped-in assembly at runtime.
/// </summary>
public interface ICustomResourceValidator
{
    /// <summary>
    /// The resources this validator applies to, declared as data rather than discovered by inspecting
    /// the document.
    /// This property is read on every write request for every registered validator, so it must be
    /// cheap, synchronous, and free of I/O.
    /// Matching against the current request's resource is exact and ordinal, so a typo'd or
    /// wrong-cased entry never matches and this validator never runs for that resource. An entry is
    /// not otherwise checked here: an empty or malformed <see cref="ValidatedResource"/> constructs
    /// without complaint, and when host support lands an entry matching no resource in the effective
    /// schema surfaces as a startup warning rather than a failure, because an entry may legitimately
    /// name an extension resource a given deployment does not carry.
    /// </summary>
    IReadOnlyList<ValidatedResource> AppliesTo { get; }

    /// <summary>
    /// Validates a document on the write path, returning the failures found.
    /// </summary>
    /// <param name="document">
    /// The document to validate. This is the profile-effective body as it stands at this point in the
    /// write pipeline: after type coercion (date formats, date-times, and other request-value
    /// coercions) and after version-metadata injection, which writes a server-assigned
    /// "_lastModifiedDate" property into it. It is the profile-shaped writable surface when a
    /// writable profile applied, and the coerced, version-stamped submitted body otherwise - never
    /// the raw submitted body byte-for-byte. A validator doing strict property checking should
    /// expect to see "_lastModifiedDate". The document is received read-only as a contract rule that
    /// the type system cannot itself enforce; a validator must not mutate it.
    /// </param>
    /// <param name="resource">The resource the document belongs to.</param>
    /// <param name="operation">The write pipeline the document arrived through.</param>
    /// <param name="scope">The tenant and route qualifiers the write belongs to.</param>
    /// <param name="traceId">
    /// The trace identifier of the current request, for correlating a validator's own logging with
    /// the DMS request log.
    /// </param>
    /// <param name="cancellationToken">
    /// The request-abort token, so an I/O-bound validator can observe client disconnection.
    /// </param>
    /// <returns>
    /// The failures found, or an empty list when the document is valid. A null return is not a
    /// substitute for an empty list and is treated as a hard failure by the caller.
    /// </returns>
    Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
        JsonNode document,
        ValidatedResourceInfo resource,
        CustomValidationOperation operation,
        ValidationScope scope,
        string traceId,
        CancellationToken cancellationToken
    );
}
