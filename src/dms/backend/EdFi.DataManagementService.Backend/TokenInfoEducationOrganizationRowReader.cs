// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.External.Backend;

namespace EdFi.DataManagementService.Backend;

internal static class TokenInfoEducationOrganizationRowReader
{
    public static async Task<IReadOnlyList<TokenInfoEducationOrganization>> ReadAsync(
        IRelationalCommandReader reader,
        CancellationToken cancellationToken
    )
    {
        ArgumentNullException.ThrowIfNull(reader);

        List<TokenInfoEducationOrganization> rows = [];

        var educationOrganizationIdOrdinal = reader.GetOrdinal(
            TokenInfoEducationOrganizationResultColumns.Default.EducationOrganizationId.Value
        );
        var nameOfInstitutionOrdinal = reader.GetOrdinal(
            TokenInfoEducationOrganizationResultColumns.Default.NameOfInstitution.Value
        );
        var discriminatorOrdinal = reader.GetOrdinal(
            TokenInfoEducationOrganizationResultColumns.Default.Discriminator.Value
        );
        var ancestorDiscriminatorOrdinal = reader.GetOrdinal(
            TokenInfoEducationOrganizationResultColumns.Default.AncestorDiscriminator.Value
        );
        var ancestorEducationOrganizationIdOrdinal = reader.GetOrdinal(
            TokenInfoEducationOrganizationResultColumns.Default.AncestorEducationOrganizationId.Value
        );

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(
                new TokenInfoEducationOrganization(
                    reader.GetFieldValue<long>(educationOrganizationIdOrdinal),
                    reader.GetFieldValue<string>(nameOfInstitutionOrdinal),
                    reader.GetFieldValue<string>(discriminatorOrdinal),
                    reader.GetFieldValue<string>(ancestorDiscriminatorOrdinal),
                    reader.GetFieldValue<long>(ancestorEducationOrganizationIdOrdinal)
                )
            );
        }

        return rows;
    }
}
