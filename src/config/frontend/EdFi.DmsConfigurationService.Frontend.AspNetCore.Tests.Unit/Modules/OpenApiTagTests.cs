// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Modules;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

/// <summary>
/// Operations in the generated /openapi/v1.json carry exactly one tag, named for the resource
/// area in the Ed-Fi Management API style, never a module class name or the assembly name.
/// </summary>
public class OpenApiTagTests
{
    private static readonly string[] HttpMethodNames =
    [
        "get",
        "put",
        "post",
        "delete",
        "patch",
        "head",
        "options",
        "trace",
    ];

    private static readonly string[] SingleTenantTags =
    [
        "Actions",
        "ApiClients",
        "Applications",
        "AuthorizationMetadata",
        "AuthorizationStrategies",
        "ClaimSets",
        "ClaimsManagement",
        "Connect",
        "DataStoreContexts",
        "DataStoreDerivatives",
        "DataStores",
        "Discovery",
        "Health",
        "Information",
        "Jobs",
        "OwnershipTokens",
        "Profiles",
        "ResourceClaims",
        "Vendors",
    ];

    private static async Task<JsonDocument> FetchDocumentAsync(bool multiTenancy)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["AppSettings:MultiTenancy"] = multiTenancy.ToString() }
                )
            );
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static IEnumerable<(string Key, JsonElement Operation)> Operations(JsonDocument document) =>
        document
            .RootElement.GetProperty("paths")
            .EnumerateObject()
            .SelectMany(path =>
                path.Value.EnumerateObject()
                    .Where(op => HttpMethodNames.Contains(op.Name))
                    .Select(op => ($"{op.Name.ToUpperInvariant()} {path.Name}", op.Value))
            );

    private static string[] OperationTags(JsonElement operation) =>
        [.. operation.GetProperty("tags").EnumerateArray().Select(t => t.GetString()!)];

    private static string[] DistinctOperationTags(JsonDocument document) =>
        [.. Operations(document).SelectMany(op => OperationTags(op.Operation)).Distinct().Order()];

    private static string[] TopLevelTags(JsonDocument document) =>
        [
            .. document
                .RootElement.GetProperty("tags")
                .EnumerateArray()
                .Select(t => t.GetProperty("name").GetString()!)
                .Order(),
        ];

    [TestFixture]
    public class Given_the_default_configuration
    {
        private JsonDocument _document = null!;

        [SetUp]
        public async Task Setup()
        {
            _document = await FetchDocumentAsync(multiTenancy: false);
        }

        [TearDown]
        public void TearDown() => _document.Dispose();

        [Test]
        public void It_uses_exactly_the_management_api_style_tags()
        {
            DistinctOperationTags(_document).Should().Equal(SingleTenantTags.Order());
        }

        [Test]
        public void It_gives_every_operation_exactly_one_tag()
        {
            var operations = Operations(_document).ToArray();

            operations.Should().NotBeEmpty();
            operations
                .Where(op => OperationTags(op.Operation).Length != 1)
                .Select(op => op.Key)
                .Should()
                .BeEmpty();
        }

        [Test]
        public void It_lists_exactly_the_same_tags_at_the_top_level()
        {
            TopLevelTags(_document).Should().Equal(SingleTenantTags.Order());
        }

        [Test]
        public void It_tags_the_health_lambda_endpoint()
        {
            var health = Operations(_document).Single(op => op.Key == "GET /health");

            OperationTags(health.Operation).Should().Equal("Health");
        }
    }

    [TestFixture]
    public class Given_multi_tenancy_enabled
    {
        private JsonDocument _document = null!;

        [SetUp]
        public async Task Setup()
        {
            _document = await FetchDocumentAsync(multiTenancy: true);
        }

        [TearDown]
        public void TearDown() => _document.Dispose();

        [Test]
        public void It_adds_only_the_tenants_tag()
        {
            DistinctOperationTags(_document).Should().Equal(SingleTenantTags.Append("Tenants").Order());
        }

        [Test]
        public void It_gives_every_operation_exactly_one_tag()
        {
            Operations(_document)
                .Where(op => OperationTags(op.Operation).Length != 1)
                .Select(op => op.Key)
                .Should()
                .BeEmpty();
        }

        [Test]
        public void It_lists_exactly_the_same_tags_at_the_top_level()
        {
            TopLevelTags(_document).Should().Equal(SingleTenantTags.Append("Tenants").Order());
        }
    }

    [TestFixture]
    public class Given_the_endpoint_modules_in_the_frontend_assembly
    {
        private Type[] _modules = null!;

        [SetUp]
        public void Setup()
        {
            _modules =
            [
                .. typeof(IEndpointModule)
                    .Assembly.GetTypes()
                    .Where(t =>
                        typeof(IEndpointModule).IsAssignableFrom(t)
                        && t is { IsClass: true, IsAbstract: false }
                    ),
            ];
        }

        [Test]
        public void It_finds_the_modules()
        {
            _modules.Should().Contain(typeof(MetadataModule)).And.Contain(typeof(HealthModule));
        }

        [Test]
        public void It_has_a_tag_for_every_module_except_metadata()
        {
            _modules
                .Where(m => m != typeof(MetadataModule))
                .Where(m => !OpenApiModuleTags.ByModule.ContainsKey(m))
                .Select(m => m.Name)
                .Should()
                .BeEmpty();
        }

        [Test]
        public void It_has_no_tag_that_is_not_a_module()
        {
            OpenApiModuleTags.ByModule.Keys.Where(k => !_modules.Contains(k)).Should().BeEmpty();
        }

        [Test]
        public void It_names_no_tag_after_a_module_class_or_the_assembly()
        {
            OpenApiModuleTags
                .ByModule.Values.Where(tag =>
                    tag.EndsWith("Module", StringComparison.Ordinal)
                    || tag == typeof(IEndpointModule).Assembly.GetName().Name
                )
                .Should()
                .BeEmpty();
        }
    }
}
