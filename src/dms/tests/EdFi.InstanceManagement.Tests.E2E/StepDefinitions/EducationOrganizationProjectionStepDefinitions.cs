// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EdFi.InstanceManagement.Tests.E2E.Management;
using EdFi.InstanceManagement.Tests.E2E.Models;
using FluentAssertions;
using FluentAssertions.Execution;
using Reqnroll;

namespace EdFi.InstanceManagement.Tests.E2E.StepDefinitions;

/// <summary>
/// The education organization projection through a real Configuration Service and DMS: credentials and
/// claim sets provisioned through CMS endpoints, a hierarchy written through the resource API, and every
/// projection request addressed through the URL templates Discovery advertises, as the Configuration
/// Service reader addresses them.
/// </summary>
/// <remarks>
/// Writing the hierarchy is setup: a failed write fails the scenario with a setup message before any
/// projection assertion. Each scenario deletes what it wrote, newest first, and the claim sets it
/// imported, after the shared cleanup hook has deleted its applications.
/// </remarks>
[Binding]
public partial class EducationOrganizationProjectionStepDefinitions(InstanceManagementContext context)
{
    private const string ContractVersion = "educationOrganizationProjection.v1";
    private const string Namespace = "uri://ed-fi.org";
    private const string SeededDescriptorCode = "ProjectionE2E";
    private const int MaximumWalkedPages = 10;

    /// <summary>Text every seeded name carries, which no denial body may contain.</summary>
    private static readonly string[] _seededNameMarkers = ["Projection E2E", "Proyección"];

    private static readonly Dictionary<string, string> _serviceClaimNames = new(StringComparer.Ordinal)
    {
        ["educationOrganizationProjection"] =
            "http://ed-fi.org/identity/claims/services/educationOrganizationProjection",
        ["identity"] = "http://ed-fi.org/identity/claims/services/identity",
    };

    private readonly Dictionary<string, ProjectionCredential> _credentials = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private readonly List<(string Tenant, int Id)> _importedClaimSets = [];
    private readonly List<WrittenDocument> _writtenDocuments = [];
    private readonly List<List<ProjectionItem>> _walkedPages = [];
    private string? _configToken;
    private JsonElement? _discovery;

    [Given(
        "the {string} credential is an application in tenant {string} with claim set {string} and the tenant's data stores"
    )]
    public async Task GivenTheCredentialIsAnApplicationWithClaimSetAndTheTenantsDataStores(
        string credential,
        string tenant,
        string claimSetName
    )
    {
        int[] dataStoreIds =
        [
            .. context.DataStoreIdToTenant.Where(entry => entry.Value == tenant).Select(entry => entry.Key),
        ];
        dataStoreIds.Should().NotBeEmpty($"tenant {tenant} must be a hydrated fixture tenant");

        await CreateApplicationAsync(credential, tenant, claimSetName, dataStoreIds);
    }

    [Given(
        "the {string} credential is an application in tenant {string} whose claim set {string} grants only Read on the {string} service claim"
    )]
    public async Task GivenTheCredentialIsAnApplicationWhoseClaimSetGrantsOnlyRead(
        string credential,
        string tenant,
        string claimSetName,
        string resourceName
    )
    {
        _serviceClaimNames.Should().ContainKey(resourceName, "the feature names a known service claim");
        string claimName = _serviceClaimNames[resourceName];
        ConfigServiceClient client = TenantConfigClient(tenant);

        int claimSetId = await client.ImportServiceClaimSetAsync(
            claimSetName,
            resourceName,
            claimName,
            "Read"
        );
        _importedClaimSets.Add((tenant, claimSetId));

        // CMS skips a resource claim missing from its stored hierarchy instead of rejecting the import,
        // so the grant is read back: without it a later 403 would be a provisioning failure, not a denial.
        GrantedActions(await client.ExportClaimSetAsync(claimSetId))
            .Should()
            .BeEquivalentTo(
                new Dictionary<string, string[]> { [claimName] = ["Read"] },
                $"claim set {claimSetName} must grant exactly Read on {claimName} and nothing else"
            );

        await CreateApplicationAsync(credential, tenant, claimSetName, dataStoreIds: []);
    }

