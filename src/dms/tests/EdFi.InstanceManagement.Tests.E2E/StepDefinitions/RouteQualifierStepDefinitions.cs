// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using EdFi.InstanceManagement.Tests.E2E.Management;
using FluentAssertions;
using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

[Binding]
public class RouteQualifierStepDefinitions(InstanceManagementContext context)
{
    [Given("the system is configured with route qualifiers")]
    public void GivenTheSystemIsConfiguredWithRouteQualifiers()
    {
        // Verify ROUTE_QUALIFIER_SEGMENTS is set in environment
        // This is validated by the setup script
        TestConfiguration.RouteQualifierSegments.Should().NotBeEmpty();

        Console.WriteLine(
            $"Route qualifier segments configured: {string.Join(", ", TestConfiguration.RouteQualifierSegments)}"
        );
        Console.WriteLine($"DMS API URL: {TestConfiguration.DmsApiUrl}");
    }

    [Given("I am authenticated to DMS with application credentials")]
    public async Task GivenIAmAuthenticatedToDmsWithApplicationCredentials()
    {
        context.ClientKey.Should().NotBeNullOrEmpty("Application must be created first");
        context.ClientSecret.Should().NotBeNullOrEmpty("Application must be created first");

        var tokenUrl = await ResolveDmsTokenUrlAsync(context.CurrentTenant);

        context.DmsToken = await TokenHelper.GetReusableDmsTokenAsync(
            tokenUrl,
            context.ClientKey!,
            context.ClientSecret!
        );

        context.DmsClient = new DmsApiClient(
            TestConfiguration.DmsApiUrl,
            context.DmsToken,
            context.CurrentTenant
        );
    }

    [Given("I am authenticated to DMS with credentials for tenant {string}")]
    [When("I am authenticated to DMS with credentials for tenant {string}")]
    public async Task GivenIAmAuthenticatedToDmsWithCredentialsForTenant(string tenantName)
    {
        context
            .CredentialsByTenant.Should()
            .ContainKey(tenantName, $"Credentials for tenant {tenantName} must exist");

        var (key, secret) = context.CredentialsByTenant[tenantName];

        var tokenUrl = await ResolveDmsTokenUrlAsync(tenantName);

        context.DmsToken = await TokenHelper.GetReusableDmsTokenAsync(tokenUrl, key, secret);

        // Set current tenant for the DMS client
        context.CurrentTenant = tenantName;
        context.DmsClient?.Dispose();
        context.DmsClient = new DmsApiClient(TestConfiguration.DmsApiUrl, context.DmsToken, tenantName);

        Console.WriteLine($"Authenticated to DMS with credentials for tenant: {tenantName}");
    }

    [When("a POST request is made to instance {string} and resource {string} with body:")]
    public async Task WhenAPostRequestIsMadeToInstanceAndResourceWithBody(
        string instanceRoute,
        string resource,
        string jsonBody
    )
    {
        context.DmsClient.Should().NotBeNull("Must be authenticated to DMS first");

        var parts = instanceRoute.Split('/');
        parts.Should().HaveCount(2, "Instance route must be in format districtId/schoolYear");

        var districtId = parts[0];
        var schoolYear = parts[1];

        Console.WriteLine($"POST to instance route: {instanceRoute}, resource: {resource}");
        Console.WriteLine($"Request body: {jsonBody}");

        // Parse JSON body to JsonElement which preserves the structure
        var body = JsonSerializer.Deserialize<JsonElement>(jsonBody);

        context.LastResponse = await context.DmsClient!.PostResourceAsync(
            districtId,
            schoolYear,
            resource,
            body
        );

        Console.WriteLine(
            $"Response: {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
        );
        if (!context.LastResponse.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Response body: {responseBody}");
        }
    }

    [Then("it should respond with {int}")]
    public void ThenItShouldRespondWith(int expectedStatusCode)
    {
        context.LastResponse.Should().NotBeNull();
        ((int)context.LastResponse!.StatusCode).Should().Be(expectedStatusCode);
    }

