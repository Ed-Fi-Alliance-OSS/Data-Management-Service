// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;

namespace EdFi.DataManagementService.Backend;

/// <summary>
/// Materializes the projection statement's result into rows, reading every row.
/// </summary>
/// <remarks>
/// A value that cannot be read as the type the row requires - a changed column type, or a null in a
/// column the row requires - surfaces as <see cref="InvalidCastException"/> (or the provider's own
/// conversion exception), which the staged read classifies as a permanent materialization mismatch.
/// The required-column null is checked explicitly so that it raises the same exception type on both
/// providers.
/// </remarks>
internal static class EducationOrganizationProjectionRowReader
{
    public static async Task<List<EducationOrganizationProjectionRow>> ReadAllAsync(
        DbDataReader reader,
        EducationOrganizationProjectionResultColumns columns,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(columns);

        int id = reader.GetOrdinal(columns.EducationOrganizationId.Value);
        int discriminator = reader.GetOrdinal(columns.Discriminator.Value);
        int name = reader.GetOrdinal(columns.NameOfInstitution.Value);
        int shortName = reader.GetOrdinal(columns.ShortNameOfInstitution.Value);
        int localEducationAgency = reader.GetOrdinal(columns.LocalEducationAgencyReference.Value);
        int parentLocalEducationAgency = reader.GetOrdinal(columns.ParentLocalEducationAgencyReference.Value);
        int educationServiceCenter = reader.GetOrdinal(columns.EducationServiceCenterReference.Value);
        int stateEducationAgency = reader.GetOrdinal(columns.StateEducationAgencyReference.Value);

        List<EducationOrganizationProjectionRow> rows = [];

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(
                new EducationOrganizationProjectionRow(
                    Required<long>(reader, id),
                    Required<string>(reader, discriminator),
                    Required<string>(reader, name),
                    Optional<string>(reader, shortName),
                    OptionalInt64(reader, localEducationAgency),
                    OptionalInt64(reader, parentLocalEducationAgency),
                    OptionalInt64(reader, educationServiceCenter),
                    OptionalInt64(reader, stateEducationAgency)
                )
            );
        }

        return rows;
    }

    private static T Required<T>(DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal)
            ? throw new InvalidCastException("A projection column the row requires is null.")
            : reader.GetFieldValue<T>(ordinal);
    }

    private static T? Optional<T>(DbDataReader reader, int ordinal)
        where T : class
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
    }

    private static long? OptionalInt64(DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<long>(ordinal);
    }
}
