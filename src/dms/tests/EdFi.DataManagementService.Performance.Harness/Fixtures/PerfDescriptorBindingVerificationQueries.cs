// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

namespace EdFi.DataManagementService.Performance.Harness.Fixtures;

/// <summary>
/// Shared compact descriptor binding checks for the PostgreSQL and SQL Server fixture loaders.
/// </summary>
internal static class PerfDescriptorBindingVerificationQueries
{
    public static IEnumerable<PerfVerificationQuery> Create(PerfFixtureDefinition definition)
    {
        (string Table, string Column, string Resource)[] bindings =
        [
            ("Student", "BirthSexDescriptor_DescriptorId", PerfFixtureDefinition.SexDescriptorResource),
            (
                "StudentOtherName",
                "OtherNameTypeDescriptor_DescriptorId",
                PerfFixtureDefinition.OtherNameTypeDescriptorResource
            ),
            (
                "StudentIdentificationDocument",
                "IdentificationDocumentUseDescriptor_DescriptorId",
                PerfFixtureDefinition.IdentificationDocumentUseDescriptorResource
            ),
            (
                "StudentIdentificationDocument",
                "PersonalInformationVerificationDescriptor_DescriptorId",
                PerfFixtureDefinition.PersonalInformationVerificationDescriptorResource
            ),
            (
                "StudentPersonalIdentificationDocument",
                "IdentificationDocumentUseDescriptor_DescriptorId",
                PerfFixtureDefinition.IdentificationDocumentUseDescriptorResource
            ),
            (
                "StudentPersonalIdentificationDocument",
                "PersonalInformationVerificationDescriptor_DescriptorId",
                PerfFixtureDefinition.PersonalInformationVerificationDescriptorResource
            ),
            ("StudentVisa", "VisaDescriptor_DescriptorId", PerfFixtureDefinition.VisaDescriptorResource),
        ];
        foreach ((string table, string column, string resource) in bindings)
        {
            yield return new PerfVerificationQuery(
                $"{table}-{column}-compact-binding",
                $"""
                SELECT COUNT(*) FROM "edfi"."{table}" r
                INNER JOIN "dms"."Descriptor" descriptor ON descriptor."DescriptorId" = r."{column}"
                INNER JOIN "dms"."ResourceKey" rk ON rk."ResourceKeyId" = descriptor."ResourceKeyId"
                WHERE descriptor."DocumentId" = {definition.DescriptorDocumentIdFor(resource)}
                    AND rk."ProjectName" = '{PerfFixtureDefinition.ProjectName}'
                    AND rk."ResourceName" = '{resource}';
                """,
                definition.RowCount
            );
        }
    }
}
