// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Text.Json;
using EdFi.DataManagementService.Tests.E2E.Authorization;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace EdFi.DataManagementService.Tests.Unit.Authorization;

[TestFixture]
public class Given_Authorization_Data_Provider_When_Requesting_Client_Credentials_Without_An_Explicit_Claim_Set
{
    private string _defaultClaimSetName = null!;

    [SetUp]
    public void Setup()
    {
        var createClientCredentialsMethod =
            typeof(AuthorizationDataProvider).GetMethod(
                nameof(AuthorizationDataProvider.CreateClientCredentials)
            ) ?? throw new InvalidOperationException("CreateClientCredentials method was not found.");

        _defaultClaimSetName = (string)(
            createClientCredentialsMethod
                .GetParameters()
                .Single(parameter => parameter.Name == "claimSetName")
                .DefaultValue
            ?? throw new InvalidOperationException("claimSetName default value was not found.")
        );
    }

    [Test]
    public void It_defaults_to_the_sis_vendor_claim_set()
    {
        _defaultClaimSetName.Should().Be(AuthorizationClaimSetNames.SisVendor);
    }
}

[TestFixture]
public class Given_Authorization_Data_Provider_When_Building_An_Application_Request
{
    private JsonDocument _requestDocument = null!;
    private string _claimSetName = null!;

    [SetUp]
    public void Setup()
    {
        _claimSetName = "CustomClaimSet";

        string requestJson = AuthorizationDataProvider.CreateApplicationRequestJson(
            vendorId: 1,
            claimSetName: _claimSetName,
            educationOrganizationIds: [255901],
            dataStoreId: 2
        );

        _requestDocument = JsonDocument.Parse(requestJson);
    }

    [TearDown]
    public void TearDown()
    {
        _requestDocument.Dispose();
    }

    [Test]
    public void It_preserves_the_explicit_claim_set_name()
    {
        _requestDocument.RootElement.GetProperty("claimSetName").GetString().Should().Be(_claimSetName);
    }
}

[TestFixture]
public class Given_Authorization_Data_Provider_When_Selecting_A_Derivative_Arrangement
{
    [Test]
    public void It_attaches_nothing_for_an_untagged_scenario()
    {
        AuthorizationDataProvider
            .ArrangementFromTags(["e2e-ci-shard-1"])
            .Should()
            .Be(DataStoreDerivativeArrangement.None);
    }

    [Test]
    public void It_selects_the_routing_arrangement_from_the_routing_tag()
    {
        AuthorizationDataProvider
            .ArrangementFromTags(["e2e-ci-shard-1", AuthorizationDataProvider.RoutingTag])
            .Should()
            .Be(DataStoreDerivativeArrangement.Routing);
    }

    [Test]
    public void It_selects_the_unreachable_snapshot_arrangement_from_its_own_tag()
    {
        AuthorizationDataProvider
            .ArrangementFromTags([AuthorizationDataProvider.UnreachableSnapshotTag])
            .Should()
            .Be(DataStoreDerivativeArrangement.UnreachableSnapshot);
    }

    [Test]
    public void It_prefers_the_unreachable_snapshot_when_a_scenario_carries_both_tags()
    {
        // The routing tag is a feature-level tag, so a scenario asking for the unreachable snapshot
        // inside such a feature carries both. The more specific arrangement has to win, otherwise the
        // scenario would silently be arranged with a reachable snapshot.
        AuthorizationDataProvider
            .ArrangementFromTags([
                AuthorizationDataProvider.RoutingTag,
                AuthorizationDataProvider.UnreachableSnapshotTag,
            ])
            .Should()
            .Be(DataStoreDerivativeArrangement.UnreachableSnapshot);
    }
}

[TestFixture]
public class Given_Authorization_Data_Provider_When_Naming_An_Absent_Snapshot_Database
{
    [Test]
    public void It_renames_only_the_catalog_of_a_postgresql_connection_string()
    {
        string absent = AuthorizationDataProvider.AbsentDatabaseConnectionString(
            "host=dms-postgresql;port=5432;username=postgres;password=abcdefgh1!;database=edfi_datamanagementservice_e2e_snapshot;"
        );

        // Read back through the provider's own parser: the derived value has to remain a connection
        // string PostgreSQL accepts, not merely a well-formed key/value list.
        NpgsqlConnectionStringBuilder parsed = new(absent);

        parsed.Database.Should().Be("edfi_datamanagementservice_e2e_snapshot_absent");
        parsed.Host.Should().Be("dms-postgresql");
        parsed.Port.Should().Be(5432);
        parsed.Username.Should().Be("postgres");
        parsed.Password.Should().Be("abcdefgh1!");
    }

    [Test]
    public void It_renames_only_the_catalog_of_a_sql_server_connection_string()
    {
        string absent = AuthorizationDataProvider.AbsentDatabaseConnectionString(
            "Server=dms-mssql,1433;Database=edfi_datamanagementservice_e2e_snapshot;User Id=sa;Password=abcdefgh1!;TrustServerCertificate=true;"
        );

        SqlConnectionStringBuilder parsed = new(absent);

        parsed.InitialCatalog.Should().Be("edfi_datamanagementservice_e2e_snapshot_absent");
        parsed.DataSource.Should().Be("dms-mssql,1433");
        parsed.UserID.Should().Be("sa");
        parsed.Password.Should().Be("abcdefgh1!");
        parsed.TrustServerCertificate.Should().BeTrue();
    }

    [Test]
    public void It_preserves_a_password_carrying_connection_string_metacharacters()
    {
        // The orchestration serializes credentials through DbConnectionStringBuilder, so a password
        // containing ';' or '"' arrives already quoted. Rewriting the catalog has to leave that
        // quoting intact rather than truncating the string at the password.
        DbConnectionStringBuilder configured = new();
        configured["host"] = "dms-postgresql";
        configured["username"] = "postgres";
        configured["password"] = "pa;ss\"word";
        configured["database"] = "snapshot_db";

        string absent = AuthorizationDataProvider.AbsentDatabaseConnectionString(configured.ConnectionString);

        NpgsqlConnectionStringBuilder parsed = new(absent);

        parsed.Database.Should().Be("snapshot_db_absent");
        parsed.Password.Should().Be("pa;ss\"word");
    }

    [Test]
    public void It_fails_when_no_snapshot_connection_string_is_configured()
    {
        Action naming = () => AuthorizationDataProvider.AbsentDatabaseConnectionString("   ");

        naming.Should().Throw<InvalidOperationException>().WithMessage("*DataStoreSnapshotConnectionString*");
    }

    [Test]
    public void It_fails_when_the_snapshot_connection_string_names_no_database()
    {
        Action naming = () =>
            AuthorizationDataProvider.AbsentDatabaseConnectionString(
                "host=dms-postgresql;port=5432;username=postgres;"
            );

        naming.Should().Throw<InvalidOperationException>().WithMessage("*'database'*");
    }

    [Test]
    public void It_never_repeats_the_connection_string_in_a_failure_message()
    {
        // The connection string carries credentials, so a message that echoed it would leak them
        // into the E2E log.
        Action naming = () =>
            AuthorizationDataProvider.AbsentDatabaseConnectionString(
                "host=dms-postgresql;username=postgres;password=abcdefgh1!;"
            );

        naming.Should().Throw<InvalidOperationException>().Which.Message.Should().NotContain("abcdefgh1!");
    }
}
