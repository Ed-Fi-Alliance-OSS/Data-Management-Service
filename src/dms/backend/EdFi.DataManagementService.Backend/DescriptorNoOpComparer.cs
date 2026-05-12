// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend;

internal static class DescriptorNoOpComparer
{
    public static bool IsUnchanged(ExtractedDescriptorBody body, ExtractedDescriptorBody persisted)
    {
        ArgumentNullException.ThrowIfNull(persisted);

        return IsUnchanged(
            body,
            persisted.Namespace,
            persisted.CodeValue,
            persisted.ShortDescription,
            persisted.Description,
            persisted.EffectiveBeginDate,
            persisted.EffectiveEndDate
        );
    }

    public static bool IsUnchanged(
        ExtractedDescriptorBody body,
        string persistedNamespace,
        string persistedCodeValue,
        string? persistedShortDescription,
        string? persistedDescription,
        DateOnly? persistedEffectiveBeginDate,
        DateOnly? persistedEffectiveEndDate
    )
    {
        ArgumentNullException.ThrowIfNull(body);

        return string.Equals(body.Namespace, persistedNamespace, StringComparison.Ordinal)
            && string.Equals(body.CodeValue, persistedCodeValue, StringComparison.Ordinal)
            && string.Equals(body.ShortDescription, persistedShortDescription, StringComparison.Ordinal)
            && string.Equals(body.Description, persistedDescription, StringComparison.Ordinal)
            && body.EffectiveBeginDate == persistedEffectiveBeginDate
            && body.EffectiveEndDate == persistedEffectiveEndDate;
    }
}