    [Given(
        "these education organizations are written through the resource API at tenant {string} instance {string} with the {string} credential:"
    )]
    public async Task GivenTheseEducationOrganizationsAreWritten(
        string tenant,
        string instance,
        string credential,
        Table table
    )
    {
        (string districtId, string schoolYear) = SplitInstance(instance);
        string token = await TokenForAsync(credential);
        using DmsApiClient client = new(TestConfiguration.DmsApiUrl, token, tenant);

        foreach (
            (string resource, string descriptor) in new[]
            {
                ("educationOrganizationCategoryDescriptors", "EducationOrganizationCategoryDescriptor"),
                ("gradeLevelDescriptors", "GradeLevelDescriptor"),
                ("localEducationAgencyCategoryDescriptors", "LocalEducationAgencyCategoryDescriptor"),
            }
        )
        {
            JsonObject body = new()
            {
                ["namespace"] = $"{Namespace}/{descriptor}",
                ["codeValue"] = SeededDescriptorCode,
                ["shortDescription"] = SeededDescriptorCode,
            };
            await WriteSetupDocumentAsync(
                client,
                token,
                tenant,
                districtId,
                schoolYear,
                resource,
                descriptor,
                body
            );
        }

        foreach (DataTableRow row in table.Rows)
        {
            string type = row["type"];
            long id = long.Parse(row["id"], CultureInfo.InvariantCulture);
            (string resource, JsonObject body) = EducationOrganizationBody(type, id, row);
            await WriteSetupDocumentAsync(
                client,
                token,
                tenant,
                districtId,
                schoolYear,
                resource,
                $"{type} {id}",
                body
            );
        }
    }

    [When("the Discovery document of tenant {string} is read")]
    public async Task WhenTheDiscoveryDocumentOfTenantIsRead(string tenant)
    {
        using JsonDocument discovery = await ReadDiscoveryAsync(tenant);
        _discovery = discovery.RootElement.Clone();
    }

