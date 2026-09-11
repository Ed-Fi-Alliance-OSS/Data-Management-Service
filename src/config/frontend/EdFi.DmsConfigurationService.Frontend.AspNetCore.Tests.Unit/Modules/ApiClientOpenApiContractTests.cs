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

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// Asserts the api-client data store assignment contract against the document the application
/// actually serves, rather than against the attributes and transformers that produce it.
/// </summary>
[TestFixture]
public class Given_the_served_openapi_document
{
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
        var response = await client.GetAsync("/openapi/v1.json");
        _document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    [TearDown]
    public void DisposeWebApplicationFactories() => _factoryTracker.DisposeTrackedFactories();

    private JsonNode DataStoreIdsSchema(string schemaName) =>
        _document["components"]!["schemas"]![schemaName]!["properties"]!["dataStoreIds"]!;

    private string DataStoreIdsDescription(string schemaName) =>
        DataStoreIdsSchema(schemaName)["description"]!.GetValue<string>();

    private JsonNode RequestBodyExample(string path, string method) =>
        _document["paths"]![path]![method]!["requestBody"]!["content"]!["application/json"]!["example"]!;

    private JsonNode SingleItemGetOperation(string path) => _document["paths"]![path]!["get"]!;

    private JsonNode GetPathParameter(string path, string parameterName) =>
        SingleItemGetOperation(path)["parameters"]!
            .AsArray()
            .Single(parameter =>
                parameter!["name"]!.GetValue<string>() == parameterName
                && parameter["in"]!.GetValue<string>() == "path"
            )!;

    /// <summary>True for the single-item apiClient path itself, excluding sub-resource paths such
    /// as the ownership and reset-credential routes.</summary>
    private static bool IsApiClientItemPath(string path) =>
        path.StartsWith("/v3/apiClients/", StringComparison.Ordinal)
        && path.EndsWith('}')
        && !path["/v3/apiClients/".Length..].Contains('/');

    [Test]
    public void It_documents_the_insert_data_store_ids_as_optional_with_empty_allowed()
    {
        string description = DataStoreIdsDescription("ApiClientInsertCommand");
        description.Should().Contain("Optional");
        description.Should().Contain("empty array");
        description.Should().Contain("no Data Store assignment");
    }

    [Test]
    public void It_documents_the_insert_rejection_of_an_explicit_null()
    {
        DataStoreIdsDescription("ApiClientInsertCommand").Should().Contain("null is rejected");
    }

    [Test]
    public void It_documents_that_supplied_insert_ids_are_tenant_scoped()
    {
        DataStoreIdsDescription("ApiClientInsertCommand").Should().Contain("caller's tenant");
    }

    [Test]
    public void It_documents_the_update_data_store_ids_as_a_full_replacement()
    {
        string description = DataStoreIdsDescription("ApiClientUpdateCommand");
        description.Should().Contain("full replacement");
        description.Should().Contain("omitting the property");
        description.Should().Contain("removes every existing assignment");
    }

    [Test]
    public void It_documents_the_update_rejection_of_an_explicit_null()
    {
        DataStoreIdsDescription("ApiClientUpdateCommand").Should().Contain("null is rejected");
    }

    [Test]
    public void It_documents_that_supplied_update_ids_are_tenant_scoped()
    {
        DataStoreIdsDescription("ApiClientUpdateCommand").Should().Contain("caller's tenant");
    }

    [TestCase("ApiClientInsertCommand")]
    [TestCase("ApiClientUpdateCommand")]
    public void It_keeps_data_store_ids_out_of_the_required_list(string schemaName)
    {
        var required = _document["components"]!["schemas"]![schemaName]!["required"]!
            .AsArray()
            .Select(entry => entry!.GetValue<string>());

        required.Should().NotContain("dataStoreIds");
    }

    [TestCase("ApiClientInsertCommand")]
    [TestCase("ApiClientUpdateCommand")]
    public void It_keeps_data_store_ids_a_non_nullable_array(string schemaName)
    {
        // A nullable property is emitted as a type union including "null"; a plain "array" is the
        // proof that an explicit null is not part of the published contract.
        DataStoreIdsSchema(schemaName)["type"]!.GetValue<string>().Should().Be("array");
    }

    [TestCase("/v3/apiClients", "post")]
    [TestCase("/v3/apiClients/{id}", "put")]
    public void It_publishes_an_empty_data_store_assignment_example(string path, string method)
    {
        RequestBodyExample(path, method)["dataStoreIds"]!.AsArray().Should().BeEmpty();
    }

    [Test]
    public void It_declares_the_api_client_get_id_parameter_as_a_string()
    {
        // One route now carries both identifier forms, so the published parameter is the raw
        // segment. PUT and DELETE on this same path keep their int32 id, which is deliberate:
        // they accept only the numeric identifier.
        JsonNode parameter = GetPathParameter("/v3/apiClients/{id}", "id");

        parameter["required"]!.GetValue<bool>().Should().BeTrue();
        parameter["schema"]!["type"]!.GetValue<string>().Should().Be("string");
    }

    [Test]
    public void It_publishes_exactly_one_single_item_get_for_api_clients()
    {
        // Two templated paths that differ only by parameter name are the same path to OpenAPI, and
        // a generator may deduplicate them arbitrarily. Only one item path may carry a get.
        List<string> singleItemGetPaths =
        [
            .. _document["paths"]!
                .AsObject()
                .Where(path => path.Value!["get"] is not null && IsApiClientItemPath(path.Key))
                .Select(path => path.Key),
        ];

        singleItemGetPaths.Should().Equal("/v3/apiClients/{id}");
    }

    [Test]
    public void It_documents_the_key_first_identifier_dispatch()
    {
        // The segment is ambiguous by construction, so the published text is the only place a
        // reader learns which identifier wins when both could match.
        JsonNode operation = SingleItemGetOperation("/v3/apiClients/{id}");
        string summary = operation["summary"]!.GetValue<string>();
        string description = operation["description"]!.GetValue<string>();

        summary.Should().Contain("OAuth client key");
        summary.Should().Contain("numeric identifier");
        description.Should().Contain("key-first");
        description.Should().Contain("wins whenever it exists");
        description.Should().Contain("Only when no client key matches");
        description.Should().Contain("32-bit integer");
    }
}