    [Then("it should respond with success")]
    public async Task ThenItShouldRespondWithSuccess()
    {
        context.LastResponse.Should().NotBeNull();

        if (!context.LastResponse!.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine(
                $"Expected success status but got {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
            );
            Console.WriteLine($"Response body: {responseBody}");
            Console.WriteLine($"Request URL: {context.LastResponse.RequestMessage?.RequestUri}");
        }

        context
            .LastResponse!.IsSuccessStatusCode.Should()
            .BeTrue(
                $"Expected success status but got {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
            );
    }

    [Then("the location should be stored as {string}")]
    public void ThenTheLocationShouldBeStoredAs(string key)
    {
        context.LastResponse.Should().NotBeNull();
        var location = context.LastResponse!.Headers.Location?.ToString();
        location.Should().NotBeNullOrEmpty();

        context.DescriptorLocations[key] = location!;
    }

    [When("a GET request is made to instance {string} and resource {string}")]
    public async Task WhenAGetRequestIsMadeToInstanceAndResource(string instanceRoute, string resource)
    {
        context.DmsClient.Should().NotBeNull("Must be authenticated to DMS first");

        var parts = instanceRoute.Split('/');
        parts.Should().HaveCount(2, "Instance route must be in format districtId/schoolYear");

        var districtId = parts[0];
        var schoolYear = parts[1];

        Console.WriteLine($"GET from instance route: {instanceRoute}, resource: {resource}");

        context.LastResponse = await context.DmsClient!.GetResourceAsync(districtId, schoolYear, resource);

        Console.WriteLine(
            $"Response: {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
        );
        if (!context.LastResponse.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Response body: {responseBody}");
        }
    }

    [When("a POST request is made to tenant {string} instance {string} resource {string} with body:")]
    public async Task WhenAPostRequestIsMadeToTenantInstanceResourceWithBody(
        string tenantName,
        string instanceRoute,
        string resource,
        string jsonBody
    )
    {
        context.DmsToken.Should().NotBeNullOrEmpty("Must be authenticated to DMS first");

        var parts = instanceRoute.Split('/');
        parts.Should().HaveCount(2, "Instance route must be in format districtId/schoolYear");

        var districtId = parts[0];
        var schoolYear = parts[1];

        Console.WriteLine($"POST to tenant: {tenantName}, instance: {instanceRoute}, resource: {resource}");
        Console.WriteLine($"Request body: {jsonBody}");

        // Create a client specifically for this tenant URL
        using var tenantClient = new DmsApiClient(TestConfiguration.DmsApiUrl, context.DmsToken!, tenantName);

        var body = JsonSerializer.Deserialize<JsonElement>(jsonBody);
        context.LastResponse = await tenantClient.PostResourceAsync(districtId, schoolYear, resource, body);

        Console.WriteLine(
            $"Response: {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
        );
        if (!context.LastResponse.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Response body: {responseBody}");
        }
    }

    [When("a GET request is made to tenant {string} instance {string} resource {string}")]
    public async Task WhenAGetRequestIsMadeToTenantInstanceResource(
        string tenantName,
        string instanceRoute,
        string resource
    )
    {
        context.DmsToken.Should().NotBeNullOrEmpty("Must be authenticated to DMS first");

        var parts = instanceRoute.Split('/');
        parts.Should().HaveCount(2, "Instance route must be in format districtId/schoolYear");

        var districtId = parts[0];
        var schoolYear = parts[1];

        Console.WriteLine($"GET from tenant: {tenantName}, instance: {instanceRoute}, resource: {resource}");

        // Create a client specifically for this tenant URL
        using var tenantClient = new DmsApiClient(TestConfiguration.DmsApiUrl, context.DmsToken!, tenantName);

        context.LastResponse = await tenantClient.GetResourceAsync(districtId, schoolYear, resource);

        Console.WriteLine(
            $"Response: {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
        );
        if (!context.LastResponse.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Response body: {responseBody}");
        }
    }

    [Then("the response should contain {string}")]
    public async Task ThenTheResponseShouldContain(string expectedContent)
    {
        context.LastResponse.Should().NotBeNull();
        context.LastResponse!.IsSuccessStatusCode.Should().BeTrue();

        var content = await context.LastResponse.Content.ReadAsStringAsync();
        content.Should().Contain(expectedContent);
    }

