// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Models.ClaimsHierarchy;
using EdFi.DmsConfigurationService.Backend.Postgresql.Repositories;
using EdFi.DmsConfigurationService.Backend.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Test.Integration;

public class ClaimsHierarchyTests : DatabaseTest
{
    private readonly IClaimsHierarchyRepository _repository = new ClaimsHierarchyRepository(
        Configuration.DatabaseOptions,
        NullLogger<ClaimsHierarchyRepository>.Instance
    );

    [SetUp]
    public async Task Setup()
    {
        await ClaimsHierarchyTestHelper.ReinitializeClaimsHierarchy(clearOnly: true);
    }

    [Test]
    public async Task Should_return_failure_when_no_hierarchy_exists()
    {
        var initialClaimsHierarchyResult = await _repository.GetClaimsHierarchy();
        initialClaimsHierarchyResult.Should().BeOfType<ClaimsHierarchyGetResult.FailureHierarchyNotFound>();
    }

    [Test]
    public async Task Should_return_failure_when_multiple_hierarchies_exist()
    {
        // Arrange
        string connectionString = Configuration.DatabaseOptions.Value.DatabaseConnection;

        await using var conn = new NpgsqlConnection(connectionString);

        // Insert 2 hierarchies
        int recordsAffected = await conn.ExecuteAsync(
            "INSERT INTO dmscs.claimshierarchy (hierarchy, lastmodifieddate) VALUES ('{}'::jsonb, now())"
        );
        recordsAffected.Should().Be(1);

        recordsAffected = await conn.ExecuteAsync(
            "INSERT INTO dmscs.claimshierarchy (hierarchy, lastmodifieddate) VALUES ('{}'::jsonb, now())"
        );
        recordsAffected.Should().Be(1);

        // Act
        var initialClaimsHierarchyResult = await _repository.GetClaimsHierarchy();

        // Assert
        initialClaimsHierarchyResult
            .Should()
            .BeOfType<ClaimsHierarchyGetResult.FailureMultipleHierarchiesFound>();
    }

    [Test]
    public async Task Should_return_success_when_single_hierarchy_exists()
    {
        // Arrange
        string connectionString = Configuration.DatabaseOptions.Value.DatabaseConnection;

        await using var conn = new NpgsqlConnection(connectionString);

        // Create a single hierarchy
        var claimsHierarchy = new List<Claim>
        {
            new Claim
            {
                Name = "RootClaim",
                ClaimSets = new List<ClaimSet> { new ClaimSet { Name = "Test-Insert-ClaimSet" } },
                Claims = new List<Claim>
                {
                    new Claim
                    {
                        Name = "ChildClaim",
                        ClaimSets = new List<ClaimSet> { new ClaimSet { Name = "Test-Insert-ClaimSet" } },
                    },
                },
            },
        };

        string hierarchyJson = JsonSerializer.Serialize(claimsHierarchy);

        // Insert the single hierarchy
        int recordsAffected = await conn.ExecuteAsync(
            "INSERT INTO dmscs.claimshierarchy (hierarchy, lastmodifieddate) VALUES (@HierarchyJson::jsonb, now())",
            new { HierarchyJson = hierarchyJson }
        );
        recordsAffected.Should().Be(1);

        // Act
        var claimsHierarchyResult = await _repository.GetClaimsHierarchy();

        // Assert
        claimsHierarchyResult.Should().BeOfType<ClaimsHierarchyGetResult.Success>();
        (claimsHierarchyResult as ClaimsHierarchyGetResult.Success)!
            .Claims.Single()
            .Name.Should()
            .Be("RootClaim");
    }
}
