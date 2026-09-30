// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.DmsConfigurationService.Backend.Repositories;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.Tenant;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Modules;

[TestFixture]
public class InformationModuleTests
{
    [Test]
    public async Task Information_Endpoint_Returns_Ok_Response()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task Information_Endpoint_Returns_Expected_Structure()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();
        var jsonDoc = JsonDocument.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonDoc.RootElement.TryGetProperty("version", out _).Should().BeTrue();
        jsonDoc.RootElement.TryGetProperty("applicationName", out _).Should().BeTrue();
        jsonDoc.RootElement.TryGetProperty("informationalVersion", out _).Should().BeTrue();
        jsonDoc.RootElement.TryGetProperty("build", out var build).Should().BeTrue();
        build.GetString().Should().NotBeNullOrEmpty();
        jsonDoc.RootElement.TryGetProperty("urls", out var urls).Should().BeTrue();
        urls.TryGetProperty("openApiMetadata", out _).Should().BeTrue();
        jsonDoc
            .RootElement.TryGetProperty("specificationVersion", out var specificationVersion)
            .Should()
            .BeTrue();
        specificationVersion.GetString().Should().BeOneOf("v1", "v2", "v3");
    }

    [Test]
    public async Task It_should_return_a_four_part_build_version()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();
        var jsonDoc = JsonDocument.Parse(content);

        // Assert
        jsonDoc.RootElement.TryGetProperty("build", out var build).Should().BeTrue();
        var buildValue = build.GetString();
        buildValue.Should().NotBeNullOrEmpty();
        Regex
            .IsMatch(buildValue!, @"^\d+\.\d+\.\d+\.\d+$")
            .Should()
            .BeTrue(
                because: $"build value '{buildValue}' should match the four-part numeric version pattern"
            );
    }

    [Test]
    public async Task Information_Endpoint_Returns_SpecificationVersion_From_Config()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["AppSettings:SpecificationVersion"] = "v2" }
                    );
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();
        var jsonDoc = JsonDocument.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonDoc
            .RootElement.TryGetProperty("specificationVersion", out var specificationVersion)
            .Should()
            .BeTrue();
        specificationVersion.GetString().Should().Be("v2");
    }

    [Test]
    public async Task Information_Endpoint_Normalizes_SpecificationVersion_To_Lowercase()
    {
        // Arrange
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["AppSettings:SpecificationVersion"] = "V2" }
                    );
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync("/");
        var content = await response.Content.ReadAsStringAsync();
        var jsonDoc = JsonDocument.Parse(content);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        jsonDoc
            .RootElement.TryGetProperty("specificationVersion", out var specificationVersion)
            .Should()
            .BeTrue();
        specificationVersion.GetString().Should().Be("v2");
    }

    [Test]
    public async Task When_PathBase_Provided_Information_Endpoint_Returns_Ok_Response()
    {
        // Arrange
        var pathBase = "dms-config";
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (context, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?> { ["AppSettings:PathBase"] = pathBase }
                    );
                }
            );
        });
        using var client = factory.CreateClient();

        // Act
        var response = await client.GetAsync($"/{pathBase}");
        var content = await response.Content.ReadAsStringAsync();

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        content.Should().NotBeNullOrEmpty();
    }
}

