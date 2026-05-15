// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;

namespace EdFi.DataManagementService.Backend.External;

public static class RelationalAuthorizationParameterNameConstants
{
    public const string ClaimEducationOrganizationIds = nameof(
        RelationalAuthorizationContext.ClaimEducationOrganizationIds
    );
}

/// <summary>
/// Typed request-scoped authorization inputs for relational GET-many planning/execution.
/// </summary>
/// <param name="ClaimEducationOrganizationIds">
/// Unique token EdOrg claim ids sorted ascending for deterministic planning/binding.
/// </param>
public sealed record RelationalAuthorizationContext(IReadOnlyList<long> ClaimEducationOrganizationIds)
{
    public static RelationalAuthorizationContext Create(ClientAuthorizations clientAuthorizations)
    {
        ArgumentNullException.ThrowIfNull(clientAuthorizations);

        return new RelationalAuthorizationContext([
            .. clientAuthorizations
                .EducationOrganizationIds.Select(static educationOrganizationId =>
                    educationOrganizationId.Value
                )
                .Distinct()
                .Order(),
        ]);
    }
}

/// <summary>
/// Backend-local relational query request.
/// <see cref="IQueryRequest.ResourceInfo"/> already carries the fully qualified resource identity,
/// so the relational seam only adds metadata that should stay off the public Core.External contract.
/// </summary>
public interface IRelationalQueryRequest : IQueryRequest
{
    /// <summary>
    /// Typed request-scoped authorization inputs for relational GET-many planning/execution.
    /// </summary>
    RelationalAuthorizationContext AuthorizationContext { get; }

    /// <summary>
    /// The resolved runtime mapping set for the active relational request.
    /// Relational GET-many only executes after mapping-set resolution.
    /// </summary>
    MappingSet MappingSet { get; }

    /// <summary>
    /// Optional readable-profile projection inputs for relational query responses.
    /// Null when no readable profile applies to the request.
    /// </summary>
    ReadableProfileProjectionContext? ReadableProfileProjectionContext { get; }
}
