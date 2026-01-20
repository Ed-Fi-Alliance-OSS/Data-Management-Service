// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EdFi.DataManagementService.Backend.Postgresql.TokenInfo;
using EdFi.DataManagementService.Core.Model;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Tests.Integration;

public class TokenInfoEducationOrganizationRepositoryTests : DatabaseTest
{
    private const long EducationServiceCenterId = 99001;
    private const long LocalEducationAgencyId = 99101;
    private const long SchoolId = 99201;

    [Test]
    public async Task Should_return_hierarchical_identifiers_for_requested_education_organizations()
    {
        var upsert = CreateUpsert();

        await upsert.Upsert(
            CreateUpsertRequest(
                resourceName: "EducationServiceCenter",
                documentUuidGuid: Guid.NewGuid(),
                referentialIdGuid: Guid.NewGuid(),
                edfiDocString: """{\"educationServiceCenterId\":99001,\"nameOfInstitution\":\"ESC-99\"}""",
                isInEducationOrganizationHierarchy: true,
                educationOrganizationId: EducationServiceCenterId
            ),
            Connection!,
            Transaction!
        );

        await upsert.Upsert(
            CreateUpsertRequest(
                resourceName: "LocalEducationAgency",
                documentUuidGuid: Guid.NewGuid(),
                referentialIdGuid: Guid.NewGuid(),
                edfiDocString: """{\"localEducationAgencyId\":99101,\"nameOfInstitution\":\"LEA-99\"}""",
                isInEducationOrganizationHierarchy: true,
                educationOrganizationId: LocalEducationAgencyId,
                parentEducationOrganizationId: EducationServiceCenterId
            ),
            Connection!,
            Transaction!
        );

        await upsert.Upsert(
            CreateUpsertRequest(
                resourceName: "School",
                documentUuidGuid: Guid.NewGuid(),
                referentialIdGuid: Guid.NewGuid(),
                edfiDocString: """{\"schoolId\":99201,\"nameOfInstitution\":\"School-99\"}""",
                isInEducationOrganizationHierarchy: true,
                educationOrganizationId: SchoolId,
                parentEducationOrganizationId: LocalEducationAgencyId
            ),
            Connection!,
            Transaction!
        );

        await CommitTestTransactionAsync(beginNewTransaction: false);

        var repository = new PostgresqlEducationOrganizationRepository(
            CreateDataSourceProvider(),
            CreateSqlAction(),
            NullLogger<PostgresqlEducationOrganizationRepository>.Instance
        );

        IReadOnlyList<TokenInfoEducationOrganization> result =
            await repository.GetEducationOrganizationsAsync(new[] { SchoolId, LocalEducationAgencyId });

        var school = result.Single(eo => eo.EducationOrganizationId == SchoolId);
        school.LocalEducationAgencyId.Should().Be(LocalEducationAgencyId);
        school.EducationServiceCenterId.Should().BeNull();

        var lea = result.Single(eo => eo.EducationOrganizationId == LocalEducationAgencyId);
        lea.LocalEducationAgencyId.Should().BeNull();
        lea.EducationServiceCenterId.Should().Be(EducationServiceCenterId);
    }
}