[TestFixture("", "/")]
[TestFixture("mt-config", "/mt-config")]
[TestFixture("mt-config", "/mt-config/")]
public class Given_MultiTenancy_Is_Enabled_And_An_Information_Discovery_Request_Without_A_Tenant_Header(
    string pathBase,
    string path
) : MultiTenantPipelineTestBase
{
    private HttpStatusCode _statusCode;

    [SetUp]
    public async Task Setup()
    {
        // UsePathBase leaves an empty path for "/mt-config" and "/" for "/mt-config/"; both are the root.
        await using var factory = CreateMultiTenantFactory(pathBase);
        using var client = factory.CreateClient();

        // No Tenant header and no credentials
        var response = await client.GetAsync(path);
        _statusCode = response.StatusCode;
    }

    [Test]
    public void It_returns_200()
    {
        _statusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>
/// Sends one GET /tenancy through the full pipeline with no credentials, against a fake tenant
/// repository, and captures the response for the DMS-1508 fixtures below.
/// </summary>
public abstract class TenancyRequestTestBase : MultiTenantPipelineTestBase
{
    protected static readonly TenantResponse[] TwoTenants =
    [
        new() { Id = 41, Name = "Tenant_A" },
        new() { Id = 42, Name = "Tenant_B" },
    ];

    protected ITenantRepository TenantRepository { get; private set; } = null!;
    protected HttpStatusCode StatusCode { get; private set; }
    protected string? ContentType { get; private set; }
    protected string Body { get; private set; } = null!;

    protected async Task SendTenancyRequestAsync(
        bool multiTenancy,
        TenantQueryResult queryResult,
        string path = "/tenancy",
        string? tenantHeader = null
    )
    {
        TenantRepository = A.Fake<ITenantRepository>();
        A.CallTo(() => TenantRepository.QueryTenant(A<PagingQuery>.Ignored)).Returns(queryResult);
        A.CallTo(() => TenantRepository.GetTenantByName(A<string>.Ignored))
            .Returns(new TenantGetByNameResult.FailureNotFound());
        A.CallTo(() => TenantRepository.GetTenantByName("Tenant_A"))
            .Returns(new TenantGetByNameResult.Success(TwoTenants[0]));

        await using var factory = multiTenancy
            ? CreateMultiTenantFactory(tenantRepository: TenantRepository)
            : new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureServices(services => services.AddTransient(_ => TenantRepository));
            });
        using var client = factory.CreateClient();
        if (tenantHeader is not null)
        {
            client.DefaultRequestHeaders.Add("Tenant", tenantHeader);
        }

        // No credentials
        var response = await client.GetAsync(path);
        StatusCode = response.StatusCode;
        ContentType = response.Content.Headers.ContentType?.MediaType;
        Body = await response.Content.ReadAsStringAsync();
    }

    protected string[] TenantNames() =>
        [.. JsonNode.Parse(Body)!["tenants"]!.AsArray().Select(name => name!.GetValue<string>())];
}

[TestFixture]
public class Given_MultiTenancy_Is_Disabled_And_A_Tenancy_Request : TenancyRequestTestBase
{
    [SetUp]
    public async Task Setup()
    {
        // The fake would return two tenants, so an empty list proves the repository is not consulted.
        await SendTenancyRequestAsync(multiTenancy: false, new TenantQueryResult.Success(TwoTenants));
    }

