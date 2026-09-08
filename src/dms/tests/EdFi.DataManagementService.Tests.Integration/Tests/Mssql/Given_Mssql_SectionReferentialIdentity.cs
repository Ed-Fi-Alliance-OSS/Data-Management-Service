// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

/// <summary>
/// SQL Server API regression: the relational ReferentialIdentity trigger must store the same
/// referential id Core computes when a resource identity contains a key-unified reference path
/// (Section's <c>$.courseOfferingReference.schoolId</c>), so resources referencing the Section
/// resolve without a reference-validation conflict. The hash construction in the trigger SQL is
/// dialect-specific, so this proof runs against each dialect's emitted trigger.
/// </summary>
public sealed class Given_Mssql_SectionReferentialIdentity : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    [Test]
    public Task It_creates_a_section_referencing_resource_without_reference_conflict() =>
        SectionReferentialIdentityScenario.It_creates_a_section_referencing_resource_without_reference_conflict(
            Harness
        );
}
