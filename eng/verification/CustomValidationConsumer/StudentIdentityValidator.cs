// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.CustomValidation;
using Microsoft.Extensions.Options;

namespace CustomValidationConsumer;

/// <summary>
/// An implementer-authored <see cref="ICustomResourceValidator"/> that stands in for a district's
/// external student identity lookup, the scenario EdFi.Api.CustomValidation's design was written
/// against.
/// This is a verification fixture, not a production validator: it takes a dependency on
/// <see cref="IOptions{ExternalIdentityOptions}"/> so this project exercises
/// Microsoft.Extensions.Options through the abstractions package rather than only the BCL types
/// the interface itself requires, and it performs no real network I/O.
/// </summary>
public sealed class StudentIdentityValidator : ICustomResourceValidator
{
    private readonly ExternalIdentityOptions _options;

    public StudentIdentityValidator(IOptions<ExternalIdentityOptions> options)
    {
        _options = options.Value;
    }

    public IReadOnlyList<ValidatedResource> AppliesTo { get; } =
        ImmutableArray.Create(new ValidatedResource("Ed-Fi", "Student"));

    public async Task<IReadOnlyList<CustomValidationFailure>> ValidateAsync(
        JsonNode document,
        ValidatedResourceInfo resource,
        CustomValidationOperation operation,
        ValidationScope scope,
        string traceId,
        CancellationToken cancellationToken
    )
    {
        // Stands in for the real external identity lookup against _options.EndpointUrl. This
        // consumer proves EdFi.Api.CustomValidation compiles against the documented registration
        // sample end to end; it does not call out over the network.
        await Task.CompletedTask;

        // This project is the package's compile-check: it proves an outside project can restore
        // EdFi.Api.CustomValidation and implement the contract against it. It touches every
        // published type on purpose, but it is not what keeps the surface honest, since a comment
        // claiming full coverage stays green after the surface moves out from under it.
        // A failure about the document as a whole, with no JSON path to point at, which is what
        // OnResource exists for.
        if (string.IsNullOrWhiteSpace(_options.EndpointUrl))
        {
            return ImmutableArray.Create<CustomValidationFailure>(
                new CustomValidationFailure.OnResource(
                    $"The external student identity endpoint is not configured, so {resource.ResourceName} cannot be verified."
                )
            );
        }

        var studentUniqueId = document["studentUniqueId"]?.GetValue<string>();

        // A create must carry a resolvable identity; an update of an existing record may not
        // restate one.
        bool identityRequired = operation == CustomValidationOperation.Upsert;

        if (identityRequired && string.IsNullOrWhiteSpace(studentUniqueId))
        {
            string qualifiers = string.Join(
                ", ",
                scope.RouteQualifiers.Select(qualifier => $"{qualifier.Key}={qualifier.Value}")
            );

            return ImmutableArray.Create<CustomValidationFailure>(
                new CustomValidationFailure.OnPath(
                    "$.studentUniqueId",
                    $"studentUniqueId could not be verified against {_options.EndpointUrl} "
                        + $"for {resource.ProjectName}.{resource.ResourceName} "
                        + $"v{resource.ResourceVersion} (tenant: {scope.Tenant ?? "none"}, "
                        + $"qualifiers: {qualifiers}, trace: {traceId})."
                )
            );
        }

        return Array.Empty<CustomValidationFailure>();
    }
}