    [Then("the response should not contain {string}")]
    public async Task ThenTheResponseShouldNotContain(string unexpectedContent)
    {
        context.LastResponse.Should().NotBeNull();
        context.LastResponse!.IsSuccessStatusCode.Should().BeTrue();

        var content = await context.LastResponse.Content.ReadAsStringAsync();
        content.Should().NotContain(unexpectedContent);
    }

    [Then("the response body should contain {string}")]
    public async Task ThenTheResponseBodyShouldContain(string expectedContent)
    {
        // Unlike "the response should contain", this step does not require a success status, so it can
        // check the body of an error response.
        context.LastResponse.Should().NotBeNull();

        var content = await context.LastResponse!.Content.ReadAsStringAsync();
        content.Should().Contain(expectedContent);
    }

    [When("I GET resource {string} by location")]
    public async Task WhenIGetResourceByLocation(string key)
    {
        context.DmsClient.Should().NotBeNull("Must be authenticated to DMS first");
        context.DescriptorLocations.Should().ContainKey(key);

        var location = context.DescriptorLocations[key];
        context.LastResponse = await context.DmsClient!.GetByLocationAsync(location);
    }

    [When("a GET request is made to discovery endpoint with route {string}")]
    public async Task WhenAGetRequestIsMadeToDiscoveryEndpointWithRoute(string route)
    {
        // Discovery endpoints are public and don't require authentication
        // Uses shared HttpClient internally to avoid connection exhaustion
        var discoveryClient = new DmsApiClient(TestConfiguration.DmsApiUrl, "");

        Console.WriteLine($"GET discovery endpoint with route: '{route}'");

        context.LastResponse = await discoveryClient.GetDiscoveryWithRouteAsync(route);

        Console.WriteLine(
            $"Response: {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
        );
        if (!context.LastResponse.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Response body: {responseBody}");
        }

        // Dispose the wrapper (but shared HttpClient remains)
        discoveryClient.Dispose();
    }

    [When("the oauth url from the discovery response is used to request a DMS token")]
    public async Task WhenTheOauthUrlFromTheDiscoveryResponseIsUsedToRequestADmsToken()
    {
        context.LastResponse.Should().NotBeNull("Discovery response must be available");

        var responseBody = await context.LastResponse!.Content.ReadAsStringAsync();
        using var responseDoc = JsonDocument.Parse(responseBody);
        var oauthUrl = responseDoc.RootElement.GetProperty("urls").GetProperty("oauth").GetString();

        oauthUrl.Should().NotBeNullOrWhiteSpace("Discovery must advertise an oauth url");
        context.ClientKey.Should().NotBeNullOrEmpty("Application must be created first");
        context.ClientSecret.Should().NotBeNullOrEmpty("Application must be created first");

        // Exercise the advertised endpoint even when another scenario has cached a token.
        context.DmsToken = await TokenHelper.GetDmsTokenAsync(
            oauthUrl!,
            context.ClientKey!,
            context.ClientSecret!
        );
    }

    [Then("the discovery metadata urls should resolve to usable documents")]
    public async Task ThenTheDiscoveryMetadataUrlsShouldResolveToUsableDocuments()
    {
        context.LastResponse.Should().NotBeNull("Discovery response must be available");

        var discoveryResponse = context.LastResponse!;
        try
        {
            var discoveryUri = discoveryResponse.RequestMessage?.RequestUri;
            discoveryUri.Should().NotBeNull("Discovery request URI must be available");

            var responseBody = await discoveryResponse.Content.ReadAsStringAsync();
            string dependenciesUrl;
            string specificationsUrl;
            string xsdUrl;
            using (var responseDoc = JsonDocument.Parse(responseBody))
            {
                var urls = responseDoc.RootElement.GetProperty("urls");
                dependenciesUrl = ReadAdvertisedUrl(urls, "dependencies");
                specificationsUrl = ReadAdvertisedUrl(urls, "openApiMetadata");
                xsdUrl = ReadAdvertisedUrl(urls, "xsdMetadata");
            }

            using var metadataClient = new DmsApiClient(TestConfiguration.DmsApiUrl, "");

            await VerifyDependenciesAsync(metadataClient, dependenciesUrl, discoveryUri!);
            await VerifyResourcesSpecificationAsync(metadataClient, specificationsUrl, discoveryUri!);
            await VerifyCoreXsdAsync(metadataClient, xsdUrl, discoveryUri!);
        }
        finally
        {
            discoveryResponse.Dispose();
            context.LastResponse = null;
        }
    }

