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
