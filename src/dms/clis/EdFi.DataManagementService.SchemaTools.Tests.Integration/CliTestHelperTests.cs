// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;

namespace EdFi.DataManagementService.SchemaTools.Tests.Integration;

[TestFixture("--connection-string")]
[TestFixture("-c")]
[TestFixture("--connection-string=")]
[TestFixture("-c=")]
public sealed class Given_Cli_Timeout_Diagnostics(string option)
{
    private string _diagnostic = null!;

    [SetUp]
    public void Setup()
    {
        const string connectionString = "Host=localhost;Database=sample;Password=credential-sentinel";
        string[] arguments = option.EndsWith('=')
            ? ["ddl", "provision", "--schema", "schema.json", option + connectionString, "--dialect", "pgsql"]
            : ["ddl", "provision", "--schema", "schema.json", option, connectionString, "--dialect", "pgsql"];
        _diagnostic = CliTestHelper.FormatTimeoutDiagnostic(
            "api-schema-tools",
            arguments,
            connectionString,
            connectionString
        );
    }

    [Test]
    public void It_retains_nonsecret_arguments() => _diagnostic.Should().Contain("--schema schema.json");

    [Test]
    public void It_retains_the_dialect() => _diagnostic.Should().Contain("--dialect pgsql");

    [Test]
    public void It_redacts_connection_values_from_arguments_and_output() =>
        _diagnostic.Should().NotContain("credential-sentinel");
}
