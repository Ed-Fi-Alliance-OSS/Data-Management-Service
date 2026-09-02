// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Security;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using EdFi.DataManagementService.Tests.Integration.Fixtures;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Public-boundary coverage for OwnershipBased authorization: the exact ProblemDetails wire contracts from
/// <c>auth.md</c> 2.13 and 2.14, stamping on create, the authorized round trip, the provider-independent token
/// cap, and the scopes that stay withheld with a 501. The provider matrix lives in the backend suites; this
/// scenario owns only what those cannot observe - the served response body and the real application-context
/// plumbing that carries the caller's ownership tokens.
/// </summary>
/// <remarks>
/// <para>
/// Token sets vary per tenant rather than per test class. The production scoped
/// <c>CachedApplicationContextProvider</c> stays wired and only the CMS-facing
/// <c>IConfigurationServiceApplicationProvider</c> underneath it is replaced, so one host can serve an owner, a
/// non-owner, a client with no creator token, and both sides of the token cap. That is what lets a single
/// request sequence seed a row as its owner and then be refused as somebody else.
/// </para>
/// <para>
/// Every tenant resolves to the same data store, so all of them see the same rows. That is the point:
/// ownership is a per-row check against a stored token, not a tenancy boundary.
/// </para>
/// </remarks>
internal static class OwnershipAuthorizationIntegrationScenario
{
    /// <summary>The token a create stamps on every tenant that carries one.</summary>
    public const short CreatorToken = 42;

    /// <summary>A token no seeded row was ever stamped with.</summary>
    public const short ForeignToken = 7;

    public const string OwnerTenant = "ownership-owner-tenant";
    public const string ForeignTenant = "ownership-foreign-tenant";
    public const string NoCreatorTokenTenant = "ownership-no-creator-token-tenant";
    public const string TokenCapTenant = "ownership-token-cap-tenant";
    public const string UnderTokenCapTenant = "ownership-under-token-cap-tenant";

    /// <summary>
    /// The defensive limit is stated as 2,000 or more fails closed, so the over-cap tenant holds exactly 2,000
    /// and the under-cap tenant exactly 1,999 - the two values the rule turns on.
    /// </summary>
    private const int OverCapTokenCount = 2000;

    private const int UnderCapTokenCount = 1999;

    private const string NullableResourcesEndpointFormat = "/{0}/data/authz/authorizationNullableResources";
    private const string GradeLevelDescriptorsEndpointFormat = "/{0}/data/ed-fi/gradeLevelDescriptors";

    private const string MismatchType =
        "urn:ed-fi:api:security:authorization:ownership:access-denied:ownership-mismatch";
    private const string StoredUninitializedType =
        "urn:ed-fi:api:security:authorization:ownership:invalid-data:ownership-uninitialized";
    private const string NotOwnedDetail =
        "Access to the requested data could not be authorized. The item is not owned by the caller.";

    private static readonly string[] _storedUninitializedErrors =
    [
        "The existing resource item has no 'CreatedByOwnershipTokenId' value assigned and thus will never be accessible to clients using the '"
            + AuthorizationStrategyNameConstants.OwnershipBased
            + "' authorization strategy.",
    ];

    /// <summary>
    /// <c>OwnershipBased</c> on every resource and action, which exercises the whole surface at once: a create
    /// is stamped and never denied, every single-record read and write is enforced, and GET-many and descriptor
    /// storage stay withheld. Seeding still works precisely because a create cannot be denied by ownership.
    /// </summary>
    public static IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new ConfigurableClaimSetProvider(
            fixture,
            static (_, _) => [AuthorizationStrategyNameConstants.OwnershipBased]
        );

    /// <summary>
    /// Resolves the simulated CMS application context for a tenant. Only the ownership fields differ; every
    /// tenant is the same client against the same data store.
    /// </summary>
    public static ApplicationContextResult Resolve(string clientId, string? tenant) =>
        tenant switch
        {
            OwnerTenant => Success(applicationId: 301, CreatorToken, [ForeignToken, CreatorToken]),
            ForeignTenant => Success(applicationId: 302, CreatorToken, [ForeignToken]),
            NoCreatorTokenTenant => Success(applicationId: 303, null, [ForeignToken]),
            TokenCapTenant => Success(applicationId: 304, CreatorToken, TokenRange(OverCapTokenCount)),
            UnderTokenCapTenant => Success(applicationId: 305, CreatorToken, TokenRange(UnderCapTokenCount)),
            _ => Success(applicationId: 300, CreatorToken, [CreatorToken]),
        };

