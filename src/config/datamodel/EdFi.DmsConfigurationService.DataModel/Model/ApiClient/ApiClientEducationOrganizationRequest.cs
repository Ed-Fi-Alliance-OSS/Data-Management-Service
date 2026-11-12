// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentValidation.Results;

namespace EdFi.DmsConfigurationService.DataModel.Model.ApiClient;

/// <summary>
/// The education organization rules of an API client create or update request that need no
/// Application: the ones checked against the Application are applied when the request is resolved.
/// </summary>
public static class ApiClientEducationOrganizationRequest
{
    public const string EducationOrganizationIdsProperty = "EducationOrganizationIds";
    public const string InheritsProperty = "InheritsApplicationEducationOrganizations";

    public static IEnumerable<ValidationFailure> Validate(
        bool educationOrganizationIdsSupplied,
        long[]? educationOrganizationIds,
        bool inheritsSupplied,
        bool? inherits
    )
    {
        if (educationOrganizationIdsSupplied && educationOrganizationIds is null)
        {
            yield return new ValidationFailure(
                EducationOrganizationIdsProperty,
                "EducationOrganizationIds cannot be null. Omit it, or supply a non-empty array of education organization ids."
            );
        }
        else if (educationOrganizationIdsSupplied && educationOrganizationIds is [])
        {
            yield return new ValidationFailure(
                EducationOrganizationIdsProperty,
                "EducationOrganizationIds cannot be empty. Omit it to keep or inherit the education organizations; to remove a client's access, disable or delete it."
            );
        }

        if (inheritsSupplied && inherits is null)
        {
            yield return new ValidationFailure(
                InheritsProperty,
                "InheritsApplicationEducationOrganizations cannot be null. Omit it, or supply true or false."
            );
        }
        else if (inherits is false && !educationOrganizationIdsSupplied)
        {
            yield return new ValidationFailure(
                InheritsProperty,
                "InheritsApplicationEducationOrganizations false requires EducationOrganizationIds, the list to narrow the client to."
            );
        }
    }
}
