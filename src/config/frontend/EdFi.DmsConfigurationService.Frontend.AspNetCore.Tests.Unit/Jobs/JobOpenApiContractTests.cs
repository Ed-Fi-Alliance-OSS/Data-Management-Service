// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Jobs;

/// <summary>
/// Asserts the <c>GET /v3/jobs/{jobId}</c> contract against the document the application serves (spec §5), and
/// compares its response schema with the pinned upstream Admin API v3 fragment (§1.4.1).
/// </summary>
[TestFixture]
public class Given_the_served_openapi_document_for_jobs
{
    private const string JobPath = "/v3/jobs/{jobId}";

    /// <summary>
    /// <c>jobStatusResult</c> from <c>reference/design/jobs-DMS-1437/admin-api-v3-ed115fd8.yaml</c> (SHA-256
    /// f0735ae1…466b): each property's type, format, and upstream nullability.
    /// </summary>
    private static readonly (
        string Name,
        string Type,
        string? Format,
        bool UpstreamNullable
    )[] _upstreamFragment =
    [
        ("jobId", "string", null, true),
        ("status", "string", null, true),
        ("createdAt", "string", "date-time", false),
        ("finishedAt", "string", "date-time", true),
        ("errorMessage", "string", null, true),
    ];

    /// <summary>
    /// Upstream marks these nullable only because Swashbuckle 7.1 annotates unannotated reference types; the source
    /// never returns null for them, and CMS publishes them non-nullable (§1.4.1).
    /// </summary>
    private static readonly string[] _intentionallyNonNullable = ["jobId", "status"];

    private readonly WebApplicationFactoryTracker<Program> _factoryTracker = new();
    private JsonNode _document = JsonNode.Parse("{}")!;

    [SetUp]
    public async Task SetUp()
    {
        var factory = _factoryTracker.Track(
            new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(collection => collection.AddTestAuthentication());
            })
        );

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        _document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    [TearDown]
    public void DisposeWebApplicationFactories() => _factoryTracker.DisposeTrackedFactories();

    private JsonNode Operation => _document["paths"]![JobPath]!["get"]!;

    private JsonNode Response(string status) => Operation["responses"]![status]!;

    private JsonObject ResponseSchema
    {
        get
        {
            JsonNode schema = Response("200")["content"]!["application/json"]!["schema"]!;
            return (
                schema["$ref"] is JsonNode reference ? Resolve(reference.GetValue<string>()) : schema
            ).AsObject();
        }
    }

    private JsonObject Properties => ResponseSchema["properties"]!.AsObject();

    private JsonNode Resolve(string reference) =>
        _document["components"]!["schemas"]![reference["#/components/schemas/".Length..]]!;

    /// <summary>The property's types: OpenAPI 3.1 writes a nullable one as a type array that includes "null".</summary>
    private string[] TypesOf(string property) =>
        Properties[property]!["type"] switch
        {
            JsonArray types => [.. types.Select(type => type!.GetValue<string>())],
            JsonNode type => [type.GetValue<string>()],
            null => [],
        };

    private string? FormatOf(string property) => Properties[property]!["format"]?.GetValue<string>();

    [Test]
    public void It_publishes_the_get_operation() => _document["paths"]![JobPath]!["get"].Should().NotBeNull();

    [Test]
    public void It_declares_job_id_as_a_required_string_path_parameter()
    {
        JsonNode parameter = Operation["parameters"]!
            .AsArray()
            .Single(candidate => candidate!["name"]!.GetValue<string>() == "jobId")!;

        parameter["in"]!.GetValue<string>().Should().Be("path");
        parameter["required"]!.GetValue<bool>().Should().BeTrue();
        parameter["schema"]!["type"]!.GetValue<string>().Should().Be("string");
        parameter["schema"]!["format"].Should().BeNull();
    }

    [Test]
    public void It_declares_exactly_the_five_contract_properties() =>
        Properties
            .Select(property => property.Key)
            .Should()
            .BeEquivalentTo(_upstreamFragment.Select(property => property.Name));

    [Test]
    public void It_matches_the_upstream_types_and_formats()
    {
        foreach ((string name, string type, string? format, _) in _upstreamFragment)
        {
            TypesOf(name).Should().Contain(type, $"'{name}' is a {type} upstream");
            FormatOf(name).Should().Be(format, $"'{name}' has format '{format}' upstream");
        }
    }

    [Test]
    public void It_matches_the_upstream_nullability_except_the_documented_generator_artifacts()
    {
        foreach ((string name, _, _, bool upstreamNullable) in _upstreamFragment)
        {
            bool expected = upstreamNullable && !_intentionallyNonNullable.Contains(name);
            TypesOf(name).Contains("null").Should().Be(expected, $"'{name}' nullability");
        }
    }

    [Test]
    public void It_declares_not_found_as_problem_json() =>
        Response("404")["content"]!["application/problem+json"].Should().NotBeNull();
}
