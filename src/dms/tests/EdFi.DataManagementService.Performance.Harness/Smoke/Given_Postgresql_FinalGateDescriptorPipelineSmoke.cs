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
/// The descriptor final-gate pipeline end to end at smoke scale, under the real namespace
/// principal: writes and reload-validates the complete artifact directory for the separate
/// descriptor fixture.
/// </summary>
[TestFixture]
[Explicit("Full final-gate pipeline against a live database at smoke scale; run manually")]
[Category("Performance")]
public class Given_Postgresql_FinalGateDescriptorPipelineSmoke : PostgresqlApiIntegrationTestBase
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
    public async Task It_writes_validated_descriptor_artifacts_end_to_end()
    {
        string resultsDirectoryBase = Path.Combine(
            Path.GetTempPath(),
            "dms-perf-final-gate-smoke",
            Guid.NewGuid().ToString("N")
        );
        string runDirectory = await PerfFinalGateRunPipeline.RunDescriptorFixtureAsync(
            Harness,
            PerfProvider.Postgresql,
            () => OpenAssertionConnectionAsync(_leasedConnectionString),
            _leasedConnectionString,
            new PerfDescriptorFixtureDefinition(PerfDescriptorFixtureKind.DescriptorsSmoke2k),
            PerfRunConfigurationLoader.MinimumWarmupIterations,
            PerfRunConfigurationLoader.MinimumMeasuredIterations,
            resultsDirectoryBase,
            GitIdentity.HeadCommit(AppContext.BaseDirectory),
            new PerfEvidenceRunSettings(
                ImageTag: "postgres:16.8-alpine",
                ImageDigest: "sha256:951d0626662c85a25e1ba0a89e64f314a2b99abced2c85b4423506249c2d82b0",
                StorageNote: "local docker volume, not tmpfs",
                AllowCi: true,
                AllowedDirtyPrefixes: [PerfEvidenceRunSettings.DefaultAllowedDirtyPrefix],
                AllowAnyDirtyPath: true
            )
        );

        (await File.ReadAllLinesAsync(Path.Combine(runDirectory, "results.csv")))
            .Should()
            .HaveCount(PerfFinalGateScenarios.DescriptorCellsInExecutionOrder.Count + 1);
        File.Exists(Path.Combine(runDirectory, "fixture-manifest.json")).Should().BeTrue();
        Directory.GetFiles(Path.Combine(runDirectory, "plans")).Should().NotBeEmpty();
        Directory.GetFiles(Path.Combine(runDirectory, "sql")).Should().NotBeEmpty();

        await TestContext.Out.WriteLineAsync($"Validated final-gate artifacts written to {runDirectory}");
    }
}
