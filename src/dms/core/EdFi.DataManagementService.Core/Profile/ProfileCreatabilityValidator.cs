// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Core.Profile;

/// <summary>
/// Validates whether a profile's WriteContentType allows creating resources
/// by checking if required fields are excluded by the profile.
/// </summary>
internal interface IProfileCreatabilityValidator
{
    /// <summary>
    /// Determines which required fields would be excluded by the profile's WriteContentType rules.
    /// </summary>
    /// <param name="requiredFields">The list of required field names from the resource schema</param>
    /// <param name="writeContentType">The profile's WriteContentType definition</param>
    /// <param name="identityPropertyNames">Set of identity property names that are always preserved</param>
    /// <returns>A list of required field names that would be excluded by the profile</returns>
    IReadOnlyList<string> GetExcludedRequiredFields(
        IReadOnlyList<string> requiredFields,
        ContentTypeDefinition writeContentType,
        HashSet<string> identityPropertyNames
    );
}

/// <summary>
/// Implementation of profile creatability validation.
/// Checks if a profile's WriteContentType excludes any required fields,
/// which would prevent successful resource creation.
/// </summary>
internal class ProfileCreatabilityValidator : IProfileCreatabilityValidator
{
    /// <inheritdoc />
    public IReadOnlyList<string> GetExcludedRequiredFields(
        IReadOnlyList<string> requiredFields,
        ContentTypeDefinition writeContentType,
        HashSet<string> identityPropertyNames
    )
    {
        var excludedRequired = new List<string>();

        foreach (string requiredField in requiredFields)
        {
            // Identity fields are always preserved by the filter, so they're never excluded
            if (identityPropertyNames.Contains(requiredField))
            {
                continue;
            }

            // Check if this required field has an explicit collection or object rule
            // If it does, the field is handled by that rule and not excluded at the top level
            if (writeContentType.CollectionRulesByName.ContainsKey(requiredField))
            {
                continue;
            }

            if (writeContentType.ObjectRulesByName.ContainsKey(requiredField))
            {
                continue;
            }

            // Determine if the field would be excluded based on member selection
            bool isExcluded = writeContentType.MemberSelection switch
            {
                // IncludeOnly: field is excluded if NOT in the property name set
                MemberSelection.IncludeOnly => !writeContentType.PropertyNameSet.Contains(requiredField),

                // ExcludeOnly: field is excluded if IN the property name set
                MemberSelection.ExcludeOnly => writeContentType.PropertyNameSet.Contains(requiredField),

                // IncludeAll: no fields are excluded
                MemberSelection.IncludeAll => false,

                _ => false,
            };

            if (isExcluded)
            {
                excludedRequired.Add(requiredField);
            }
        }

        return excludedRequired;
    }
}
