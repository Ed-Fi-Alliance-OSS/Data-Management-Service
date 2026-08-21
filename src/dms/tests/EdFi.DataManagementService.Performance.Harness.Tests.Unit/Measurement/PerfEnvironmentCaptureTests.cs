// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Measurement;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Measurement;

[TestFixture]
public class Given_Connection_Strings_To_Redact
{
    [Test]
    public void It_redacts_a_password_value()
    {
        string shape = PerfEnvironmentCapture.RedactConnectionString(
            "host=localhost;port=5435;username=postgres;password=s3cr3t;database=perf;pooling=true"
        );
        shape.Should().ContainEquivalentOf("password=REDACTED");
        shape.Should().NotContain("s3cr3t");
        shape.Should().ContainEquivalentOf("pooling=true", "the non-secret shape must survive");
    }

    [Test]
    public void It_redacts_a_pwd_value()
    {
        string shape = PerfEnvironmentCapture.RedactConnectionString(
            "Server=localhost,14333;User Id=sa;Pwd=hunter2;TrustServerCertificate=true"
        );
        shape.Should().ContainEquivalentOf("pwd=REDACTED");
        shape.Should().NotContain("hunter2");
    }

    [Test]
    public void It_leaves_secretless_strings_intact()
    {
        string shape = PerfEnvironmentCapture.RedactConnectionString(
            "Server=localhost;Integrated Security=true"
        );
        shape.Should().ContainEquivalentOf("integrated security=true");
        shape.Should().NotContain("REDACTED");
    }
}

[TestFixture]
public class Given_Driver_Version_Sources
{
    [Test]
    public void It_prefers_the_informational_version_and_strips_build_metadata()
    {
        PerfEnvironmentCapture
            .NormalizePackageVersion("6.1.4+9d3ab5cf6c", new Version(6, 0, 0, 0))
            .Should()
            .Be("6.1.4");
    }

    [Test]
    public void It_falls_back_to_the_assembly_version()
    {
        PerfEnvironmentCapture.NormalizePackageVersion(null, new Version(8, 0, 4, 0)).Should().Be("8.0.4.0");
    }

    [Test]
    public void It_reports_unknown_when_no_source_exists()
    {
        PerfEnvironmentCapture.NormalizePackageVersion(" ", null).Should().Be("unknown");
    }

    [Test]
    public void It_reads_the_real_sqlclient_package_version()
    {
        string packageVersion = PerfEnvironmentCapture.DriverPackageVersionOf(
            typeof(Microsoft.Data.SqlClient.SqlConnection).Assembly
        );
        packageVersion.Should().MatchRegex(@"^\d+\.\d+\.\d+");
        packageVersion
            .Should()
            .NotBe(
                typeof(Microsoft.Data.SqlClient.SqlConnection).Assembly.GetName().Version!.ToString(),
                "the package version must be finer-grained than the assembly version"
            );
    }
}
