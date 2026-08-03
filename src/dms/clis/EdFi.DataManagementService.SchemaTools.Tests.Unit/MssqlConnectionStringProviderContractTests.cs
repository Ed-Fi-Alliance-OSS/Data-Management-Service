// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Data.SqlClient;

namespace EdFi.DataManagementService.SchemaTools.Tests.Unit;

/// <summary>
/// Pins the Microsoft.Data.SqlClient connection-string behaviour that the bootstrap provisioning
/// phase (eng/docker-compose/provision-dms-schema.ps1) depends on when it decides which server and
/// database SchemaTools will actually deploy into.
///
/// This lives here, in a project that always builds against the very provider MssqlDatabaseProvisioner
/// links, because the PowerShell side cannot guarantee it: the bootstrap Pester CI job installs Pester
/// and the dotnet SDK but never builds the solution, so no SchemaTools output - and therefore no
/// Microsoft.Data.SqlClient assembly - exists in that job. A provider assertion written there could
/// only skip. These assertions run on every pull request through build-dms.ps1 UnitTest.
///
/// Three contracts are pinned, each one an assumption that phase would otherwise be making silently.
/// </summary>
public class MssqlConnectionStringProviderContractTests
{
    /// <summary>
    /// The precedence rule the phase resolves synonym families by: within a family, the LAST
    /// occurrence in the connection-string text wins, regardless of which spelling it used. A
    /// "first listed synonym" reading of the same text selects a different database, which is what
    /// let a connection string validate one name and be deployed into another.
    /// </summary>
    [TestCase("Database=ignored_first;Initial Catalog=effective_last", "effective_last")]
    [TestCase("Initial Catalog=ignored_first;Database=effective_last", "effective_last")]
    [TestCase("Database=first;Initial Catalog=second;Database=effective_last", "effective_last")]
    [TestCase("Initial Catalog=first;Database=second;Initial Catalog=effective_last", "effective_last")]
    [TestCase("Database=only;Database=effective_last", "effective_last")]
    [TestCase("Database=only_database", "only_database")]
    [TestCase("Initial Catalog=only_initial_catalog", "only_initial_catalog")]
    public void Database_synonym_family_resolves_to_its_last_occurrence(
        string connectionString,
        string expectedInitialCatalog
    )
    {
        new SqlConnectionStringBuilder(connectionString).InitialCatalog.Should().Be(expectedInitialCatalog);
    }

    /// <summary>
    /// Same rule for the server family, and across spellings: the phase classifies the endpoint from
    /// the winner of this family and encodes that same winner in the string it hands the tool.
    /// </summary>
    [TestCase("Server=tcp:localhost,1435;Data Source=effective.example.com", "effective.example.com")]
    [TestCase("Data Source=ignored.example.com;Server=tcp:localhost,1435", "tcp:localhost,1435")]
    [TestCase("Server=first.example.com;Addr=effective.example.com", "effective.example.com")]
    [TestCase("Addr=first.example.com;Network Address=effective.example.com", "effective.example.com")]
    [TestCase("Address=first.example.com;Server=effective.example.com", "effective.example.com")]
    [TestCase("Server=only.example.com", "only.example.com")]
    public void Server_synonym_family_resolves_to_its_last_occurrence(
        string connectionString,
        string expectedDataSource
    )
    {
        new SqlConnectionStringBuilder(connectionString).DataSource.Should().Be(expectedDataSource);
    }

    /// <summary>
    /// The COMPLETE membership of each family the phase collapses. The phase removes every other
    /// member of a family from the string it emits so no survivor can override the validated value on
    /// reparse; if this provider grew a synonym the phase does not list, that survivor would silently
    /// redirect the deployment. Asserted by construction - each keyword is set alone and must land on
    /// the family's property - plus a closed-world check that a non-member does not.
    /// </summary>
    [TestCase("Database", "value")]
    [TestCase("Initial Catalog", "value")]
    public void Database_family_membership_is_exactly_the_keywords_the_phase_collapses(
        string keyword,
        string value
    )
    {
        new SqlConnectionStringBuilder($"{keyword}={value}").InitialCatalog.Should().Be(value);
    }

    [TestCase("Server", "value")]
    [TestCase("Data Source", "value")]
    [TestCase("Addr", "value")]
    [TestCase("Address", "value")]
    [TestCase("Network Address", "value")]
    public void Server_family_membership_is_exactly_the_keywords_the_phase_collapses(
        string keyword,
        string value
    )
    {
        new SqlConnectionStringBuilder($"{keyword}={value}").DataSource.Should().Be(value);
    }

    /// <summary>
    /// Closed-world half of the membership contract: a keyword the phase does NOT treat as a family
    /// member must not feed that family's property. Without this, the lists above could be incomplete
    /// and still pass.
    /// </summary>
    [Test]
    public void Unrelated_keywords_do_not_participate_in_either_collapsed_family()
    {
        var builder = new SqlConnectionStringBuilder(
            "Application Name=app;Workstation ID=ws;Failover Partner=partner.example.com"
        );

        builder.DataSource.Should().BeEmpty();
        builder.InitialCatalog.Should().BeEmpty();
    }

    /// <summary>
    /// Keywords this provider accepts that the in-box legacy System.Data.SqlClient rejects outright
    /// with "Keyword not supported" (measured against that provider as shipped inside PowerShell 7).
    /// They are why the phase must parse with the provider the resolved tool ships rather than the one
    /// that happens to be in-box: parsing a valid external SQL Server datastore's connection string
    /// with the legacy builder refuses a target SchemaTools would have accepted.
    ///
    /// Only this side of the divergence is asserted here - the legacy provider is not referenced by
    /// this project, and adding a package dependency purely to observe it is not worth it. The
    /// regression guard for the other side is where the risk actually lives: the bootstrap Pester
    /// suite drives an unambiguous connection string carrying these keywords end to end, so a return
    /// to full-string legacy parsing fails there.
    /// </summary>
    [TestCase("Host Name In Certificate")]
    [TestCase("Server Certificate")]
    [TestCase("Enclave Attestation Url")]
    public void Provider_accepts_keywords_the_legacy_in_box_provider_rejects(string keyword)
    {
        var connectionString =
            $"Server=sql.example.com;Database=edfi_datastore;User Id=sa;Password=p;{keyword}=value";

        var builder = new SqlConnectionStringBuilder(connectionString);

        builder.InitialCatalog.Should().Be("edfi_datastore");
        builder.DataSource.Should().Be("sql.example.com");
    }

    /// <summary>
    /// What the phase's canonical output must round-trip through: exactly one member of each family
    /// plus every unrelated option, reparsed by this provider to the same values the phase validated.
    /// </summary>
    [Test]
    public void Canonical_single_member_string_round_trips_with_unrelated_options_preserved()
    {
        const string Canonical =
            "server=127.0.0.1,15433;database=edfi_datastore;user id=sa;password=abcdefgh1!;"
            + "trustservercertificate=true;host name in certificate=cert.example.com";

        var builder = new SqlConnectionStringBuilder(Canonical);

        builder.DataSource.Should().Be("127.0.0.1,15433");
        builder.InitialCatalog.Should().Be("edfi_datastore");
        builder.UserID.Should().Be("sa");
        builder.TrustServerCertificate.Should().BeTrue();
        builder.HostNameInCertificate.Should().Be("cert.example.com");
    }
}
