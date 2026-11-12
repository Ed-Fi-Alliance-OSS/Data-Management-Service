// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model.ApiClient;
using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;

/// <summary>
/// The outcome of applying the client education organization rules to a request against the
/// client's Application, its new one on a move.
/// </summary>
internal abstract record ApiClientEducationOrganizationScopeResolution
{
    private ApiClientEducationOrganizationScopeResolution() { }

    /// <summary>
    /// The request is valid. <paramref name="Scope"/> is what the repository write requests, null
    /// when the request sent neither field. <paramref name="Inherits"/> is the flag the write
    /// leaves, and the sorted <paramref name="EducationOrganizationIds"/> the client's effective
    /// list, its token claim. <paramref name="StoredEducationOrganizationIds"/> are the rows the
    /// write leaves, which for an inheriting client kept on its Application are its existing rows:
    /// rows that an Application addition never refreshes, so they can lag its effective list.
    /// </summary>
    public sealed record Resolved(
        ApiClientEducationOrganizationScope? Scope,
        bool Inherits,
        long[] EducationOrganizationIds,
        long[] StoredEducationOrganizationIds
    ) : ApiClientEducationOrganizationScopeResolution
    {
        public string Claim => string.Join(",", EducationOrganizationIds);
    }

    /// <summary>
    /// The request breaks a rule that needs the Application: a 400 on the field concerned.
    /// </summary>
    public sealed record Invalid(ValidationFailure Failure) : ApiClientEducationOrganizationScopeResolution;

    /// <summary>
    /// The request sent neither field and moves a narrowed client to an Application without
    /// these of its education organizations: a 409.
    /// </summary>
    public sealed record ScopeConflict(long[] MissingEducationOrganizationIds)
        : ApiClientEducationOrganizationScopeResolution;
}

internal static class ApiClientEducationOrganizationScopeResolver
{
    /// <summary>
    /// Applies the client education organization rules, after the request has passed
    /// <see cref="ApiClientEducationOrganizationRequest.Validate"/>.
    /// </summary>
    /// <param name="requestedIds">The request's list, or null when the request omitted it.</param>
    /// <param name="inherits">The request's flag, or null when the request omitted it.</param>
    /// <param name="applicationIds">The current list of the client's Application, its new one on a move.</param>
    /// <param name="current">The client's state on update; null on create.</param>
    /// <param name="moves">Whether the update moves the client to another Application.</param>
    public static ApiClientEducationOrganizationScopeResolution Resolve(
        long[]? requestedIds,
        bool? inherits,
        IReadOnlyCollection<long> applicationIds,
        ApiClientEducationOrganizationState? current,
        bool moves = false
    )
    {
        long[] sortedApplicationIds = [.. applicationIds.Order()];

        if (requestedIds is not null)
        {
            long[] distinctIds = [.. requestedIds.Distinct().Order()];

            if (inherits is true)
            {
                // A GET-then-PUT round trip of an inheriting client sends its effective list back
                return distinctIds.ToHashSet().SetEquals(applicationIds)
                    ? Inheriting()
                    : new ApiClientEducationOrganizationScopeResolution.Invalid(
                        new ValidationFailure(
                            ApiClientEducationOrganizationRequest.EducationOrganizationIdsProperty,
                            "EducationOrganizationIds must equal the Application's education organizations when InheritsApplicationEducationOrganizations is true. Omit EducationOrganizationIds to inherit, or send false to narrow the client."
                        )
                    );
            }

            long[] missingIds = [.. distinctIds.Except(applicationIds)];
            return missingIds.Length > 0
                ? NotOnApplication(missingIds)
                : new ApiClientEducationOrganizationScopeResolution.Resolved(
                    new ApiClientEducationOrganizationScope.Narrow(distinctIds),
                    false,
                    distinctIds,
                    distinctIds
                );
        }

        if (inherits is true)
        {
            return Inheriting();
        }

        // Neither field: a create inherits, and an update keeps the client's own scope. The
        // repository applies that default itself, so no scope is requested. A create and a move
        // store the Application's list; an inheriting client kept on its Application keeps its rows.
        if (current is null || current.InheritsApplicationEducationOrganizations)
        {
            return new ApiClientEducationOrganizationScopeResolution.Resolved(
                null,
                true,
                sortedApplicationIds,
                current is null || moves
                    ? sortedApplicationIds
                    : [.. current.StoredEducationOrganizationIds.Order()]
            );
        }

        long[] currentIds = [.. current.StoredEducationOrganizationIds.Order()];
        long[] currentMissingIds = [.. currentIds.Except(applicationIds)];
        return currentMissingIds.Length > 0
            ? new ApiClientEducationOrganizationScopeResolution.ScopeConflict(currentMissingIds)
            : new ApiClientEducationOrganizationScopeResolution.Resolved(null, false, currentIds, currentIds);

        ApiClientEducationOrganizationScopeResolution Inheriting() =>
            new ApiClientEducationOrganizationScopeResolution.Resolved(
                new ApiClientEducationOrganizationScope.Inherit(),
                true,
                sortedApplicationIds,
                sortedApplicationIds
            );
    }

    /// <summary>
    /// The 400 for requested education organizations that are not on the client's Application.
    /// </summary>
    public static ApiClientEducationOrganizationScopeResolution.Invalid NotOnApplication(long[] missingIds) =>
        new(
            new ValidationFailure(
                ApiClientEducationOrganizationRequest.EducationOrganizationIdsProperty,
                $"EducationOrganizationIds must be education organizations of the API client's Application. Not on the Application: {string.Join(", ", missingIds)}."
            )
        );
}
