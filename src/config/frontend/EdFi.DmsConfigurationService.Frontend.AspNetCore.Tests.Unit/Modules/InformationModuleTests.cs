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
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Infrastructure;
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

[TestFixture("", "/", "http://localhost/tenancy")]
[TestFixture("mt-config", "/mt-config", "http://localhost/mt-config/tenancy")]
[TestFixture("mt-config", "/mt-config/", "http://localhost/mt-config/tenancy")]
public class Given_MultiTenancy_Is_Enabled_And_An_Information_Discovery_Request_Without_A_Tenant_Header(
    string pathBase,
    string path,
    string expectedTenancyUrl
) : MultiTenantPipelineTestBase
{
    private HttpStatusCode _statusCode;
    private string _body = null!;

    [SetUp]
    public async Task Setup()
    {
        // UsePathBase leaves an empty path for "/mt-config" and "/" for "/mt-config/"; both are the root.
        await using var factory = CreateMultiTenantFactory(pathBase);
        using var client = factory.CreateClient();

        // No Tenant header and no credentials
        var response = await client.GetAsync(path);
        _statusCode = response.StatusCode;
        _body = await response.Content.ReadAsStringAsync();
    }

    [Test]
    public void It_returns_200()
    {
        _statusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_advertises_an_absolute_tenancy_url()
    {
        JsonNode.Parse(_body)!["urls"]!["tenancy"]!.GetValue<string>().Should().Be(expectedTenancyUrl);
    }
}

[TestFixture]
public class Given_MultiTenancy_Is_Disabled_And_An_Information_Request
{
    private string _body = null!;

    [SetUp]
    public async Task Setup()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Test")
        );
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/");
        _body = await response.Content.ReadAsStringAsync();
    }

    [Test]
    public void It_returns_the_information_body_with_only_urls_tenancy_added()
    {
        // Byte-for-byte: field names, order and values. Each value is serialized with the default
        // encoder, the one the minimal API response uses, so escaping matches as well.
        static string Json(string value) => JsonSerializer.Serialize(value);

        _body
            .Should()
            .Be(
                "{"
                    + $"\"version\":{Json(ApiVersionDetails.Version)},"
                    + $"\"applicationName\":{Json(ApiVersionDetails.ApplicationName)},"
                    + $"\"informationalVersion\":{Json(ApiVersionDetails.InformationalVersion)},"
                    + $"\"build\":{Json(ApiVersionDetails.Build)},"
                    + "\"urls\":{"
                    + "\"openApiMetadata\":\"http://localhost/metadata/specifications\","
                    + "\"tenancy\":\"http://localhost/tenancy\""
                    + "},"
                    + "\"specificationVersion\":\"v3\""
                    + "}"
            );
    }
}

[TestFixture]
public class Given_A_PathBase_And_An_Information_Request
{
    private string _body = null!;

    [SetUp]
    public async Task Setup()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(configuration =>
                configuration.AddInMemoryCollection(
                    new Dictionary<string, string?> { ["AppSettings:PathBase"] = "dms-config" }
                )
            );
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/dms-config");
        _body = await response.Content.ReadAsStringAsync();
    }

    [Test]
    public void It_advertises_the_tenancy_url_under_the_path_base()
    {
        JsonNode.Parse(_body)!["urls"]!["tenancy"]!
            .GetValue<string>()
            .Should()
            .Be("http://localhost/dms-config/tenancy");
    }
}

[TestFixture]
public class Given_The_Advertised_Tenancy_Url_In_Single_Tenant_Mode
{
    private HttpStatusCode _statusCode;
    private string _body = null!;

    [SetUp]
    public async Task Setup()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.UseEnvironment("Test")
        );
        using var client = factory.CreateClient();

        // No credentials on either request
        var information = await client.GetStringAsync("/");
        var tenancyUrl = JsonNode.Parse(information)!["urls"]!["tenancy"]!.GetValue<string>();

        var response = await client.GetAsync(tenancyUrl);
        _statusCode = response.StatusCode;
        _body = await response.Content.ReadAsStringAsync();
    }

    [Test]
    public void It_returns_200()
    {
        _statusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_returns_an_empty_tenants_array()
    {
        _body.Should().Be("""{"tenants":[]}""");
    }
}

[TestFixture]
public class Given_The_Advertised_Tenancy_Url_In_Multi_Tenant_Mode_Under_A_PathBase
    : MultiTenantPipelineTestBase
{
    private string _tenancyUrl = null!;
    private HttpStatusCode _statusCode;
    private string _body = null!;

    [SetUp]
    public async Task Setup()
    {
        var tenantRepository = A.Fake<ITenantRepository>();
        A.CallTo(() => tenantRepository.QueryTenant(A<PagingQuery>.Ignored))
            .Returns(
                new TenantQueryResult.Success([
                    new TenantResponse { Id = 1, Name = "Tenant_A" },
                    new TenantResponse { Id = 2, Name = "Tenant_B" },
                ])
            );

        await using var factory = CreateMultiTenantFactory("mt-config", tenantRepository);
        using var client = factory.CreateClient();

        // The complete discovery path: no Tenant header and no credentials on either request.
        var information = await client.GetStringAsync("/mt-config/");
        _tenancyUrl = JsonNode.Parse(information)!["urls"]!["tenancy"]!.GetValue<string>();

        var response = await client.GetAsync(_tenancyUrl);
        _statusCode = response.StatusCode;
        _body = await response.Content.ReadAsStringAsync();
    }

    [Test]
    public void It_advertises_the_path_base_tenancy_url()
    {
        _tenancyUrl.Should().Be("http://localhost/mt-config/tenancy");
    }

    [Test]
    public void It_returns_200()
    {
        _statusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void It_lists_every_configured_tenant_name()
    {
        JsonNode.Parse(_body)!["tenants"]!
            .AsArray()
            .Select(name => name!.GetValue<string>())
            .Should()
            .BeEquivalentTo("Tenant_A", "Tenant_B");
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
