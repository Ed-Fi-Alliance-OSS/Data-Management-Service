// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Performance.Harness.Configuration;
using EdFi.DataManagementService.Performance.Harness.Fixtures;
using EdFi.DataManagementService.Performance.Harness.Measurement;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Mssql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;

namespace EdFi.DataManagementService.Performance.Harness.Runs;

/// <summary>
/// The SQL Server descriptor final-gate evidence run, configured entirely through PERF_*
/// environment variables and normally invoked by eng/performance/invoke-final-gate.ps1.
/// Runs under the real namespace principal: the caller holds only the accessible prefix,
/// so descriptor page membership is decided by the production namespace predicate.
/// </summary>
[TestFixture]
[Explicit("Evidence run: loads the configured fixture and writes final-gate artifacts")]
[Category("Performance")]
public class Given_Mssql_FinalGateDescriptorRun : MssqlApiIntegrationTestBase
{
    private string _leasedConnectionString = null!;

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

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture)
    {
        _leasedConnectionString = await base.LeaseDatabaseAsync(fixture);
        return _leasedConnectionString;
    }

    [Test]
    public async Task It_captures_the_descriptor_final_gate_run()
    {
        PerfDescriptorRunConfiguration configuration = PerfDescriptorRunConfiguration.FromEnvironment();

        string runDirectory = await PerfFinalGateRunPipeline.RunDescriptorFixtureAsync(
            Harness,
            PerfProvider.Mssql,
            () => OpenAssertionConnectionAsync(_leasedConnectionString),
            _leasedConnectionString,
            new PerfDescriptorFixtureDefinition(configuration.Fixture),
            configuration.WarmupIterations,
            configuration.MeasuredIterations,
            configuration.ResultsDirectory,
            configuration.RunnerCommit,
            PerfEvidenceRunSettings.FromEnvironment()
        );

        await TestContext.Out.WriteLineAsync($"Final-gate descriptor artifacts: {runDirectory}");
    }
}
