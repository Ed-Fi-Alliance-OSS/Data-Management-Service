// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Decides whether a descriptor write would change the stored row.
/// </summary>
/// <remarks>
/// The identity fields compare case-insensitively and the descriptive fields case-sensitively. That
/// split follows from what identity means for a descriptor: <c>UX_Descriptor_UriLowered_Discriminator</c>
/// treats case variants as one identity, and POST-as-update deliberately keeps the stored casing
/// (<c>DescriptorWriteHandler.PreserveStoredDescriptorIdentity</c>), so a POST that differs from the
/// persisted row only in identity casing has nothing to write and must report a no-op rather than issue
/// an UPDATE that would be a no-op anyway. <c>Uri</c> is not compared separately: it is derived as
/// <c>{Namespace}#{CodeValue}</c>, so comparing those two covers it.
/// </remarks>
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

        return string.Equals(body.Namespace, persistedNamespace, StringComparison.OrdinalIgnoreCase)
            && string.Equals(body.CodeValue, persistedCodeValue, StringComparison.OrdinalIgnoreCase)
            && string.Equals(body.ShortDescription, persistedShortDescription, StringComparison.Ordinal)
            && string.Equals(body.Description, persistedDescription, StringComparison.Ordinal)
            && body.EffectiveBeginDate == persistedEffectiveBeginDate
            && body.EffectiveEndDate == persistedEffectiveEndDate;
    }
}