    [Test]
    public void It_returns_200()
    {
        StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_returns_an_empty_tenants_array()
    {
        Body.Should().Be("""{"tenants":[]}""");
    }

    [Test]
    public void It_does_not_query_tenants()
    {
        A.CallTo(() => TenantRepository.QueryTenant(A<PagingQuery>.Ignored)).MustNotHaveHappened();
    }
}

[TestFixture("/tenancy")]
[TestFixture("/TENANCY")]
[TestFixture("/tenancy/")]
public class Given_MultiTenancy_Is_Enabled_And_A_Tenancy_Request_Without_A_Tenant_Header(string path)
    : TenancyRequestTestBase
{
    [SetUp]
    public async Task Setup()
    {
        await SendTenancyRequestAsync(multiTenancy: true, new TenantQueryResult.Success(TwoTenants), path);
    }

    [Test]
    public void It_returns_200()
    {
        StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_lists_every_configured_tenant_name()
    {
        TenantNames().Should().BeEquivalentTo("Tenant_A", "Tenant_B");
    }

    [Test]
    public void It_returns_names_only()
    {
        var root = JsonNode.Parse(Body)!.AsObject();
        root.Select(property => property.Key).Should().Equal("tenants");
        root["tenants"]!
            .AsArray()
            .Should()
            .OnlyContain(element => element!.GetValueKind() == JsonValueKind.String);
        Body.Should().NotContain("41").And.NotContain("42");
    }

    [Test]
    public void It_reads_tenants_without_a_row_cap()
    {
        A.CallTo(() =>
                TenantRepository.QueryTenant(
                    A<PagingQuery>.That.Matches(query => query.Limit == null && query.Offset == null)
                )
            )
            .MustHaveHappenedOnceExactly();
    }
}

[TestFixture]
public class Given_MultiTenancy_Is_Enabled_And_A_Tenancy_Request_With_An_Unknown_Tenant_Header
    : TenancyRequestTestBase
{
    [SetUp]
    public async Task Setup()
    {
        await SendTenancyRequestAsync(
            multiTenancy: true,
            new TenantQueryResult.Success(TwoTenants),
            tenantHeader: "Unknown_Tenant"
        );
    }

    [Test]
    public void It_returns_200()
    {
        StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_lists_every_configured_tenant_name()
    {
        TenantNames().Should().BeEquivalentTo("Tenant_A", "Tenant_B");
    }

    [Test]
    public void It_does_not_resolve_a_tenant()
    {
        A.CallTo(() => TenantRepository.GetTenantByName(A<string>.Ignored)).MustNotHaveHappened();
    }
}

[TestFixture]
public class Given_MultiTenancy_Is_Enabled_And_A_Tenancy_Request_With_A_Valid_Tenant_Header
    : TenancyRequestTestBase
{
    [SetUp]
    public async Task Setup()
    {
        await SendTenancyRequestAsync(
            multiTenancy: true,
            new TenantQueryResult.Success(TwoTenants),
            tenantHeader: "Tenant_A"
        );
    }

    [Test]
    public void It_lists_every_tenant_not_only_the_header_tenant()
    {
        TenantNames().Should().BeEquivalentTo("Tenant_A", "Tenant_B");
    }
}

[TestFixture]
public class Given_MultiTenancy_Is_Enabled_And_A_Tenancy_Request_With_No_Tenants : TenancyRequestTestBase
{
    [SetUp]
    public async Task Setup()
    {
        await SendTenancyRequestAsync(multiTenancy: true, new TenantQueryResult.Success([]));
    }

    [Test]
    public void It_returns_200()
    {
        StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_returns_an_empty_tenants_array()
    {
        Body.Should().Be("""{"tenants":[]}""");
    }
}

[TestFixture]
public class Given_MultiTenancy_Is_Enabled_And_A_Tenancy_Request_Whose_Tenant_Query_Fails
    : TenancyRequestTestBase
{
    private const string Sentinel = "SENTINEL_TENANCY_DB_7c1e_must_not_leak";

    [SetUp]
    public async Task Setup()
    {
        await SendTenancyRequestAsync(multiTenancy: true, new TenantQueryResult.FailureUnknown(Sentinel));
    }

    [Test]
    public void It_returns_500()
    {
        StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }

    [Test]
    public void It_returns_the_internal_server_error_body()
    {
        ContentType.Should().Be("application/problem+json");
        var body = JsonNode.Parse(Body)!.AsObject();
        body["type"]!.GetValue<string>().Should().Be("urn:ed-fi:api:internal-server-error");
        body["correlationId"]!.GetValue<string>().Should().NotBeNullOrEmpty();
    }

    [Test]
    public void It_does_not_leak_the_failure_message()
    {
        Body.Should().NotContain(Sentinel);
    }
}

[TestFixture]
public class Given_MultiTenancy_Is_Enabled_And_A_Tenancy_Request_Whose_Tenant_Query_Returns_An_Unrecognized_Result
    : TenancyRequestTestBase
{
    [SetUp]
    public async Task Setup()
    {
        await SendTenancyRequestAsync(multiTenancy: true, new TenantQueryResult());
    }

    [Test]
    public void It_returns_500()
    {
        StatusCode.Should().Be(HttpStatusCode.InternalServerError);
    }
}