    /// <summary>
    /// A create is stamped from the caller's creator token and is never denied by ownership, even for a caller
    /// holding no token that stamp would authorize. The no-creator-token client stamps NULL, which is the value
    /// 2.14 is about, and it too is created rather than refused.
    /// </summary>
    public static async Task It_stamps_the_creator_ownership_token_on_create_and_never_denies_it(
        ApiIntegrationHarness harness
    )
    {
        Guid stampedId = await CreateAsync(harness, OwnerTenant, 1001, "ownership-stamped-create");

        (await ReadStoredOwnershipTokenAsync(harness, stampedId)).Should().Be(CreatorToken);

        // Holds only the foreign token and carries no creator token, so nothing about this client could
        // authorize the row it is about to create. It still creates it.
        Guid unstampedId = await CreateAsync(harness, NoCreatorTokenTenant, 1002, "ownership-null-create");

        (await ReadStoredOwnershipTokenAsync(harness, unstampedId)).Should().BeNull();
    }

    /// <summary>
    /// An owner may read, update and delete, and the update leaves the stored token alone even though the
    /// request carries a creator token a create would have stamped.
    /// </summary>
    public static async Task It_authorizes_the_full_round_trip_for_a_holder_of_the_stored_token(
        ApiIntegrationHarness harness
    )
    {
        Guid documentId = await CreateAsync(harness, OwnerTenant, 1101, "ownership-round-trip");
        string resourcePath = ResourcePath(OwnerTenant, documentId);

        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(resourcePath);
        string getBody = await getResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.OK, getBody);

