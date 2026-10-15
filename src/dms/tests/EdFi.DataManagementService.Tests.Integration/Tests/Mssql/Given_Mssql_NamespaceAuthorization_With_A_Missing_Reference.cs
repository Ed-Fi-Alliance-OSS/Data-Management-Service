// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Mssql;

public sealed class Given_Mssql_NamespaceAuthorization_With_A_Missing_Reference : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        NamespaceAuthorizationMissingReferenceScenario.ClientPrefixes;

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        NamespaceAuthorizationMissingReferenceScenario.CreateClaimSetProvider(fixture);

    [Test]
    public Task It_denies_an_existing_assessment_in_a_disallowed_namespace() =>
        NamespaceAuthorizationMissingReferenceScenario.It_denies_an_existing_assessment_in_a_disallowed_namespace(
            Harness
        );

    [Test]
    public Task It_denies_a_missing_assessment_in_a_disallowed_namespace() =>
        NamespaceAuthorizationMissingReferenceScenario.It_denies_a_missing_assessment_in_a_disallowed_namespace(
            Harness
        );

    [Test]
    public Task It_reports_the_unresolved_reference_for_a_missing_assessment_in_an_allowed_namespace() =>
        NamespaceAuthorizationMissingReferenceScenario.It_reports_the_unresolved_reference_for_a_missing_assessment_in_an_allowed_namespace(
            Harness
        );

    [Test]
    public Task It_creates_a_student_assessment_for_an_existing_assessment_in_an_allowed_namespace() =>
        NamespaceAuthorizationMissingReferenceScenario.It_creates_a_student_assessment_for_an_existing_assessment_in_an_allowed_namespace(
            Harness
        );

    [Test]
    public Task It_denies_key_unified_missing_references_in_a_disallowed_namespace() =>
        NamespaceAuthorizationMissingReferenceScenario.It_denies_key_unified_missing_references_in_a_disallowed_namespace(
            Harness
        );

    [Test]
    public Task It_reports_both_unresolved_key_unified_references_in_an_allowed_namespace() =>
        NamespaceAuthorizationMissingReferenceScenario.It_reports_both_unresolved_key_unified_references_in_an_allowed_namespace(
            Harness
        );
}
