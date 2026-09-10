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
    public void It_declares_the_numeric_api_client_get_id_parameter_as_int32()
    {
        // The Management API specification models this route's identifier as integer/int32. The
        // generator infers that from the int handler parameter behind the {id:int} constraint.
        JsonNode parameter = GetPathParameter("/v3/apiClients/{id}", "id");

        parameter["required"]!.GetValue<bool>().Should().BeTrue();
        parameter["schema"]!["type"]!.GetValue<string>().Should().Be("integer");
        parameter["schema"]!["format"]!.GetValue<string>().Should().Be("int32");
    }

    [Test]
    public void It_declares_the_client_key_api_client_get_parameter_as_a_string()
    {
        JsonNode parameter = GetPathParameter("/v3/apiClients/{clientId}", "clientId");

        parameter["required"]!.GetValue<bool>().Should().BeTrue();
        parameter["schema"]!["type"]!.GetValue<string>().Should().Be("string");
    }

    [Test]
    public void It_describes_which_identifier_each_api_client_get_takes()
    {
        // The two single-item GETs share a path hierarchy and differ only by parameter name, so the
        // published text is what tells a reader which identifier each one resolves.
        JsonNode numericGet = SingleItemGetOperation("/v3/apiClients/{id}");
        JsonNode clientKeyGet = SingleItemGetOperation("/v3/apiClients/{clientId}");

        numericGet["summary"]!.GetValue<string>().Should().Contain("numeric identifier");
        numericGet["description"]!.GetValue<string>().Should().Contain("not a valid 32-bit integer");
        clientKeyGet["summary"]!.GetValue<string>().Should().Contain("OAuth client key");
        clientKeyGet["description"]!.GetValue<string>().Should().Contain("is a valid 32-bit integer");
    }

    [Test]
    public void It_publishes_the_numeric_get_without_its_route_constraint()
    {
        // The route is registered as {id:int}. The document must key the operation under the
        // constraint-free path the specification names, with no separate /v3/apiClients/{id:int}.
        List<string> pathKeys = [.. _document["paths"]!.AsObject().Select(path => path.Key)];

        pathKeys.Should().Contain("/v3/apiClients/{id}");
        pathKeys.Should().OnlyContain(key => !key.Contains(':'));
    }
}