        using HttpResponseMessage putResponse = await PutAsync(
            harness,
            OwnerTenant,
            documentId,
            1101,
            "ownership-round-trip-updated"
        );
        string putBody = await putResponse.Content.ReadAsStringAsync();
        putResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, putBody);
        (await ReadStoredOwnershipTokenAsync(harness, documentId)).Should().Be(CreatorToken);

        using HttpResponseMessage deleteResponse = await harness.HttpClient.DeleteAsync(resourcePath);
        string deleteBody = await deleteResponse.Content.ReadAsStringAsync();
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent, deleteBody);
    }

    /// <summary>
    /// 2.13 on every enforced operation for a caller whose tokens do not include the stored one, including the
    /// POST that resolves to an upsert-as-update. The row, its token, and its readability by the owner are all
    /// unchanged afterwards, which is the assertion that a denial cannot write.
    /// </summary>
    public static async Task It_returns_ownership_mismatch_problem_details_for_reads_and_writes(
        ApiIntegrationHarness harness
    )
    {
        Guid documentId = await CreateAsync(harness, OwnerTenant, 1201, "ownership-mismatch");

        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(
            ResourcePath(ForeignTenant, documentId)
        );
        await AssertOwnershipDenialAsync(getResponse, MismatchType, []);

        using HttpResponseMessage putResponse = await PutAsync(
            harness,
            ForeignTenant,
            documentId,
            1201,
            "ownership-mismatch-put"
        );
        await AssertOwnershipDenialAsync(putResponse, MismatchType, []);

        // The same identity, so this POST resolves to an upsert-as-update against the stored row rather than
        // to a create - the branch a create's vacuous check must not be allowed to cover.
        using HttpResponseMessage postAsUpdateResponse = await PostAsync(
            harness,
            ForeignTenant,
            1201,
            "ownership-mismatch-post-as-update"
        );
        await AssertOwnershipDenialAsync(postAsUpdateResponse, MismatchType, []);

        using HttpResponseMessage deleteResponse = await harness.HttpClient.DeleteAsync(
            ResourcePath(ForeignTenant, documentId)
        );
        await AssertOwnershipDenialAsync(deleteResponse, MismatchType, []);

        (await ReadStoredOwnershipTokenAsync(harness, documentId)).Should().Be(CreatorToken);

        using HttpResponseMessage ownerGetResponse = await harness.HttpClient.GetAsync(
            ResourcePath(OwnerTenant, documentId)
        );
        string ownerGetBody = await ownerGetResponse.Content.ReadAsStringAsync();
        ownerGetResponse.StatusCode.Should().Be(HttpStatusCode.OK, ownerGetBody);
        ownerGetBody.Should().Contain("ownership-mismatch");
    }

    /// <summary>
    /// 2.14 on every enforced operation against a row created without a token: a distinct <c>type</c> and an
    /// <c>errors</c> entry naming the strategy, over the same shared detail sentence 2.13 uses.
    /// </summary>
    public static async Task It_returns_stored_uninitialized_problem_details_for_reads_and_writes(
        ApiIntegrationHarness harness
    )
    {
        Guid documentId = await CreateAsync(harness, NoCreatorTokenTenant, 1301, "ownership-uninitialized");

        (await ReadStoredOwnershipTokenAsync(harness, documentId)).Should().BeNull();

        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(
            ResourcePath(ForeignTenant, documentId)
        );
        await AssertOwnershipDenialAsync(getResponse, StoredUninitializedType, _storedUninitializedErrors);

        using HttpResponseMessage putResponse = await PutAsync(
            harness,
            ForeignTenant,
            documentId,
            1301,
            "ownership-uninitialized-put"
        );
        await AssertOwnershipDenialAsync(putResponse, StoredUninitializedType, _storedUninitializedErrors);

        using HttpResponseMessage deleteResponse = await harness.HttpClient.DeleteAsync(
            ResourcePath(ForeignTenant, documentId)
        );
        await AssertOwnershipDenialAsync(deleteResponse, StoredUninitializedType, _storedUninitializedErrors);

        (await ReadStoredOwnershipTokenAsync(harness, documentId)).Should().BeNull();
    }

    /// <summary>
    /// No ownership denial may disclose a token value - not the caller's and not the stored one. Ownership
    /// tokens are numeric, so the property asserted is that no served string carries a digit at all: it holds
    /// for both denial shapes and cannot be satisfied by a body that leaked one. The correlation id is excluded
    /// because it is the request's own trace id.
    /// </summary>
    public static async Task It_never_discloses_an_ownership_token_value(ApiIntegrationHarness harness)
    {
        Guid mismatchId = await CreateAsync(harness, OwnerTenant, 1401, "ownership-no-disclosure-mismatch");
        Guid uninitializedId = await CreateAsync(
            harness,
            NoCreatorTokenTenant,
            1402,
            "ownership-no-disclosure-uninitialized"
        );

        using HttpResponseMessage mismatchResponse = await harness.HttpClient.GetAsync(
            ResourcePath(ForeignTenant, mismatchId)
        );
        using HttpResponseMessage uninitializedResponse = await harness.HttpClient.GetAsync(
            ResourcePath(ForeignTenant, uninitializedId)
        );

        await AssertNoDigitsInServedTextAsync(mismatchResponse);
        await AssertNoDigitsInServedTextAsync(uninitializedResponse);
    }

    /// <summary>
    /// The provider-independent cap: 2,000 tokens fails closed with the security-configuration 500 on both
    /// engines, and 1,999 is authorized on both. The pair runs against the same stored row, so the token count
    /// is the only thing that differs between the two outcomes.
    /// </summary>
    public static async Task It_fails_closed_at_the_ownership_token_cap_and_authorizes_just_under_it(
        ApiIntegrationHarness harness
    )
    {
        Guid documentId = await CreateAsync(harness, OwnerTenant, 1501, "ownership-token-cap");

        using HttpResponseMessage overCapResponse = await harness.HttpClient.GetAsync(
            ResourcePath(TokenCapTenant, documentId)
        );
        string overCapBody = await overCapResponse.Content.ReadAsStringAsync();

        overCapResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError, overCapBody);
        overCapResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        JsonObject overCapProblem = JsonNode.Parse(overCapBody)!.AsObject();
        overCapProblem["type"]!.GetValue<string>().Should().Be(SecurityConfigurationProblemDetails.Type);
        overCapProblem["status"]!.GetValue<int>().Should().Be(SecurityConfigurationProblemDetails.Status);

        // 1,999 is the largest authorized list, and it carries the stored token, so the row is served.
        using HttpResponseMessage underCapResponse = await harness.HttpClient.GetAsync(
            ResourcePath(UnderTokenCapTenant, documentId)
        );
        string underCapBody = await underCapResponse.Content.ReadAsStringAsync();

        underCapResponse.StatusCode.Should().Be(HttpStatusCode.OK, underCapBody);
        underCapBody.Should().Contain("ownership-token-cap");
    }

    /// <summary>
    /// GET-many ownership filtering belongs to a later story, so <c>OwnershipBased</c> on a collection read
    /// stays a 501 rather than silently serving an unfiltered page. The 501 names the strategy, which is what
    /// makes the withholding diagnosable rather than mysterious.
    /// </summary>
    public static async Task It_withholds_get_many_from_ownership_with_a_501(ApiIntegrationHarness harness)
    {
        await CreateAsync(harness, OwnerTenant, 1601, "ownership-get-many");

        using HttpResponseMessage response = await harness.HttpClient.GetAsync(
            string.Format(NullableResourcesEndpointFormat, OwnerTenant)
        );

        await AssertNotImplementedAsync(response);
    }

    /// <summary>
    /// Descriptor ownership enforcement stays withheld on all four operations. The read and write paths are
    /// given a document id nothing was created with, so a 501 there also proves the gate precedes target
    /// lookup rather than depending on a row being present.
    /// </summary>
    public static async Task It_withholds_descriptor_operations_from_ownership_with_a_501(
        ApiIntegrationHarness harness
    )
    {
        string descriptorsEndpoint = string.Format(GradeLevelDescriptorsEndpointFormat, OwnerTenant);
        string unknownId = Guid.NewGuid().ToString();
        string descriptorPath = $"{descriptorsEndpoint}/{unknownId}";

        using HttpResponseMessage postResponse = await SendJsonAsync(
            harness,
            HttpMethod.Post,
            descriptorsEndpoint,
            CreateDescriptorBody(resourceId: null)
        );
        await AssertNotImplementedAsync(postResponse);

        using HttpResponseMessage getResponse = await harness.HttpClient.GetAsync(descriptorPath);
        await AssertNotImplementedAsync(getResponse);

        using HttpResponseMessage putResponse = await SendJsonAsync(
            harness,
            HttpMethod.Put,
            descriptorPath,
            CreateDescriptorBody(unknownId)
        );
        await AssertNotImplementedAsync(putResponse);

        using HttpResponseMessage deleteResponse = await harness.HttpClient.DeleteAsync(descriptorPath);
        await AssertNotImplementedAsync(deleteResponse);
    }

    private static ApplicationContextResult Success(
        long applicationId,
        short? creatorOwnershipTokenId,
        IReadOnlyList<short> ownershipTokenIds
    ) =>
        new ApplicationContextResult.Success(
            new ApplicationContext(
                Id: applicationId,
                ApplicationId: applicationId,
                ClientId: ExternalDoublesConstants.SmokeClientId,
                ClientUuid: ExternalDoublesConstants.StableClientUuid,
                DataStoreIds: [ExternalDoublesConstants.StableDataStoreId],
                CreatorOwnershipTokenId: creatorOwnershipTokenId,
                OwnershipTokenIds: ownershipTokenIds
            )
        );

    /// <summary>
    /// Distinct tokens starting at 1, so the range always contains <see cref="CreatorToken"/> and an authorized
    /// outcome under the cap can never be mistaken for one the cap decided.
    /// </summary>
    private static IReadOnlyList<short> TokenRange(int count) =>
        [.. Enumerable.Range(1, count).Select(static tokenId => (short)tokenId)];

    private static string ResourcePath(string tenant, Guid documentId) =>
        $"{string.Format(NullableResourcesEndpointFormat, tenant)}/{documentId}";

    private static async Task<Guid> CreateAsync(
        ApiIntegrationHarness harness,
        string tenant,
        int authorizationNullableId,
        string name
    )
    {
        using HttpResponseMessage response = await PostAsync(harness, tenant, authorizationNullableId, name);
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);

        return Guid.Parse(GetLocationPath(response).Split('/', StringSplitOptions.RemoveEmptyEntries)[^1]);
    }

    private static async Task<HttpResponseMessage> PostAsync(
        ApiIntegrationHarness harness,
        string tenant,
        int authorizationNullableId,
        string name
    ) =>
        await SendJsonAsync(
            harness,
            HttpMethod.Post,
            string.Format(NullableResourcesEndpointFormat, tenant),
            CreateBody(authorizationNullableId, name, resourceId: null)
        );

    private static async Task<HttpResponseMessage> PutAsync(
        ApiIntegrationHarness harness,
        string tenant,
        Guid documentId,
        int authorizationNullableId,
        string name
    ) =>
        await SendJsonAsync(
            harness,
            HttpMethod.Put,
            ResourcePath(tenant, documentId),
            CreateBody(authorizationNullableId, name, documentId.ToString())
        );

    /// <summary>
    /// <c>nullableSchoolId</c> is deliberately omitted, so the resource carries no securable element and no
    /// reference data has to be seeded: ownership is the only strategy this scenario is about.
    /// </summary>
    private static JsonObject CreateBody(int authorizationNullableId, string name, string? resourceId)
    {
        JsonObject body = new() { ["authorizationNullableId"] = authorizationNullableId, ["name"] = name };

        if (resourceId is not null)
        {
            body["id"] = resourceId;
        }

        return body;
    }

    private static JsonObject CreateDescriptorBody(string? resourceId)
    {
        JsonObject body = new()
        {
            ["codeValue"] = "Tenth grade",
            ["description"] = "Tenth grade",
            ["namespace"] = "uri://ed-fi.org/GradeLevelDescriptor",
            ["shortDescription"] = "Tenth grade",
        };

        if (resourceId is not null)
        {
            body["id"] = resourceId;
        }

        return body;
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(
        ApiIntegrationHarness harness,
        HttpMethod method,
        string endpoint,
        JsonObject body
    )
    {
        using var request = new HttpRequestMessage(method, endpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        return await harness.HttpClient.SendAsync(request);
    }

    private static string GetLocationPath(HttpResponseMessage response)
    {
        response.Headers.Location.Should().NotBeNull();

        return response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location.OriginalString;
    }

    private static async Task AssertOwnershipDenialAsync(
        HttpResponseMessage response,
        string expectedType,
        IReadOnlyList<string> expectedErrors
    )
    {
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        JsonObject problem = JsonNode.Parse(body)!.AsObject();
        problem["type"]!.GetValue<string>().Should().Be(expectedType);
        problem["title"]!.GetValue<string>().Should().Be("Authorization Denied");
        problem["status"]!.GetValue<int>().Should().Be(403);
        // One sentence for both kinds, by design: the client is told only that the item is not owned, and the
        // distinction is carried by type and by the errors entry.
        problem["detail"]!.GetValue<string>().Should().Be(NotOwnedDetail);
        problem["correlationId"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        problem["validationErrors"]!.AsObject().Count.Should().Be(0);
        problem["errors"]!
            .AsArray()
            .Select(static error => error!.GetValue<string>())
            .Should()
            .Equal(expectedErrors);
    }

    private static async Task AssertNoDigitsInServedTextAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);

        JsonObject problem = JsonNode.Parse(body)!.AsObject();

        foreach ((string propertyName, JsonNode? value) in problem)
        {
            if (string.Equals(propertyName, "correlationId", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (string text in CollectStrings(value))
            {
                text.Any(char.IsDigit)
                    .Should()
                    .BeFalse(
                        $"'{propertyName}' must not carry an ownership token value, and '{text}' contains a digit"
                    );
            }
        }
    }

    private static IEnumerable<string> CollectStrings(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue(out string? text):
                yield return text;
                break;

            case JsonArray array:
                foreach (string text in array.SelectMany(CollectStrings))
                {
                    yield return text;
                }
                break;

            case JsonObject jsonObject:
                foreach (string text in jsonObject.SelectMany(property => CollectStrings(property.Value)))
                {
                    yield return text;
                }
                break;
        }
    }

    private static async Task AssertNotImplementedAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented, body);
        JsonNode.Parse(body)!.AsObject()["error"]!
            .GetValue<string>()
            .Should()
            .Contain(AuthorizationStrategyNameConstants.OwnershipBased);
    }

    private static async Task<short?> ReadStoredOwnershipTokenAsync(
        ApiIntegrationHarness harness,
        Guid documentUuid
    )
    {
        string sql = IsMssql(harness.DbConnection)
            ? """
                SELECT [CreatedByOwnershipTokenId]
                FROM [dms].[Document]
                WHERE [DocumentUuid] = @documentUuid;
                """
            : """
                SELECT "CreatedByOwnershipTokenId"
                FROM "dms"."Document"
                WHERE "DocumentUuid" = @documentUuid;
                """;

        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = sql;

        DbParameter parameter = command.CreateParameter();
        parameter.ParameterName = "@documentUuid";
        parameter.Value = documentUuid;
        command.Parameters.Add(parameter);

        object? value = await command.ExecuteScalarAsync();

        value.Should().NotBeNull("the document row must exist for its stored ownership token to be read");

        return value is DBNull ? null : Convert.ToInt16(value, CultureInfo.InvariantCulture);
    }

    private static bool IsMssql(DbConnection connection)
    {
        string? fullName = connection.GetType().FullName;
        return fullName is not null && fullName.Contains("SqlClient", StringComparison.Ordinal);
    }
}
