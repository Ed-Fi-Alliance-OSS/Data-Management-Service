// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Configuration;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Configuration;

[TestFixture]
public class Given_The_Final_Gate_Report_Settings_Loader
{
    private static readonly string _reportDirectory = Path.Combine(Path.GetTempPath(), "perf-report");

    private static Dictionary<string, string?> PostgresqlOnlyVariables() =>
        new()
        {
            [PerfEnvironmentVariables.ReportDirectory] = _reportDirectory,
            [PerfEnvironmentVariables.BaselineDirectoryPostgresql] = @"C:\evidence\pg-baseline",
            [PerfEnvironmentVariables.FinalPrimaryDirectoryPostgresql] = @"C:\evidence\pg-primary",
            [PerfEnvironmentVariables.FinalDescriptorsDirectoryPostgresql] = @"C:\evidence\pg-descriptors",
        };

    private static PerfFinalGateReportSettings Load(Dictionary<string, string?> variables) =>
        PerfFinalGateReportSettings.Load(name => variables.GetValueOrDefault(name));

    [Test]
    public void It_loads_a_single_provider_triplet()
    {
        PerfFinalGateReportSettings settings = Load(PostgresqlOnlyVariables());

        settings.ReportDirectory.Should().Be(_reportDirectory);
        PerfFinalGateReportProviderDirectories provider = settings.Providers.Should().ContainSingle().Subject;
        provider.Provider.Should().Be("postgresql");
        provider.BaselineDirectory.Should().Be(@"C:\evidence\pg-baseline");
        provider.PrimaryDirectory.Should().Be(@"C:\evidence\pg-primary");
        provider.DescriptorsDirectory.Should().Be(@"C:\evidence\pg-descriptors");
    }

    [Test]
    public void It_loads_both_providers_when_both_triplets_are_set()
    {
        Dictionary<string, string?> variables = PostgresqlOnlyVariables();
        variables[PerfEnvironmentVariables.BaselineDirectoryMssql] = @"C:\evidence\ms-baseline";
        variables[PerfEnvironmentVariables.FinalPrimaryDirectoryMssql] = @"C:\evidence\ms-primary";
        variables[PerfEnvironmentVariables.FinalDescriptorsDirectoryMssql] = @"C:\evidence\ms-descriptors";

        PerfFinalGateReportSettings settings = Load(variables);

        settings.Providers.Select(provider => provider.Provider).Should().Equal("postgresql", "mssql");
    }

    [Test]
    public void It_rejects_a_partial_provider_triplet()
    {
        Dictionary<string, string?> variables = PostgresqlOnlyVariables();
        variables.Remove(PerfEnvironmentVariables.FinalDescriptorsDirectoryPostgresql);

        Action act = () => Load(variables);

        act.Should().Throw<PerfConfigurationException>().WithMessage("*all be set or all be absent*");
    }

    [Test]
    public void It_rejects_a_configuration_with_no_providers()
    {
        Action act = () =>
            Load(
                new Dictionary<string, string?>
                {
                    [PerfEnvironmentVariables.ReportDirectory] = _reportDirectory,
                }
            );

        act.Should().Throw<PerfConfigurationException>().WithMessage("*at least one provider*");
    }

    [Test]
    public void It_rejects_a_relative_report_directory()
    {
        Dictionary<string, string?> variables = PostgresqlOnlyVariables();
        variables[PerfEnvironmentVariables.ReportDirectory] = "relative/report";

        Action act = () => Load(variables);

        act.Should().Throw<PerfConfigurationException>().WithMessage("*fully qualified*");
    }
}
