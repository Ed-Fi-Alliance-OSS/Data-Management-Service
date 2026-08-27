// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using EdFi.DataManagementService.Tests.Integration.Postgresql;
using FluentAssertions;
using Npgsql;

namespace EdFi.DataManagementService.Tests.Integration.Tests.Postgresql;

/// <summary>
/// The endpoint-validation phase answers before the database-validation phase, so when both would
/// fail the request receives the endpoint verdict rather than a database availability error. None of
/// this depends on a derivative or on a routing header: it is the observable consequence of the phase
/// ordering alone, which is why it needs coverage of its own.
///
/// Each concrete fixture breaks exactly one database-validation stage and then drives every hoisted
/// verdict against it, so the matrix is stage-by-verdict rather than a single spot check.
///
/// The breakage is applied to the leased database before the host boots, which is required rather than
/// incidental: startup instance validation reads the fingerprint and the resource-key seed and caches
/// both verdicts permanently for the primary, so a database mutated after startup would still be
/// served from those cached verdicts and every assertion below would pass vacuously. The
/// <see cref="It_answers_503_when_the_request_reaches_the_broken_later_stage" /> control exists to keep
/// that failure mode detectable if the breakage ever stops working.
/// </summary>
public abstract class PostgresqlValidationPhasePrecedenceTestBase : PostgresqlApiIntegrationTestBase
{
    /// <summary>A resource the ApiSchema does not define, so endpoint validation rejects it.</summary>
    private const string UnknownResourceEndpoint = "/data/ed-fi/nonexistentThings";

    /// <summary>A resource the ApiSchema does define, used for the invalid mutation route shapes.</summary>
    private const string StudentsEndpoint = "/data/ed-fi/students";

    private const string ItemPath = "/data/ed-fi/students/00000000-0000-0000-0000-000000000001";

    /// <summary>
    /// The status the broken stage produces. Every stage of the database phase answers 503, which is
    /// why each test below also asserts the hoisted verdict's own status rather than only asserting
    /// that the response is not a 503.
    /// </summary>
    private const HttpStatusCode DatabaseValidationStatus = HttpStatusCode.ServiceUnavailable;

    protected override FixtureKey Fixture => FixtureKey.ProfileRootOnlyMerge;

    /// <summary>
    /// The SQL that breaks this fixture's stage, applied to the freshly provisioned database before the
    /// host starts.
    /// </summary>
    protected abstract string BreakLaterStageSql { get; }

    protected override async Task<string> LeaseDatabaseAsync(FixtureContext fixture)
    {
        string leasedConnectionString = await base.LeaseDatabaseAsync(fixture);

        await using NpgsqlConnection connection = new(leasedConnectionString);
        await connection.OpenAsync();
        await using NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = BreakLaterStageSql;
        await command.ExecuteNonQueryAsync();

        return leasedConnectionString;
    }

    private static StringContent JsonBody(string json) => new(json, Encoding.UTF8, "application/json");

    [Test]
    public async Task It_answers_503_when_the_request_reaches_the_broken_later_stage()
    {
        using HttpResponseMessage response = await Harness.HttpClient.GetAsync(StudentsEndpoint);

        response.StatusCode.Should().Be(DatabaseValidationStatus);
    }

    [Test]
    public async Task It_answers_the_endpoint_404_for_an_unroutable_request()
    {
        using HttpResponseMessage response = await Harness.HttpClient.GetAsync(UnknownResourceEndpoint);

        response.StatusCode.Should().NotBe(DatabaseValidationStatus);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task It_answers_the_route_semantics_405_for_a_collection_delete()
    {
        using HttpResponseMessage response = await Harness.HttpClient.DeleteAsync(StudentsEndpoint);

        response.StatusCode.Should().NotBe(DatabaseValidationStatus);
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Test]
    public async Task It_answers_the_route_semantics_405_for_a_collection_put()
    {
        using HttpResponseMessage response = await Harness.HttpClient.PutAsync(
            StudentsEndpoint,
            JsonBody("{}")
        );

        response.StatusCode.Should().NotBe(DatabaseValidationStatus);
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }

    [Test]
    public async Task It_answers_the_route_semantics_405_for_an_item_post()
    {
        using HttpResponseMessage response = await Harness.HttpClient.PostAsync(ItemPath, JsonBody("{}"));

        response.StatusCode.Should().NotBe(DatabaseValidationStatus);
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
    }
}

/// <summary>
/// Fingerprint validation fails: the provisioned database has no dms.EffectiveSchema row at all, which
/// is the unprovisioned-database case.
/// </summary>
public sealed class Given_Postgresql_Precedence_Over_Fingerprint_Failure
    : PostgresqlValidationPhasePrecedenceTestBase
{
    protected override string BreakLaterStageSql => """DELETE FROM dms."EffectiveSchema";""";
}

/// <summary>
/// Fingerprint validation succeeds and resource-key validation then fails: the recorded effective
/// schema hash still matches what the process expects, so the fingerprint is valid, while the recorded
/// resource-key seed hash no longer matches the seed the process computed.
/// </summary>
public sealed class Given_Postgresql_Precedence_Over_Resource_Key_Mismatch
    : PostgresqlValidationPhasePrecedenceTestBase
{
    protected override string BreakLaterStageSql =>
        """UPDATE dms."EffectiveSchema" SET "ResourceKeySeedHash" = decode(repeat('ab', 32), 'hex');""";
}
