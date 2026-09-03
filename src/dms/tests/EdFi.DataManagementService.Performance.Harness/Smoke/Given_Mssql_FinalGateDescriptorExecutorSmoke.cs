// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

/// <summary>
/// The descriptor-variant principal made real: authorization is not bypassed, the caller
/// holds only the accessible namespace prefix, and descriptor reads authorize through the
/// namespace strategy — so cursor page membership is decided by the production namespace
/// predicate.
/// </summary>
[TestFixture]
[Explicit("Runs live cursor/partition executor cells at smoke scale; run manually against a local database")]
[Category("Performance")]
public class Given_Mssql_FinalGateDescriptorExecutorSmoke : MssqlApiIntegrationTestBase
{
    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    protected override bool BypassAuthorization => false;

    protected override IReadOnlyList<string> ClientNamespacePrefixes =>
        [PerfDescriptorFixtureDefinition.AccessibleNamespacePrefix];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        PeopleRelationshipGetManyScenarioHelpers.CreateClaimSetProvider(
            fixture,
            [
                new RelationshipReadResource(
                    PerfDescriptorFixtureDefinition.ProjectName,
                    PerfDescriptorFixtureDefinition.ResourceName
                ),
            ],
            AuthorizationStrategyNameConstants.NamespaceBased
        );

    [Test]
    public async Task It_measures_descriptor_cursor_and_partition_cells()
    {
        await PerfFinalGateExecutorSmokeScenario.RunDescriptorAsync(Harness, PerfProvider.Mssql);
    }
}
