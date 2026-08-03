// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using Microsoft.Data.SqlClient;
using Npgsql;

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
/// Each contract below is an assumption that phase would otherwise be making silently: the precedence
/// rule it resolves synonym families by, the complete membership of every family it collapses, the
/// keywords it must not reject, and the shape of the canonical string it emits.
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
    /// The user-ID family, which the phase resolves for Username and TargetKey AND collapses in the
    /// string it hands the tool. Same last-occurrence rule, across all three spellings: leaving a losing
    /// alias in that string let the tool reparse - and therefore connect as - a different principal than
    /// the one the phase reported.
    /// </summary>
    [TestCase("UID=first;User ID=middle;UID=effective_last", "effective_last")]
    [TestCase("User ID=ignored_first;UID=effective_last", "effective_last")]
    [TestCase("User ID=ignored_first;User=effective_last", "effective_last")]
    [TestCase("User=ignored_first;UID=effective_last", "effective_last")]
    [TestCase("UID=ignored_first;User=effective_last", "effective_last")]
    [TestCase("User Id=only", "only")]
    public void User_synonym_family_resolves_to_its_last_occurrence(
        string connectionString,
        string expectedUserId
    )
    {
        new SqlConnectionStringBuilder(connectionString).UserID.Should().Be(expectedUserId);
    }

    /// <summary>
    /// Complete membership of the user-ID family the phase collapses. 'User' is the member the phase
    /// originally omitted, which both mis-resolved the principal and left the family uncollapsed.
    /// </summary>
    [TestCase("User ID", "value")]
    [TestCase("User Id", "value")]
    [TestCase("UID", "value")]
    [TestCase("User", "value")]
    public void User_family_membership_is_exactly_the_keywords_the_phase_collapses(
        string keyword,
        string value
    )
    {
        new SqlConnectionStringBuilder($"{keyword}={value}").UserID.Should().Be(value);
    }

    /// <summary>
    /// Closed-world half of the user-family contract: keywords that merely look like members are not
    /// keywords at all to this provider, so the phase's alias list is not silently short.
    /// </summary>
    [TestCase("Username")]
    [TestCase("User Name")]
    public void Keywords_outside_the_user_family_are_not_recognized_at_all(string keyword)
    {
        Assert.Throws<ArgumentException>(() => _ = new SqlConnectionStringBuilder($"{keyword}=value"));
    }

    /// <summary>
    /// Closed-world half of the membership contract: a keyword the phase does NOT treat as a family
    /// member must not feed that family's property. Without this, the lists above could be incomplete
    /// and still pass.
    /// </summary>
    [Test]
    public void Unrelated_keywords_do_not_participate_in_any_collapsed_family()
    {
        var builder = new SqlConnectionStringBuilder(
            "Application Name=app;Workstation ID=ws;Failover Partner=partner.example.com"
        );

        builder.DataSource.Should().BeEmpty();
        builder.InitialCatalog.Should().BeEmpty();
        builder.UserID.Should().BeEmpty();
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
    /// The PostgreSQL side of the same phase: which port spellings Npgsql resolves to the SAME TCP port.
    /// The provisioning phase decides whether a target is the local Compose database by comparing its
    /// port against the configured one, so any spelling this provider accepts and normalizes must compare
    /// equal there too - otherwise the target is classified external while Npgsql still connects to the
    /// local server, which is exactly how a padded or signed spelling escaped the separate-topology
    /// guard. Pinned here because the phase's own suite cannot rely on a built Npgsql being present.
    /// </summary>
    [TestCase("5544", 5544)]
    [TestCase("05544", 5544)]
    [TestCase("+5544", 5544)]
    [TestCase("+05544", 5544)]
    [TestCase(" 5544 ", 5544)]
    public void Npgsql_resolves_padded_and_signed_port_spellings_to_the_same_port(
        string portValue,
        int expectedPort
    )
    {
        new NpgsqlConnectionStringBuilder($"Host=localhost;Port={portValue}").Port.Should().Be(expectedPort);
    }

    /// <summary>
    /// The other half: spellings Npgsql refuses outright. The phase never needs to call any of these
    /// equivalent to a port, and skipping them costs nothing because no connection could be made with
    /// them either.
    /// </summary>
    [TestCase("-5544")]
    [TestCase("0")]
    [TestCase("5544.0")]
    [TestCase("1_5544")]
    [TestCase("2147483648")]
    public void Npgsql_rejects_values_that_are_not_ports(string portValue)
    {
        Assert.Throws<ArgumentException>(() =>
            _ = new NpgsqlConnectionStringBuilder($"Host=localhost;Port={portValue}")
        );
    }

    /// <summary>
    /// Measured boundary worth recording rather than assuming: Npgsql does NOT enforce the upper TCP
    /// bound, so it will hold a Port above 65535. The phase's own 1-65535 check is therefore a sanity
    /// bound and not a locality decision - such a value can never equal the configured local port, which
    /// is always a real one, so bounding it cannot classify a genuinely local target as external.
    /// </summary>
    [TestCase("65535", 65535)]
    [TestCase("65536", 65536)]
    public void Npgsql_does_not_enforce_the_upper_tcp_port_bound(string portValue, int expectedPort)
    {
        new NpgsqlConnectionStringBuilder($"Host=localhost;Port={portValue}").Port.Should().Be(expectedPort);
    }

    /// <summary>
    /// What the phase's canonical output must round-trip through: exactly one member of each of the
    /// THREE collapsed families plus every unrelated option, reparsed by this provider to the same
    /// values the phase validated.
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
