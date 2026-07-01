// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

public class DeployTests : DatabaseTestBase
{
    [Test]
    public async Task It_creates_all_dmscs_tables()
    {
        await using var connection = await OpenConnectionAsync();
        var tables = (
            await connection.QueryAsync<string>(
                "SELECT LOWER(t.name) FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = 'dmscs'"
            )
        ).ToList();

        tables
            .Should()
            .Contain([
                "vendor",
                "vendornamespaceprefix",
                "application",
                "applicationeducationorganization",
                "apiclient",
                "claimset",
                "authorizationstrategy",
                "resourceclaim",
                "claimshierarchy",
                "openiddictapplication",
                "openiddictauthorization",
                "openiddictscope",
                "openiddictapplicationscope",
                "openiddicttoken",
                "openiddictrole",
                "openiddictclientrole",
                "openiddictkey",
                "datastore",
                "apiclientdatastore",
                "datastorecontext",
                "datastorederivative",
                "tenant",
                "profile",
                "applicationprofile",
            ]);
    }

    [Test]
    public async Task It_seeds_authorization_strategies_and_resource_claims()
    {
        await using var connection = await OpenConnectionAsync();
        var strategyCount = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dmscs.AuthorizationStrategy"
        );
        var resourceClaimCount = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dmscs.ResourceClaim"
        );

        strategyCount.Should().Be(13);
        // The seed script's VALUES list has 429 rows (identifiers are sparse, max Id 437),
        // matching the PostgreSQL seed script row-for-row.
        resourceClaimCount.Should().Be(429);
    }

    [Test]
    public void It_is_idempotent_on_redeploy()
    {
        var result = new Deploy.DatabaseDeploy().DeployDatabase(ConnectionString);
        result.Should().BeOfType<Backend.Deploy.DatabaseDeployResult.DatabaseDeploySuccess>();
    }
}