    private static string ReadAdvertisedUrl(JsonElement urls, string key)
    {
        urls.TryGetProperty(key, out var property)
            .Should()
            .BeTrue($"Discovery response should include urls.{key}");
        property.ValueKind.Should().Be(JsonValueKind.String);

        var url = property.GetString();
        url.Should().NotBeNullOrWhiteSpace($"Discovery response should include a usable urls.{key}");
        return url!;
    }

    private static string RequireStringProperty(JsonElement element, string name, string what)
    {
        element.ValueKind.Should().Be(JsonValueKind.Object, $"{what} entries must be objects");
        element.TryGetProperty(name, out var property).Should().BeTrue($"{what} entries must have '{name}'");
        property.ValueKind.Should().Be(JsonValueKind.String, $"{what} '{name}' must be a string");
        var value = property.GetString();
        value.Should().NotBeNullOrWhiteSpace($"{what} '{name}' must not be blank");
        return value!;
    }

    private static async Task<JsonDocument> GetJsonDocumentAsync(DmsApiClient client, Uri uri, string what)
    {
        using var response = await client.GetByLocationAsync(uri.ToString());
        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{what} should resolve");
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json", $"{what} media type");
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body);
    }

    private static async Task VerifyDependenciesAsync(DmsApiClient client, string url, Uri discoveryUri)
    {
        var uri = DiscoveryUrlAssertions.AssertConcreteContextUrl(url, discoveryUri);
        using var document = await GetJsonDocumentAsync(client, uri, "dependencies");

        document.RootElement.ValueKind.Should().Be(JsonValueKind.Array, "dependencies must be an array");
        List<string> resources = [];
        foreach (var item in document.RootElement.EnumerateArray())
        {
            resources.Add(RequireStringProperty(item, "resource", "dependency"));
            item.TryGetProperty("order", out var order).Should().BeTrue("dependency must have 'order'");
            order.ValueKind.Should().Be(JsonValueKind.Number);
            order.TryGetInt32(out _).Should().BeTrue("dependency 'order' must be an integer");
            item.TryGetProperty("operations", out var operations).Should().BeTrue();
            operations.ValueKind.Should().Be(JsonValueKind.Array, "dependency 'operations' must be an array");
            foreach (var operation in operations.EnumerateArray())
            {
                operation.ValueKind.Should().Be(JsonValueKind.String);
            }
        }

        resources.Should().Contain("/ed-fi/students", "the core Students resource must be a dependency");
    }

    private static async Task VerifyResourcesSpecificationAsync(
        DmsApiClient client,
        string url,
        Uri discoveryUri
    )
    {
        var uri = DiscoveryUrlAssertions.AssertConcreteContextUrl(url, discoveryUri);
        string endpointUri;
        using (var listing = await GetJsonDocumentAsync(client, uri, "specifications"))
        {
            listing.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
            var resources = listing
                .RootElement.EnumerateArray()
                .Where(entry => RequireStringProperty(entry, "name", "specification") == "Resources")
                .ToList();
            resources.Should().ContainSingle("exactly one Resources specification is advertised");
            endpointUri = RequireStringProperty(resources[0], "endpointUri", "specification");
        }

        var specUri = DiscoveryUrlAssertions.AssertConcreteContextUrl(endpointUri, discoveryUri);
        using var spec = await GetJsonDocumentAsync(client, specUri, "Resources specification");

        spec.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        spec.RootElement.TryGetProperty("openapi", out var version).Should().BeTrue();
        version.ValueKind.Should().Be(JsonValueKind.String);
        Regex
            .IsMatch(version.GetString()!, @"^3\.\d+\.\d+$")
            .Should()
            .BeTrue("the Resources document must declare an OpenAPI 3.x version");
        spec.RootElement.TryGetProperty("paths", out var paths).Should().BeTrue();
        paths.ValueKind.Should().Be(JsonValueKind.Object);
        paths.TryGetProperty("/ed-fi/students", out _).Should().BeTrue("the core Students path must exist");
    }

    private static async Task VerifyCoreXsdAsync(DmsApiClient client, string url, Uri discoveryUri)
    {
        var uri = DiscoveryUrlAssertions.AssertConcreteContextUrl(url, discoveryUri);
        string filesUrl;
        using (var sections = await GetJsonDocumentAsync(client, uri, "XSD sections"))
        {
            sections.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
            var core = sections
                .RootElement.EnumerateArray()
                .Where(entry => RequireStringProperty(entry, "name", "XSD section") == "ed-fi")
                .ToList();
            core.Should().ContainSingle("exactly one ed-fi XSD section is advertised");
            filesUrl = RequireStringProperty(core[0], "files", "XSD section");
        }

        var filesUri = DiscoveryUrlAssertions.AssertConcreteContextUrl(filesUrl, discoveryUri);
        string fileUrl;
        using (var files = await GetJsonDocumentAsync(client, filesUri, "XSD file list"))
        {
            files.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
            List<string> coreFiles = [];
            foreach (var entry in files.RootElement.EnumerateArray())
            {
                entry.ValueKind.Should().Be(JsonValueKind.String);
                var candidate = entry.GetString()!;
                if (
                    Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri)
                    && candidateUri.Segments[^1] == "Ed-Fi-Core.xsd"
                )
                {
                    coreFiles.Add(candidate);
                }
            }

            coreFiles.Should().ContainSingle("exactly one Ed-Fi-Core.xsd file is advertised");
            fileUrl = coreFiles[0];
        }

        var fileUri = DiscoveryUrlAssertions.AssertConcreteContextUrl(fileUrl, discoveryUri);
        using var xsdResponse = await client.GetByLocationAsync(fileUri.ToString());
        xsdResponse.StatusCode.Should().Be(HttpStatusCode.OK, "the advertised core XSD should download");
        xsdResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/xml");
        var xml = await xsdResponse.Content.ReadAsStringAsync();
        XDocument.Parse(xml).Root!.Name.Should().Be(XName.Get("schema", "http://www.w3.org/2001/XMLSchema"));
    }

    [When("a GET request is made to metadata path {string}")]
    public async Task WhenAGetRequestIsMadeToMetadataPath(string metadataPath)
    {
        using var metadataClient = new DmsApiClient(TestConfiguration.DmsApiUrl, "");

        Console.WriteLine($"GET metadata path: '{metadataPath}'");

        context.LastResponse = await metadataClient.GetDiscoveryWithRouteAsync(metadataPath.TrimStart('/'));

        Console.WriteLine(
            $"Response: {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
        );
        if (!context.LastResponse.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Response body: {responseBody}");
        }
    }

    private async Task<string> ResolveDmsTokenUrlAsync(string? tenantName)
    {
        var route = tenantName is null
            ? null
            : context
                .RouteQualifierToDataStoreId.Where(route =>
                    context.DataStoreIdToTenant.TryGetValue(route.Value, out var owner) && owner == tenantName
                )
                .Select(route => route.Key)
                .FirstOrDefault();

        if (route is not null)
        {
            using var discoveryClient = new DmsApiClient(TestConfiguration.DmsApiUrl, "");
            using var discoveryResponse = await discoveryClient.GetDiscoveryWithRouteAsync(
                $"{tenantName}/{route}"
            );

            if (discoveryResponse.IsSuccessStatusCode)
            {
                var responseBody = await discoveryResponse.Content.ReadAsStringAsync();
                if (!string.IsNullOrWhiteSpace(responseBody))
                {
                    try
                    {
                        using var responseDoc = JsonDocument.Parse(responseBody);
                        var oauthUrl = ExtractOauthUrl(responseDoc.RootElement);
                        if (!string.IsNullOrWhiteSpace(oauthUrl))
                        {
                            return oauthUrl;
                        }
                    }
                    catch (JsonException)
                    {
                        // Fall back to the direct token endpoint when discovery is unavailable.
                    }
                }
            }
        }

        return "http://localhost:8081/connect/token/";
    }

    internal static string? ExtractOauthUrl(JsonElement discoveryDocument)
    {
        if (
            discoveryDocument.ValueKind is not JsonValueKind.Object
            || !discoveryDocument.TryGetProperty("urls", out var urls)
            || urls.ValueKind is not JsonValueKind.Object
            || !urls.TryGetProperty("oauth", out var oauth)
            || oauth.ValueKind is not JsonValueKind.String
        )
        {
            return null;
        }

        return oauth.GetString();
    }

    [Then("the DMS token should be available")]
    public void ThenTheDmsTokenShouldBeAvailable()
    {
        context.DmsToken.Should().NotBeNullOrWhiteSpace();
    }

    [Then("the urls should be")]
    public async Task ThenTheUrlsShouldBe(string expectedJson)
    {
        context.LastResponse.Should().NotBeNull();
        context.LastResponse!.IsSuccessStatusCode.Should().BeTrue();

        var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
        Console.WriteLine($"Response body: {responseBody}");

        var responseDoc = JsonDocument.Parse(responseBody);
        var actualUrls = responseDoc.RootElement.GetProperty("urls");

        var expectedDoc = JsonDocument.Parse(expectedJson);
        var expectedUrls = expectedDoc.RootElement;

        // Compare each URL property
        foreach (var expectedProperty in expectedUrls.EnumerateObject())
        {
            var propertyName = expectedProperty.Name;
            var expectedValue = expectedProperty.Value.GetString();

            actualUrls
                .TryGetProperty(propertyName, out var actualProperty)
                .Should()
                .BeTrue($"Response should contain URL property '{propertyName}'");

            var actualValue = actualProperty.GetString();
            actualValue
                .Should()
                .Be(expectedValue, $"URL property '{propertyName}' should match expected value");
        }

        // Ensure no extra properties in actual response
        var actualPropertyCount = actualUrls.EnumerateObject().Count();
        var expectedPropertyCount = expectedUrls.EnumerateObject().Count();
        actualPropertyCount
            .Should()
            .Be(expectedPropertyCount, "Response should not contain extra URL properties");
    }

    [When("a GET request is made to XSD metadata endpoint with tenant {string}")]
    public async Task WhenAGetRequestIsMadeToXsdMetadataEndpointWithTenant(string tenantName)
    {
        // XSD metadata endpoints are public and don't require authentication
        using var xsdClient = new DmsApiClient(TestConfiguration.DmsApiUrl, "");

        Console.WriteLine($"GET XSD metadata endpoint with tenant: '{tenantName}'");

        context.LastResponse = await xsdClient.GetXsdMetadataWithTenantAsync(tenantName);

        Console.WriteLine(
            $"Response: {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
        );
        if (!context.LastResponse.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Response body: {responseBody}");
        }
    }

    [When("a GET request is made to XSD file {string} in section {string} with tenant {string}")]
    public async Task WhenAGetRequestIsMadeToXsdFileInSectionWithTenant(
        string fileName,
        string section,
        string tenantName
    )
    {
        // XSD file endpoints are public and don't require authentication
        using var xsdClient = new DmsApiClient(TestConfiguration.DmsApiUrl, "");

        Console.WriteLine($"GET XSD file '{fileName}' in section '{section}' with tenant: '{tenantName}'");

        context.LastResponse = await xsdClient.GetXsdFileWithTenantAsync(tenantName, section, fileName);

        Console.WriteLine(
            $"Response: {(int)context.LastResponse.StatusCode} ({context.LastResponse.StatusCode})"
        );
        if (!context.LastResponse.IsSuccessStatusCode)
        {
            var responseBody = await context.LastResponse.Content.ReadAsStringAsync();
            Console.WriteLine($"Response body: {responseBody}");
        }
    }

    [Then("the response content type should be {string}")]
    public void ThenTheResponseContentTypeShouldBe(string expectedMediaType)
    {
        context.LastResponse.Should().NotBeNull();
        context.LastResponse!.Content.Headers.ContentType?.MediaType.Should().Be(expectedMediaType);
    }
}
