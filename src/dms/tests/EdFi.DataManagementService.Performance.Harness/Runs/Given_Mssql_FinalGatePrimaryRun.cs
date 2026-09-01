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
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Runs;

/// <summary>
/// The SQL Server primary final-gate evidence run, configured entirely through PERF_*
/// environment variables and normally invoked by eng/performance/invoke-final-gate.ps1.
/// The three ordered tests are the three phases over one shared leased database, each
/// booting its own host so the measuring principal is boot-time real: the pristine and
/// filtered phases run bypassed exactly like the DMS-1391 baseline capture, and the
/// authorized phase runs the relationship claim with the seed school's education
/// organization id.
/// </summary>
[TestFixture]
[Explicit("Evidence run: loads the configured fixture and writes final-gate artifacts")]
[Category("Performance")]
public class Given_Mssql_FinalGatePrimaryRun : MssqlApiIntegrationTestBase
{
    private static string? _sharedConnectionString;
    private static PerfFinalGateRunAccumulator? _accumulator;

    protected override FixtureKey Fixture => FixtureKey.AuthoritativeDs52;

    protected override bool CaptureQueryPlans => true;

    protected override bool BypassAuthorization => !IsAuthorizedPhaseTest;

    protected override IReadOnlyList<long> ClientEducationOrganizationIds =>
        IsAuthorizedPhaseTest ? [PerfAuthorizationSeedDefinition.SchoolId] : [];

    protected override IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        IsAuthorizedPhaseTest
            ? PeopleRelationshipGetManyScenarioHelpers.CreateClaimSetProvider(
                fixture,
                [
                    new RelationshipReadResource(
                        PerfFixtureDefinition.ProjectName,
                        PerfFixtureDefinition.ResourceName
                    ),
                ],
                AuthorizationStrategyNameConstants.RelationshipsWithStudentsOnly
            )
            : base.CreateClaimSetProvider(fixture);

    /// <summary>
    /// The phase host is chosen while the test's host boots, so the phase is read from the
    /// test method about to run.
    /// </summary>
    private static bool IsAuthorizedPhaseTest =>
        TestContext.CurrentContext.Test.MethodName?.Contains("authorized", StringComparison.OrdinalIgnoreCase)
        ?? false;

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture)
    {
        _sharedConnectionString ??= await base.LeaseDatabaseAsync(fixture);
        return _sharedConnectionString;
    }

    protected override Task ReleaseDatabaseAsync(string leasedConnectionString) =>
        // The shared database lives for the whole fixture; the one-time teardown releases it.
        Task.CompletedTask;

    [OneTimeTearDown]
    public async Task ReleaseSharedDatabaseAsync()
    {
        _accumulator = null;
        if (_sharedConnectionString is not null)
        {
            string leased = _sharedConnectionString;
            _sharedConnectionString = null;
            await base.ReleaseDatabaseAsync(leased);
        }
    }

    [Test]
    [Order(1)]
    public async Task It_runs_the_pristine_phase()
    {
        PerfRunConfiguration configuration = PerfRunConfigurationLoader.FromEnvironment();

        _accumulator = await PerfFinalGateRunPipeline.RunPristinePhaseAsync(
            Harness,
            PerfProvider.Mssql,
            () => OpenAssertionConnectionAsync(_sharedConnectionString!),
            new PerfFixtureDefinition(configuration.Fixture),
            configuration.DeepOffset,
            configuration.WarmupIterations,
            configuration.MeasuredIterations,
            configuration.RunnerCommit,
            PerfEvidenceRunSettings.FromEnvironment()
        );
    }

    [Test]
    [Order(2)]
    public async Task It_runs_the_second_principal_authorized_phase()
    {
        _accumulator.Should().NotBeNull("the pristine phase must have completed first");

        await PerfFinalGateRunPipeline.RunAuthorizedPhaseAsync(
            _accumulator!,
            Harness,
            () => OpenAssertionConnectionAsync(_sharedConnectionString!)
        );
    }

    [Test]
    [Order(3)]
    public async Task It_runs_the_filtered_phase_and_writes_artifacts()
    {
        _accumulator.Should().NotBeNull("the earlier phases must have completed first");
        PerfRunConfiguration configuration = PerfRunConfigurationLoader.FromEnvironment();

        string runDirectory = await PerfFinalGateRunPipeline.RunFilteredPhaseAndWriteAsync(
            _accumulator!,
            Harness,
            () => OpenAssertionConnectionAsync(_sharedConnectionString!),
            _sharedConnectionString!,
            configuration.ResultsDirectory,
            PerfEvidenceRunSettings.FromEnvironment()
        );

        await TestContext.Out.WriteLineAsync($"Final-gate primary artifacts: {runDirectory}");
    }
}
