// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;

namespace EdFi.DataManagementService.Backend.Plans;

internal static class ReferenceDerivedSourcePathValidator
{
    public static void ValidateOrThrow(
        string planKind,
        DbTableName table,
        DbColumnModel columnModel,
        string subjectDescription,
        ReferenceDerivedValueSourceMetadata referenceDerivedSource
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planKind);
        ArgumentNullException.ThrowIfNull(columnModel);
        ArgumentException.ThrowIfNullOrWhiteSpace(subjectDescription);

        var sourcePath = columnModel.SourceJsonPath?.Canonical ?? "<null>";

        if (columnModel.SourceJsonPath?.Canonical == referenceDerivedSource.ReferenceJsonPath.Canonical)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Cannot compile {planKind} for '{table}': reference-derived source mismatch for {subjectDescription} '{columnModel.ColumnName.Value}'. "
                + $"DbColumnModel.SourceJsonPath '{sourcePath}' does not match ReferenceIdentityBinding.ReferenceJsonPath '{referenceDerivedSource.ReferenceJsonPath.Canonical}'."
        );
    }
}
