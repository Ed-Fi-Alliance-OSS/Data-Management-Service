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
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using EdFi.DataManagementService.Tests.Integration.Scenarios;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Smoke;

/// <summary>
/// The primary final-gate pipeline end to end at smoke scale. The three ordered tests are the
/// three phases over one shared leased database, each booting its own host so the measuring
/// principal is boot-time real: the pristine and filtered phases run bypassed exactly like
/// the baseline capture, and the authorized phase runs the relationship claim. The filtered
/// phase writes and reload-validates the complete artifact directory.
/// </summary>
[TestFixture]
[Explicit("Full final-gate pipeline against a live database at smoke scale; run manually")]
[Category("Performance")]
public class Given_Postgresql_FinalGatePrimaryPipelineSmoke : PostgresqlApiIntegrationTestBase
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
        _accumulator = await PerfFinalGateRunPipeline.RunPristinePhaseAsync(
            Harness,
            PerfProvider.Postgresql,
            () => OpenAssertionConnectionAsync(_sharedConnectionString!),
            new PerfFixtureDefinition(PerfFixtureKind.Smoke10k),
            deepOffset: 9_000,
            PerfRunConfigurationLoader.MinimumWarmupIterations,
            PerfRunConfigurationLoader.MinimumMeasuredIterations,
            GitIdentity.HeadCommit(AppContext.BaseDirectory),
            SmokeSettings()
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
    public async Task It_runs_the_filtered_phase_and_writes_validated_artifacts()
    {
        _accumulator.Should().NotBeNull("the earlier phases must have completed first");

        string resultsDirectoryBase = Path.Combine(
            Path.GetTempPath(),
            "dms-perf-final-gate-smoke",
            Guid.NewGuid().ToString("N")
        );
        string runDirectory = await PerfFinalGateRunPipeline.RunFilteredPhaseAndWriteAsync(
            _accumulator!,
            Harness,
            () => OpenAssertionConnectionAsync(_sharedConnectionString!),
            _sharedConnectionString!,
            resultsDirectoryBase,
            SmokeSettings()
        );

        (await File.ReadAllLinesAsync(Path.Combine(runDirectory, "results.csv")))
            .Should()
            .HaveCount(PerfFinalGateScenarios.PrimaryCellsInExecutionOrder.Count + 1);
        File.Exists(Path.Combine(runDirectory, "fixture-manifest.json")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(runDirectory, "plans")).Should().NotBeEmpty();
        Directory.GetFiles(Path.Combine(runDirectory, "sql")).Should().NotBeEmpty();

        await TestContext.Out.WriteLineAsync($"Validated final-gate artifacts written to {runDirectory}");
    }

    private static PerfEvidenceRunSettings SmokeSettings() =>
        // The compose-pinned image identity; the capture wrapper resolves and validates these
        // dynamically for evidence runs.
        new(
            ImageTag: "postgres:16.8-alpine",
            ImageDigest: "sha256:951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0",
            StorageNote: "local docker volume, not tmpfs",
            AllowCi: true,
            AllowedDirtyPrefixes: [PerfEvidenceRunSettings.DefaultAllowedDirtyPrefix],
            AllowAnyDirtyPath: true
        );
}
