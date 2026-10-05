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
/// <para>
/// Names returned as UTF-16 bytes (<see cref="EducationOrganizationProjectionNameEncoding.Utf16LittleEndianBytes"/>)
/// are rebuilt code unit by code unit, without decoding, so a lone surrogate reaches the handler's
/// validation exactly as stored. Nothing here validates text; that is the handler's job, after the
/// transaction ends.
/// </para>
/// </remarks>
internal static class EducationOrganizationProjectionRowReader
{
    public static async Task<List<EducationOrganizationProjectionRow>> ReadAllAsync(
        DbDataReader reader,
        EducationOrganizationProjectionResultColumns columns,
        EducationOrganizationProjectionNameEncoding nameEncoding,
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
                    RequiredName(reader, name, nameEncoding),
                    OptionalName(reader, shortName, nameEncoding),
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

    private static string RequiredName(
        DbDataReader reader,
        int ordinal,
        EducationOrganizationProjectionNameEncoding encoding
    )
    {
        return reader.IsDBNull(ordinal)
            ? throw new InvalidCastException("A projection column the row requires is null.")
            : Name(reader, ordinal, encoding);
    }

    private static string? OptionalName(
        DbDataReader reader,
        int ordinal,
        EducationOrganizationProjectionNameEncoding encoding
    )
    {
        return reader.IsDBNull(ordinal) ? null : Name(reader, ordinal, encoding);
    }

    private static string Name(
        DbDataReader reader,
        int ordinal,
        EducationOrganizationProjectionNameEncoding encoding
    )
    {
        return encoding == EducationOrganizationProjectionNameEncoding.Utf16LittleEndianBytes
            ? FromUtf16LittleEndian(reader.GetFieldValue<byte[]>(ordinal))
            : reader.GetFieldValue<string>(ordinal);
    }

    /// <summary>
    /// The code units the bytes hold, each from two bytes, low byte first, whatever the host's byte
    /// order. No decoder is involved, so no unit is replaced or dropped.
    /// </summary>
    internal static string FromUtf16LittleEndian(byte[] bytes)
    {
        if (bytes.Length % 2 != 0)
        {
            // An nvarchar value always has an even length; anything else is not what the statement returns.
            throw new InvalidCastException("A projection name column did not hold whole UTF-16 code units.");
        }

        return string.Create(
            bytes.Length / 2,
            bytes,
            static (units, source) =>
            {
                for (int index = 0; index < units.Length; index++)
                {
                    units[index] = (char)(source[2 * index] | (source[(2 * index) + 1] << 8));
                }
            }
        );
    }

    private static long? OptionalInt64(DbDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<long>(ordinal);
    }
}
