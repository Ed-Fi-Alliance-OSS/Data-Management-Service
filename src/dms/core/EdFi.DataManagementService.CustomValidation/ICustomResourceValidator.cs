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
    /// The document to validate. It is never the raw submitted body byte-for-byte, and what it
    /// carries differs by whether a writable profile applied to the request.
    /// With no writable profile, it is the submitted body after date-format and date-time coercion
    /// and after version-metadata injection, so it carries a server-assigned "_lastModifiedDate" the
    /// client never sent; a validator doing strict property checking must expect to see it. Broader
    /// request-value coercion is applied too unless the deployment sets
    /// <c>AppSettings:BypassTypeCoercion</c>, which removes that step from the pipeline, so a
    /// validator must not assume every value has been coerced to its schema type.
    /// With a writable profile, it is the profile-shaped writable surface, which is built before
    /// version-metadata injection and therefore does not carry "_lastModifiedDate" at all.
    /// A validator that must behave the same either way should not depend on that property's
    /// presence. The document is received read-only as a contract rule that the type system cannot
    /// itself enforce; a validator must not mutate it.
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
    /// <remarks>
    /// A validator runs after the request's claim-set and resource-action authorization, but before
    /// the relationship, namespace, ownership and custom-view authorization that the backend decides
    /// while performing the write. Two consequences an implementer has to plan for: a validator is
    /// invoked - and any I/O it performs is performed - for documents whose caller will ultimately
    /// be refused, and when a validator returns failures the caller receives that 400 instead of the
    /// 403 they would otherwise have been given.
    /// </remarks>
    Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
        JsonNode document,
        ValidatedResourceInfo resource,
        CustomValidationOperation operation,
        ValidationScope scope,
        string traceId,
        CancellationToken cancellationToken
    );
}
