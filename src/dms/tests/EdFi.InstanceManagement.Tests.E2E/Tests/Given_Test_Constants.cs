// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.InstanceManagement.Tests.E2E.Configuration;
using FluentAssertions;

namespace EdFi.InstanceManagement.Tests.E2E.UnitTests;

[TestFixture]
[Category("InstanceFixtureUnit")]
[NonParallelizable]
public class Given_Test_Constants
{
    private static readonly string[] ManagedVariables =
    [
        "INSTANCE_E2E_DATABASE_1_NAME",
        "INSTANCE_E2E_DATABASE_2_NAME",
        "INSTANCE_E2E_DATABASE_3_NAME",
        "INSTANCE_E2E_DATABASE_1_CONNECTION_STRING",
        "INSTANCE_E2E_DATABASE_2_CONNECTION_STRING",
        "INSTANCE_E2E_DATABASE_3_CONNECTION_STRING",
    ];

    private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);

    [SetUp]
    public void SaveAndClearEnvironment()
    {
        foreach (var name in ManagedVariables)
        {
            _originalValues[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [TearDown]
    public void RestoreEnvironment()
    {
        foreach (var name in ManagedVariables)
        {
            Environment.SetEnvironmentVariable(name, _originalValues[name]);
        }
    }

    [Test]
    public void It_returns_the_database_name_from_the_environment_verbatim()
    {
        Environment.SetEnvironmentVariable("INSTANCE_E2E_DATABASE_2_NAME", "edfi_route_two");

        TestConstants.GetDatabaseName(2).Should().Be("edfi_route_two");
    }

    [Test]
    public void It_returns_the_connection_string_from_the_environment_verbatim()
    {
        const string connectionString =
            "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=db3;";
        Environment.SetEnvironmentVariable("INSTANCE_E2E_DATABASE_3_CONNECTION_STRING", connectionString);

        TestConstants.GetConnectionString(3).Should().Be(connectionString);
    }

    [Test]
    public void It_throws_for_an_index_below_range()
    {
        var act = () => TestConstants.GetDatabaseName(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void It_throws_for_an_index_above_range()
    {
        var act = () => TestConstants.GetConnectionString(5);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void It_throws_a_clear_error_when_the_database_name_is_not_set()
    {
        var act = () => TestConstants.GetDatabaseName(1);

        act.Should().Throw<InvalidOperationException>().WithMessage("*INSTANCE_E2E_DATABASE_1_NAME*");
    }

    [Test]
    public void It_throws_a_clear_error_when_the_connection_string_is_blank()
    {
        Environment.SetEnvironmentVariable("INSTANCE_E2E_DATABASE_1_CONNECTION_STRING", "   ");

        var act = () => TestConstants.GetConnectionString(1);

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*INSTANCE_E2E_DATABASE_1_CONNECTION_STRING*");
    }
}
