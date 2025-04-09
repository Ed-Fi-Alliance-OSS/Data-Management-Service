// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Backend;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Postgresql.Test.Integration;

public class EducationOrganizationHierarchyTests : DatabaseTest
{
    [TestFixture]
    public class Given_An_Upsert_Of_An_EducationOrganization : EducationOrganizationHierarchyTests
    {
        private UpsertResult? _upsertResult;

        [SetUp]
        public async Task Setup()
        {
            IUpsertRequest upsertRequest = CreateUpsertRequest(
                "EducationOrganization",
                Guid.NewGuid(),
                Guid.NewGuid(),
                """{"abc":1}""",
                isInEducationOrganizationHierarchy: true,
                educationOrganizationId: 100,
                parentEducationOrganizationId: null
            );
            _upsertResult = await CreateUpsert().Upsert(upsertRequest, Connection!, Transaction!);
        }

        [Test]
        public void It_should_be_a_successful_insert()
        {
            _upsertResult!.Should().BeOfType<UpsertResult.InsertSuccess>();
        }
    }

    [TestFixture]
    public class Given_An_Upsert_Of_An_EducationOrganization_And_School : EducationOrganizationHierarchyTests
    {
        private UpsertResult? _upsertResult;

        [SetUp]
        public async Task Setup()
        {
            IUpsertRequest edOrgUpsertRequest = CreateUpsertRequest(
                "EducationOrganization",
                Guid.NewGuid(),
                Guid.NewGuid(),
                """{"abc":1}""",
                isInEducationOrganizationHierarchy: true,
                educationOrganizationId: 100,
                parentEducationOrganizationId: null
            );
            await CreateUpsert().Upsert(edOrgUpsertRequest, Connection!, Transaction!);

            IUpsertRequest schoolUpsertRequest = CreateUpsertRequest(
                "School",
                Guid.NewGuid(),
                Guid.NewGuid(),
                """{"abc":1}""",
                isInEducationOrganizationHierarchy: true,
                educationOrganizationId: 1000,
                parentEducationOrganizationId: 100
            );
            _upsertResult = await CreateUpsert().Upsert(schoolUpsertRequest, Connection!, Transaction!);
            await Transaction!.CommitAsync();
        }

        [Test]
        public void It_should_be_a_successful_insert()
        {
            _upsertResult!.Should().BeOfType<UpsertResult.InsertSuccess>();
        }
    }
}