    [Then("the Discovery document advertises:")]
    public void ThenTheDiscoveryDocumentAdvertises(Table table)
    {
        _discovery.Should().NotBeNull("the Discovery document must be read first");

        using var scope = new AssertionScope();
        foreach (DataTableRow row in table.Rows)
        {
            string member = row["member"];
            JsonElement element = _discovery!.Value;
            bool found = true;
            foreach (string name in member.Split('.'))
            {
                found =
                    element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out element);
                if (!found)
                {
                    break;
                }
            }

            found.Should().BeTrue($"Discovery must advertise '{member}'");
            if (!found)
            {
                continue;
            }

            string expected = row["value"];
            if (expected.StartsWith('['))
            {
                JsonNode
                    .DeepEquals(JsonNode.Parse(element.GetRawText()), JsonNode.Parse(expected))
                    .Should()
                    .BeTrue($"'{member}' should be {expected} but was {element.GetRawText()}");
            }
            else
            {
                element.GetString().Should().Be(expected, $"'{member}' is advertised exactly");
            }
        }
    }

    [When(
        "the {string} credential walks the projection at tenant {string} instance {string} for the data store of instance {string} with limit {int}"
    )]
    public async Task WhenTheCredentialWalksTheProjection(
        string credential,
        string tenant,
        string instance,
        string dataStoreInstance,
        int limit
    )
    {
        string projectionUrl = await ProjectionUrlAsync(tenant, instance);
        int dataStoreId = DataStoreIdOf(dataStoreInstance);
        using DmsApiClient client = new(TestConfiguration.DmsApiUrl, await TokenForAsync(credential));

        _walkedPages.Clear();
        HashSet<string> seenCursors = new(StringComparer.Ordinal);
        string? cursor = null;

        do
        {
            string query =
                $"dataStoreId={dataStoreId}&limit={limit}&contractVersion={Uri.EscapeDataString(ContractVersion)}";
            if (cursor is not null)
            {
                query += $"&cursor={Uri.EscapeDataString(cursor)}";
            }

            using HttpResponseMessage response = await client.GetEducationOrganizationProjectionAsync(
                projectionUrl,
                query
            );
            string body = await response.Content.ReadAsStringAsync();
            int pageNumber = _walkedPages.Count + 1;

            response.StatusCode.Should().Be(HttpStatusCode.OK, $"page {pageNumber} answered {body}");
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
            CacheControlOf(response).Should().Be("no-store", $"page {pageNumber} must not be cached");

            (List<ProjectionItem> items, cursor) = ReadPage(body, dataStoreId, pageNumber);
            _walkedPages.Add(items);

            if (cursor is not null)
            {
                items.Should().HaveCount(limit, "a page with a continuation holds exactly limit items");
                seenCursors.Add(cursor).Should().BeTrue("a walk never hands back a cursor twice");
            }
        } while (cursor is not null && _walkedPages.Count < MaximumWalkedPages);

        cursor.Should().BeNull($"the walk must reach the end of the set within {MaximumWalkedPages} pages");
    }

    [Then("the walk returned these pages:")]
    public void ThenTheWalkReturnedThesePages(Table table)
    {
        List<List<ProjectionItem>> expected = [];
        foreach (DataTableRow row in table.Rows)
        {
            int page = int.Parse(row["page"], CultureInfo.InvariantCulture);
            while (expected.Count < page)
            {
                expected.Add([]);
            }

            expected[page - 1]
                .Add(
                    new ProjectionItem(
                        long.Parse(row["educationOrganizationId"], CultureInfo.InvariantCulture),
                        row["nameOfInstitution"],
                        NullLiteral(row["shortNameOfInstitution"]),
                        row["discriminator"],
                        NullLiteral(row["parentId"]) is { } parent
                            ? long.Parse(parent, CultureInfo.InvariantCulture)
                            : null
                    )
                );
        }

        _walkedPages.Should().HaveCount(expected.Count, "the walk returns exactly the expected pages");
        for (int page = 0; page < expected.Count; page++)
        {
            _walkedPages[page].Should().Equal(expected[page], $"page {page + 1} holds exactly these items");
        }
    }

    [Then("the walk returned one empty page")]
    public void ThenTheWalkReturnedOneEmptyPage()
    {
        _walkedPages.Should().ContainSingle("an empty set is one page with no continuation");
        _walkedPages[0].Should().BeEmpty("the store holds no education organization");
    }

    [Then("projection requests respond as follows:")]
    public async Task ThenProjectionRequestsRespondAsFollows(Table table)
    {
        using var scope = new AssertionScope();
        foreach (DataTableRow row in table.Rows)
        {
            string credential = row["credential"];
            string token = credential switch
            {
                "no credential" => "",
                "an unknown token" => "not-a-token",
                _ => await TokenForAsync(credential),
            };
            string projectionUrl = await ProjectionUrlAsync(row["tenant"], row["instance"]);
            string query = RoutePlaceholder()
                .Replace(
                    row["query"],
                    match => DataStoreIdOf(match.Groups["route"].Value).ToString(CultureInfo.InvariantCulture)
                );
            string because = $"{credential} at {row["tenant"]}/{row["instance"]} with '{query}'";

            using DmsApiClient client = new(TestConfiguration.DmsApiUrl, token);
            using HttpResponseMessage response = await client.GetEducationOrganizationProjectionAsync(
                projectionUrl,
                query
            );
            string body = await response.Content.ReadAsStringAsync();

            ((int)response.StatusCode)
                .Should()
                .Be(int.Parse(row["status"], CultureInfo.InvariantCulture), because + $" answered {body}");
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json", because);
            CacheControlOf(response).Should().Be("no-store", because);
            ProblemTypeOf(body).Should().Be(row["type"], because);
            foreach (string marker in _seededNameMarkers)
            {
                body.Should().NotContain(marker, because + " must not expose any education organization");
            }
        }
    }

    [When("the {string} credential sends GET to resource {string} at tenant {string} instance {string}")]
    public async Task WhenTheCredentialSendsGetToResource(
        string credential,
        string resource,
        string tenant,
        string instance
    ) => await SendGetAsync(credential, resource, tenant, instance, query: null);

    [When(
        "the {string} credential sends GET to resource {string} at tenant {string} instance {string} with query {string}"
    )]
    public async Task WhenTheCredentialSendsGetToResourceWithQuery(
        string credential,
        string resource,
        string tenant,
        string instance,
        string query
    ) => await SendGetAsync(credential, resource, tenant, instance, query);

    [When(
        "the {string} credential sends POST to resource {string} at tenant {string} instance {string} with body:"
    )]
    public async Task WhenTheCredentialSendsPostToResource(
        string credential,
        string resource,
        string tenant,
        string instance,
        string jsonBody
    )
    {
        (string districtId, string schoolYear) = SplitInstance(instance);
        using DmsApiClient client = new(TestConfiguration.DmsApiUrl, await TokenForAsync(credential), tenant);

        context.LastResponse = await client.PostResourceAsync(
            districtId,
            schoolYear,
            resource,
            JsonSerializer.Deserialize<JsonElement>(jsonBody)
        );
    }

    [Then("the response should be an empty array")]
    public async Task ThenTheResponseShouldBeAnEmptyArray()
    {
        context.LastResponse.Should().NotBeNull();
        string body = await context.LastResponse!.Content.ReadAsStringAsync();

        JsonNode.Parse(body).Should().BeOfType<JsonArray>().Which.Should().BeEmpty(body);
    }

    /// <summary>
    /// Deletes the documents this scenario wrote, newest first, and the claim sets it imported. Runs after
    /// <see cref="Hooks.InstanceManagementCleanupHooks.CleanupInstanceResources"/> (Order 1000) has deleted
    /// the scenario's applications, using a CMS token captured before that hook reset the context.
    /// </summary>
    /// <remarks>
    /// A document left behind would change the next scenario's exact walk, so a failed deletion fails the
    /// scenario here instead of surfacing later as a projection mismatch.
    /// </remarks>
    [AfterScenario("@EducationOrganizationProjection", Order = 1100)]
    public async Task DeleteScenarioOwnedRecordsAsync()
    {
        List<string> failures = [];

        foreach (WrittenDocument document in Enumerable.Reverse(_writtenDocuments))
        {
            try
            {
                using DmsApiClient client = new(TestConfiguration.DmsApiUrl, document.Token);
                using HttpResponseMessage response = await client.DeleteByLocationAsync(document.Location);
                if (response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.NotFound))
                {
                    failures.Add($"{document.Description} answered {(int)response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{document.Description} threw {ex.GetType().Name}");
            }
        }

        foreach ((string tenant, int claimSetId) in _importedClaimSets)
        {
            try
            {
                await new ConfigServiceClient(
                    TestConfiguration.ConfigServiceUrl,
                    _configToken!,
                    tenant
                ).DeleteClaimSetAsync(claimSetId);
            }
            catch (Exception ex)
            {
                failures.Add($"claim set {claimSetId} in {tenant} threw {ex.GetType().Name}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Projection E2E cleanup failed; later projection scenarios may see these records: "
                    + string.Join("; ", failures)
            );
        }
    }

    /// <summary>
    /// Creates a scenario-owned application under the tenant's fixture vendor, with no education
    /// organizations, and records its first client's credentials.
    /// </summary>
    private async Task CreateApplicationAsync(
        string credential,
        string tenant,
        string claimSetName,
        int[] dataStoreIds
    )
    {
        context
            .VendorIdsByTenant.Should()
            .ContainKey(tenant, $"a vendor for tenant {tenant} must be hydrated first");
        ConfigServiceClient client = TenantConfigClient(tenant);

        ApplicationResponse application = await client.CreateApplicationAsync(
            new ApplicationRequest(
                VendorId: context.VendorIdsByTenant[tenant],
                ApplicationName: $"Projection E2E {credential}",
                ClaimSetName: claimSetName,
                EducationOrganizationIds: [],
                DataStoreIds: dataStoreIds
            )
        );
        context.MarkApplicationScenarioOwned(tenant, application.Id);

        ApiClientResponse apiClient = await client.GetApiClientAsync(application.Key);
        apiClient
            .DataStoreIds.Should()
            .BeEquivalentTo(
                dataStoreIds,
                $"the {credential} credential is assigned exactly these data stores"
            );

        _credentials[credential] = new ProjectionCredential(tenant, application.Key, application.Secret);
    }

    private async Task SendGetAsync(
        string credential,
        string resource,
        string tenant,
        string instance,
        string? query
    )
    {
        (string districtId, string schoolYear) = SplitInstance(instance);
        using DmsApiClient client = new(TestConfiguration.DmsApiUrl, await TokenForAsync(credential), tenant);

        context.LastResponse = await client.GetResourceAsync(districtId, schoolYear, resource, query);
    }

    private async Task WriteSetupDocumentAsync(
        DmsApiClient client,
        string token,
        string tenant,
        string districtId,
        string schoolYear,
        string resource,
        string description,
        JsonObject body
    )
    {
        HttpResponseMessage response = await client.PostResourceAsync(districtId, schoolYear, resource, body);
        string responseBody = await response.Content.ReadAsStringAsync();
        bool isDescriptor = resource.EndsWith("Descriptors", StringComparison.Ordinal);

        if (response.StatusCode == HttpStatusCode.Created)
        {
            string location =
                response.Headers.Location?.ToString()
                ?? throw new InvalidOperationException(
                    $"Projection E2E setup failed: writing {description} returned no Location."
                );
            _writtenDocuments.Add(new WrittenDocument(description, location, token));
            return;
        }

        // A descriptor that already exists is not this scenario's, so it is used and left in place. An
        // education organization that already exists means the store was not clean, which would make the
        // exact walk meaningless.
        if (isDescriptor && response.StatusCode == HttpStatusCode.OK)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Projection E2E setup failed: writing {description} to {tenant}/{districtId}/{schoolYear} "
                + $"answered {(int)response.StatusCode}, expected 201: {responseBody}"
        );
    }

    /// <summary>
    /// Mints (once per scenario) a token for the credential through the token URL its own tenant's
    /// Discovery document advertises, the same template the Configuration Service reader resolves.
    /// </summary>
    private async Task<string> TokenForAsync(string credential)
    {
        if (_tokens.TryGetValue(credential, out string? token))
        {
            return token;
        }

        _credentials
            .Should()
            .ContainKey(credential, $"the {credential} credential must be provisioned first");
        ProjectionCredential owner = _credentials[credential];
        string instance = context
            .RouteQualifierToDataStoreId.Where(route =>
                context.DataStoreIdToTenant.TryGetValue(route.Value, out string? tenant)
                && tenant == owner.Tenant
            )
            .Select(route => route.Key)
            .Order(StringComparer.Ordinal)
            .First();

        string tokenUrl = await ResolveTemplateAsync(owner.Tenant, instance, "oauth");
        token = await TokenHelper.GetDmsTokenAsync(tokenUrl, owner.Key, owner.Secret);
        _tokens[credential] = token;
        return token;
    }

    private static Task<string> ProjectionUrlAsync(string tenant, string instance) =>
        ResolveTemplateAsync(tenant, instance, "educationOrganizationProjection");

    /// <summary>
    /// Reads the tenant's Discovery document and fills the route qualifier placeholders of one advertised
    /// URL template with the instance's values.
    /// </summary>
    private static async Task<string> ResolveTemplateAsync(string tenant, string instance, string urlMember)
    {
        (string districtId, string schoolYear) = SplitInstance(instance);
        using JsonDocument discovery = await ReadDiscoveryAsync(tenant);

        discovery
            .RootElement.GetProperty("urls")
            .TryGetProperty(urlMember, out JsonElement template)
            .Should()
            .BeTrue($"Discovery for {tenant} must advertise urls.{urlMember}");

        string url = template
            .GetString()!
            .Replace("{districtId}", Uri.EscapeDataString(districtId), StringComparison.Ordinal)
            .Replace("{schoolYear}", Uri.EscapeDataString(schoolYear), StringComparison.Ordinal);
        url.Should().NotContain("{", $"every placeholder of urls.{urlMember} is a route qualifier");
        return url;
    }

    private static async Task<JsonDocument> ReadDiscoveryAsync(string tenant)
    {
        using DmsApiClient client = new(TestConfiguration.DmsApiUrl, "");
        using HttpResponseMessage response = await client.GetDiscoveryWithRouteAsync(tenant);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"Discovery for {tenant} answered {body}");
        return JsonDocument.Parse(body);
    }

    private int DataStoreIdOf(string instance)
    {
        context
            .RouteQualifierToDataStoreId.Should()
            .ContainKey(instance, $"{instance} must be a hydrated fixture route");
        return context.RouteQualifierToDataStoreId[instance];
    }

    private ConfigServiceClient TenantConfigClient(string tenant)
    {
        context
            .ConfigToken.Should()
            .NotBeNullOrEmpty("the Configuration Service system admin token must be established first");
        _configToken = context.ConfigToken;

        if (!context.ConfigClientsByTenant.TryGetValue(tenant, out ConfigServiceClient? client))
        {
            client = new ConfigServiceClient(
                TestConfiguration.ConfigServiceUrl,
                context.ConfigToken!,
                tenant
            );
            context.ConfigClientsByTenant[tenant] = client;
        }

        return client;
    }

    /// <summary>
    /// Every resource claim the exported claim set grants, by claim name, with its enabled actions. The
    /// export lists each granted claim once, child claims included, with its full claim name.
    /// </summary>
    private static Dictionary<string, string[]> GrantedActions(string export)
    {
        using JsonDocument document = JsonDocument.Parse(export);

        return document
            .RootElement.GetProperty("resourceClaims")
            .EnumerateArray()
            .Select(claim =>
                (
                    Name: claim.GetProperty("claimName").GetString()!,
                    Actions: claim
                        .GetProperty("actions")
                        .EnumerateArray()
                        .Where(action => action.GetProperty("enabled").GetBoolean())
                        .Select(action => action.GetProperty("name").GetString()!)
                        .ToArray()
                )
            )
            .Where(claim => claim.Actions.Length > 0)
            .ToDictionary(claim => claim.Name, claim => claim.Actions, StringComparer.Ordinal);
    }

    private static (string Resource, JsonObject Body) EducationOrganizationBody(
        string type,
        long id,
        DataTableRow row
    )
    {
        JsonObject body = new() { ["nameOfInstitution"] = row["nameOfInstitution"] };
        if (row["shortNameOfInstitution"] is { Length: > 0 } shortName)
        {
            body["shortNameOfInstitution"] = shortName;
        }

        JsonArray categories = new(
            new JsonObject
            {
                ["educationOrganizationCategoryDescriptor"] =
                    $"{Namespace}/EducationOrganizationCategoryDescriptor#{SeededDescriptorCode}",
            }
        );

        string resource;
        switch (type)
        {
            case "StateEducationAgency":
                resource = "stateEducationAgencies";
                body["stateEducationAgencyId"] = id;
                body["categories"] = categories;
                break;
            case "EducationServiceCenter":
                resource = "educationServiceCenters";
                body["educationServiceCenterId"] = id;
                body["categories"] = categories;
                break;
            case "LocalEducationAgency":
                resource = "localEducationAgencies";
                body["localEducationAgencyId"] = id;
                body["localEducationAgencyCategoryDescriptor"] =
                    $"{Namespace}/LocalEducationAgencyCategoryDescriptor#{SeededDescriptorCode}";
                body["categories"] = categories;
                break;
            case "School":
                resource = "schools";
                body["schoolId"] = id;
                body["educationOrganizationCategories"] = categories;
                body["gradeLevels"] = new JsonArray(
                    new JsonObject
                    {
                        ["gradeLevelDescriptor"] = $"{Namespace}/GradeLevelDescriptor#{SeededDescriptorCode}",
                    }
                );
                break;
            default:
                throw new InvalidOperationException($"Unknown education organization type '{type}'.");
        }

        foreach (
            string reference in row["references"]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        )
        {
            string[] parts = reference.Split('=');
            long referencedId = long.Parse(parts[1], CultureInfo.InvariantCulture);
            (string member, string key) = parts[0] switch
            {
                "stateEducationAgency" => ("stateEducationAgencyReference", "stateEducationAgencyId"),
                "educationServiceCenter" => ("educationServiceCenterReference", "educationServiceCenterId"),
                "parentLocalEducationAgency" => (
                    "parentLocalEducationAgencyReference",
                    "localEducationAgencyId"
                ),
                "localEducationAgency" => ("localEducationAgencyReference", "localEducationAgencyId"),
                _ => throw new InvalidOperationException($"Unknown reference '{parts[0]}'."),
            };
            body[member] = new JsonObject { [key] = referencedId };
        }

        return (resource, body);
    }

    /// <summary>
    /// Reads one page strictly: exactly the four envelope members and five item members the contract
    /// pins, the echoed contract version and data store id, and the continuation.
    /// </summary>
    private static (List<ProjectionItem> Items, string? NextCursor) ReadPage(
        string body,
        int dataStoreId,
        int pageNumber
    )
    {
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        string because = $"page {pageNumber} was {body}";

        root.EnumerateObject()
            .Select(member => member.Name)
            .Should()
            .BeEquivalentTo(["contractVersion", "dataStoreId", "nextCursor", "items"], because);
        root.GetProperty("contractVersion").GetString().Should().Be(ContractVersion, because);
        root.GetProperty("dataStoreId").GetInt32().Should().Be(dataStoreId, because);

        List<ProjectionItem> items = [];
        foreach (JsonElement item in root.GetProperty("items").EnumerateArray())
        {
            item.EnumerateObject()
                .Select(member => member.Name)
                .Should()
                .BeEquivalentTo(
                    [
                        "educationOrganizationId",
                        "nameOfInstitution",
                        "shortNameOfInstitution",
                        "discriminator",
                        "parentId",
                    ],
                    because
                );
            JsonElement parent = item.GetProperty("parentId");
            items.Add(
                new ProjectionItem(
                    item.GetProperty("educationOrganizationId").GetInt64(),
                    item.GetProperty("nameOfInstitution").GetString()!,
                    item.GetProperty("shortNameOfInstitution").GetString(),
                    item.GetProperty("discriminator").GetString()!,
                    parent.ValueKind == JsonValueKind.Null ? null : parent.GetInt64()
                )
            );
        }

        return (items, root.GetProperty("nextCursor").GetString());
    }

    private static string? ProblemTypeOf(string body)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("type", out JsonElement type)
                ? type.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? CacheControlOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Cache-Control", out IEnumerable<string>? values)
            ? string.Join(", ", values)
            : null;

    private static string? NullLiteral(string value) => value == "null" ? null : value;

    private static (string DistrictId, string SchoolYear) SplitInstance(string instance)
    {
        string[] parts = instance.Split('/');
        parts.Should().HaveCount(2, "an instance is written as districtId/schoolYear");
        return (parts[0], parts[1]);
    }

    [GeneratedRegex("<(?<route>[0-9]+/[0-9]+)>")]
    private static partial Regex RoutePlaceholder();

    private sealed record ProjectionCredential(string Tenant, string Key, string Secret);

    private sealed record WrittenDocument(string Description, string Location, string Token);

    private sealed record ProjectionItem(
        long Id,
        string Name,
        string? ShortName,
        string Discriminator,
        long? ParentId
    );
}
