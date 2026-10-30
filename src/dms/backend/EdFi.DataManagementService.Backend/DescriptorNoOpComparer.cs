// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Decides whether a descriptor write changes the stored representation. Identity is not compared here:
/// callers establish it first through the database's descriptor-identity verdict and pass a body that
/// already carries the persisted <c>Namespace</c> and <c>CodeValue</c>, and a PUT whose identity changed
/// is rejected before this runs. Only the descriptive fields compare, ordinally.
/// </summary>
internal static class DescriptorNoOpComparer
{
    public static bool IsUnchanged(ExtractedDescriptorBody body, ExtractedDescriptorBody persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);

        return IsUnchanged(
            body,
            persisted.ShortDescription,
            persisted.Description,
            persisted.EffectiveBeginDate,
            persisted.EffectiveEndDate
        );
    }

    public static bool IsUnchanged(
        ExtractedDescriptorBody body,
        string? persistedShortDescription,
        string? persistedDescription,
        DateOnly? persistedEffectiveBeginDate,
        DateOnly? persistedEffectiveEndDate
    )
    {
        ArgumentNullException.ThrowIfNull(body);

        return string.Equals(body.ShortDescription, persistedShortDescription, StringComparison.Ordinal)
            && string.Equals(body.Description, persistedDescription, StringComparison.Ordinal)
            && body.EffectiveBeginDate == persistedEffectiveBeginDate
            && body.EffectiveEndDate == persistedEffectiveEndDate;
    }
}
