// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
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
/// <c>auth.md</c> 2.13 and 2.14, stamping on create, the authorized round trip, the GET-many page filter, the
/// provider-independent token cap, descriptor GET-by-id, and the descriptor operations that stay withheld with a
/// 501. The provider matrix
/// lives in the backend suites; this scenario owns only what those cannot observe - the served response body
/// and the real application-context plumbing that carries the caller's ownership tokens.
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
    private const string AcademicSubjectDescriptorsEndpointFormat =
        "/{0}/data/ed-fi/academicSubjectDescriptors";
    private const string SeedableDescriptorResourceName = "AcademicSubjectDescriptor";

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
    /// Every property <c>FailureResponse.CreateBaseJsonObject</c> emits, and nothing else. Pinned so the
    /// no-disclosure scan cannot be outflanked by a field it does not know to look at.
    /// </summary>
    private static readonly string[] _problemDetailsProperties =
    [
        "detail",
        "type",
        "title",
        "status",
        "correlationId",
        "validationErrors",
        "errors",
    ];

    /// <summary>
    /// <c>OwnershipBased</c> on every resource and action, which exercises the whole surface at once: a create
    /// is stamped or, for a caller that could not own the row, refused; every single-record read and write is
    /// enforced; GET-many is filtered; descriptor GET-by-id is enforced and the other descriptor operations
    /// stay withheld. Seeding goes through the owner, whose creates are authorized, and a NULL or foreign stamp
    /// is fabricated directly when a step needs one.
    /// </summary>
    /// <remarks>
    /// One exception: <c>AcademicSubjectDescriptor</c> creates and updates under
    /// <c>NoFurtherAuthorizationRequired</c>, so its rows can be seeded through the API while descriptor writes
    /// under <c>OwnershipBased</c> still answer 501. Its reads and deletes keep <c>OwnershipBased</c>.
    /// </remarks>
    public static IClaimSetProvider CreateClaimSetProvider(FixtureContext fixture) =>
        new ConfigurableClaimSetProvider(
            fixture,
            static (resource, action) =>
                resource.ResourceName == SeedableDescriptorResourceName && action is "Create" or "Update"
                    ? [AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired]
                    : [AuthorizationStrategyNameConstants.OwnershipBased]
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
    /// A create is stamped from the caller's creator token when the caller holds that token. A create that
    /// would leave a row its own client could never reach is refused before any row is written, with the body
    /// of the stored state it would have produced: no creator token is 2.14, a creator token outside the
    /// caller's ownership tokens is 2.13. The absence of a row is proven by counting <c>dms.Document</c>, not
    /// by the stamp reader, which requires the row to exist.
    /// </summary>
    public static async Task It_stamps_the_creator_ownership_token_on_create_and_refuses_creates_the_caller_could_not_own(
        ApiIntegrationHarness harness
    )
    {
        Guid stampedId = await CreateAsync(harness, OwnerTenant, 1001, "ownership-stamped-create");

        (await ReadStoredOwnershipTokenAsync(harness, stampedId)).Should().Be(CreatorToken);

        long documentCount = await CountDocumentsAsync(harness);

        // Carries no creator token, so the row it would create is stamped NULL and unreachable by anyone.
        using HttpResponseMessage uninitializedResponse = await PostAsync(
            harness,
            NoCreatorTokenTenant,
            1002,
            "ownership-null-create"
        );
        await AssertOwnershipDenialAsync(
            uninitializedResponse,
            StoredUninitializedType,
            _storedUninitializedErrors
        );
        uninitializedResponse.Headers.Location.Should().BeNull();
        (await CountDocumentsAsync(harness)).Should().Be(documentCount);

        // Its creator token is not among its own ownership tokens, so the row it would stamp is foreign to it.
        using HttpResponseMessage mismatchResponse = await PostAsync(
            harness,
            ForeignTenant,
            1003,
            "ownership-foreign-create"
        );
        await AssertOwnershipDenialAsync(mismatchResponse, MismatchType, []);
        mismatchResponse.Headers.Location.Should().BeNull();
        (await CountDocumentsAsync(harness)).Should().Be(documentCount);
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
    /// 2.14 on every enforced operation against a row whose stored token is NULL: a distinct <c>type</c> and an
    /// <c>errors</c> entry naming the strategy, over the same shared detail sentence 2.13 uses.
    /// </summary>
    public static async Task It_returns_stored_uninitialized_problem_details_for_reads_and_writes(
        ApiIntegrationHarness harness
    )
    {
        Guid documentId = await SeedWithStoredOwnershipTokenAsync(
            harness,
            1301,
            "ownership-uninitialized",
            storedOwnershipTokenId: null
        );

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

        // The same identity, so this POST resolves to an upsert-as-update against the stored row rather than
        // to a create - the stored NULL is what denies it here, not the caller's own creator token.
        using HttpResponseMessage postAsUpdateResponse = await PostAsync(
            harness,
            ForeignTenant,
            1301,
            "ownership-uninitialized-post-as-update"
        );
        await AssertOwnershipDenialAsync(
            postAsUpdateResponse,
            StoredUninitializedType,
            _storedUninitializedErrors
        );

        using HttpResponseMessage deleteResponse = await harness.HttpClient.DeleteAsync(
            ResourcePath(ForeignTenant, documentId)
        );
        await AssertOwnershipDenialAsync(deleteResponse, StoredUninitializedType, _storedUninitializedErrors);

        (await ReadStoredOwnershipTokenAsync(harness, documentId)).Should().BeNull();
    }

    /// <summary>
    /// No ownership denial may disclose a token value - not the caller's and not the stored one. Ownership
    /// tokens are numeric, so the property asserted is that no served value carries a digit at all, JSON
    /// numbers as well as strings, because a leaked token would most naturally arrive as a number. The body's
    /// property set is pinned alongside the scan, so a field cannot be added to a denial and skipped. Only the
    /// correlation id, which is the request's own trace id, and the 403 status itself are excused.
    /// </summary>
    public static async Task It_never_discloses_an_ownership_token_value(ApiIntegrationHarness harness)
    {
        Guid mismatchId = await CreateAsync(harness, OwnerTenant, 1401, "ownership-no-disclosure-mismatch");
        Guid uninitializedId = await SeedWithStoredOwnershipTokenAsync(
            harness,
            1402,
            "ownership-no-disclosure-uninitialized",
            storedOwnershipTokenId: null
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
    /// The cap fails a POST whichever branch its target selects, before any DML. Over the cap, a create is
    /// refused with the security-configuration 500 and no row is written, even though the over-cap tenant's
    /// creator token is among its 2,000; and the same tenant's POST of an owner's identity, which resolves to an
    /// upsert-as-update, is refused the same way, leaving the row's token and name alone. One token under the
    /// cap, the create succeeds and is stamped.
    /// </summary>
    public static async Task It_fails_closed_at_the_ownership_token_cap_for_a_post_create_and_a_post_as_update(
        ApiIntegrationHarness harness
    )
    {
        long documentCount = await CountDocumentsAsync(harness);

        using HttpResponseMessage overCapCreateResponse = await PostAsync(
            harness,
            TokenCapTenant,
            1701,
            "ownership-over-cap-create"
        );
        await AssertSecurityConfigurationFailureAsync(overCapCreateResponse);
        overCapCreateResponse.Headers.Location.Should().BeNull();
        (await CountDocumentsAsync(harness)).Should().Be(documentCount);

        Guid underCapId = await CreateAsync(harness, UnderTokenCapTenant, 1702, "ownership-under-cap-create");

        (await ReadStoredOwnershipTokenAsync(harness, underCapId)).Should().Be(CreatorToken);

        Guid documentId = await CreateAsync(harness, OwnerTenant, 1703, "ownership-over-cap-seed");

        using HttpResponseMessage postAsUpdateResponse = await PostAsync(
            harness,
            TokenCapTenant,
            1703,
            "ownership-over-cap-post-as-update"
        );
        await AssertSecurityConfigurationFailureAsync(postAsUpdateResponse);

        (await ReadStoredOwnershipTokenAsync(harness, documentId)).Should().Be(CreatorToken);

        // The owner still reads the seeded representation, so the refused upsert-as-update wrote nothing.
        using HttpResponseMessage ownerGetResponse = await harness.HttpClient.GetAsync(
            ResourcePath(OwnerTenant, documentId)
        );
        string ownerGetBody = await ownerGetResponse.Content.ReadAsStringAsync();
        ownerGetResponse.StatusCode.Should().Be(HttpStatusCode.OK, ownerGetBody);
        ownerGetBody.Should().Contain("ownership-over-cap-seed");
        ownerGetBody.Should().NotContain("ownership-over-cap-post-as-update");
    }

    private static async Task AssertSecurityConfigurationFailureAsync(HttpResponseMessage response)
    {
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, body);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        JsonObject problem = JsonNode.Parse(body)!.AsObject();
        problem["type"]!.GetValue<string>().Should().Be(SecurityConfigurationProblemDetails.Type);
        problem["status"]!.GetValue<int>().Should().Be(SecurityConfigurationProblemDetails.Status);
    }

    /// <summary>
    /// GET-many is filtered by the caller's ownership tokens rather than denied or withheld. The owner holds
    /// the token its rows were stamped with, so it is served them and never a null-stamped row; a caller
    /// holding only a token nothing was ever stamped with is served an empty page whose total count is zero,
    /// not a 403. Every row the owner is served carries its token, which states the filter as a property of
    /// the page rather than of one seeded row.
    /// </summary>
    public static async Task It_filters_get_many_to_the_callers_ownership_tokens(
        ApiIntegrationHarness harness
    )
    {
        Guid ownedId = await CreateAsync(harness, OwnerTenant, 1601, "ownership-get-many-owned");
        Guid unstampedId = await SeedWithStoredOwnershipTokenAsync(
            harness,
            1602,
            "ownership-get-many-unstamped",
            storedOwnershipTokenId: null
        );

        using HttpResponseMessage ownerResponse = await harness.HttpClient.GetAsync(
            $"{string.Format(NullableResourcesEndpointFormat, OwnerTenant)}?totalCount=true"
        );
        string ownerBody = await ownerResponse.Content.ReadAsStringAsync();

        ownerResponse.StatusCode.Should().Be(HttpStatusCode.OK, ownerBody);
        List<Guid> ownerIds = ReadServedIds(ownerBody);
        ownerIds.Should().Contain(ownedId);
        ownerIds.Should().NotContain(unstampedId);
        ReadTotalCount(ownerResponse).Should().BeGreaterThanOrEqualTo(ownerIds.Count);

        foreach (Guid servedId in ownerIds)
        {
            (await ReadStoredOwnershipTokenAsync(harness, servedId)).Should().Be(CreatorToken);
        }

        using HttpResponseMessage foreignResponse = await harness.HttpClient.GetAsync(
            $"{string.Format(NullableResourcesEndpointFormat, ForeignTenant)}?totalCount=true"
        );
        string foreignBody = await foreignResponse.Content.ReadAsStringAsync();

        foreignResponse.StatusCode.Should().Be(HttpStatusCode.OK, foreignBody);
        ReadServedIds(foreignBody).Should().BeEmpty();
        ReadTotalCount(foreignResponse).Should().Be(0);
    }

    /// <summary>
    /// The defensive cap gates the page filter as it gates the single-record check: 2,000 tokens fail closed
    /// with the security-configuration 500 before any page runs, and 1,999 — which carries the creator token —
    /// serves the stamped row.
    /// </summary>
    public static async Task It_fails_closed_for_get_many_at_the_ownership_token_cap_and_serves_just_under_it(
        ApiIntegrationHarness harness
    )
    {
        Guid documentId = await CreateAsync(harness, OwnerTenant, 1603, "ownership-get-many-token-cap");

        using HttpResponseMessage overCapResponse = await harness.HttpClient.GetAsync(
            string.Format(NullableResourcesEndpointFormat, TokenCapTenant)
        );
        string overCapBody = await overCapResponse.Content.ReadAsStringAsync();

        overCapResponse.StatusCode.Should().Be(HttpStatusCode.InternalServerError, overCapBody);
        overCapResponse.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");

        JsonObject overCapProblem = JsonNode.Parse(overCapBody)!.AsObject();
        overCapProblem["type"]!.GetValue<string>().Should().Be(SecurityConfigurationProblemDetails.Type);
        overCapProblem["status"]!.GetValue<int>().Should().Be(SecurityConfigurationProblemDetails.Status);

        using HttpResponseMessage underCapResponse = await harness.HttpClient.GetAsync(
            string.Format(NullableResourcesEndpointFormat, UnderTokenCapTenant)
        );
        string underCapBody = await underCapResponse.Content.ReadAsStringAsync();

        underCapResponse.StatusCode.Should().Be(HttpStatusCode.OK, underCapBody);
        ReadServedIds(underCapBody).Should().Contain(documentId);
    }

    private static List<Guid> ReadServedIds(string collectionBody) =>
        [
            .. JsonNode
                .Parse(collectionBody)!
                .AsArray()
                .Select(static item => Guid.Parse(item!["id"]!.GetValue<string>())),
        ];

    private static int ReadTotalCount(HttpResponseMessage response) =>
        int.Parse(response.Headers.GetValues("Total-Count").Single(), CultureInfo.InvariantCulture);

    /// <summary>
    /// Descriptor GET-by-id is enforced with the same bodies as a regular resource: the owner is served, a
    /// holder of other tokens gets 2.13, a descriptor never stamped gets 2.14, an unknown id is a 404 rather than
    /// a 403, and the token cap is the security-configuration 500 with 1,999 still served. The other descriptor
    /// operations stay withheld with a 501; they are given a document id nothing was created with, so a 501
    /// there also proves the gate precedes target lookup rather than depending on a row being present.
    /// </summary>
    public static async Task It_enforces_descriptor_get_by_id_ownership_and_withholds_the_other_descriptor_operations_with_a_501(
        ApiIntegrationHarness harness
    )
    {
        Guid ownedId = await CreateSeedableDescriptorAsync(harness, OwnerTenant, "OwnedSubject");
        (await ReadStoredOwnershipTokenAsync(harness, ownedId)).Should().Be(CreatorToken);

        // A create under NoFurtherAuthorizationRequired by a client with no creator token stamps NULL, which is
        // exactly how an unreachable descriptor arises.
        Guid unstampedId = await CreateSeedableDescriptorAsync(
            harness,
            NoCreatorTokenTenant,
            "UnstampedSubject"
        );
        (await ReadStoredOwnershipTokenAsync(harness, unstampedId)).Should().BeNull();

        using HttpResponseMessage ownerResponse = await harness.HttpClient.GetAsync(
            SeedableDescriptorPath(OwnerTenant, ownedId)
        );
        string ownerBody = await ownerResponse.Content.ReadAsStringAsync();
        ownerResponse.StatusCode.Should().Be(HttpStatusCode.OK, ownerBody);
        ownerBody.Should().Contain("OwnedSubject");

        using HttpResponseMessage foreignResponse = await harness.HttpClient.GetAsync(
            SeedableDescriptorPath(ForeignTenant, ownedId)
        );
        await AssertOwnershipDenialAsync(foreignResponse, MismatchType, []);

        using HttpResponseMessage unstampedResponse = await harness.HttpClient.GetAsync(
            SeedableDescriptorPath(OwnerTenant, unstampedId)
        );
        await AssertOwnershipDenialAsync(
            unstampedResponse,
            StoredUninitializedType,
            _storedUninitializedErrors
        );

        using HttpResponseMessage unknownSeedableResponse = await harness.HttpClient.GetAsync(
            SeedableDescriptorPath(ForeignTenant, Guid.NewGuid())
        );
        string unknownSeedableBody = await unknownSeedableResponse.Content.ReadAsStringAsync();
        unknownSeedableResponse.StatusCode.Should().Be(HttpStatusCode.NotFound, unknownSeedableBody);

        using HttpResponseMessage overCapResponse = await harness.HttpClient.GetAsync(
            SeedableDescriptorPath(TokenCapTenant, ownedId)
        );
        await AssertSecurityConfigurationFailureAsync(overCapResponse);

        using HttpResponseMessage underCapResponse = await harness.HttpClient.GetAsync(
            SeedableDescriptorPath(UnderTokenCapTenant, ownedId)
        );
        string underCapBody = await underCapResponse.Content.ReadAsStringAsync();
        underCapResponse.StatusCode.Should().Be(HttpStatusCode.OK, underCapBody);

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
        string getBody = await getResponse.Content.ReadAsStringAsync();
        getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound, getBody);

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

    /// <summary>
    /// Seeds a row carrying a chosen stored ownership token without relying on how a create is authorized: the
    /// owner creates it, then the stamp is overwritten directly. Product code never updates the stamp, so this
    /// fabricates exactly the state a legacy or misconfigured create leaves behind, and reading it back proves
    /// the seed holds the value the caller's assertions are about.
    /// </summary>
    private static async Task<Guid> SeedWithStoredOwnershipTokenAsync(
        ApiIntegrationHarness harness,
        int authorizationNullableId,
        string name,
        short? storedOwnershipTokenId
    )
    {
        Guid documentId = await CreateAsync(harness, OwnerTenant, authorizationNullableId, name);

        string sql = IsMssql(harness.DbConnection)
            ? """
                UPDATE [dms].[Document]
                SET [CreatedByOwnershipTokenId] = @storedOwnershipTokenId
                WHERE [DocumentUuid] = @documentUuid;
                """
            : """
                UPDATE "dms"."Document"
                SET "CreatedByOwnershipTokenId" = @storedOwnershipTokenId
                WHERE "DocumentUuid" = @documentUuid;
                """;

        await using (DbCommand command = harness.DbConnection.CreateCommand())
        {
            command.CommandText = sql;

            DbParameter tokenParameter = command.CreateParameter();
            tokenParameter.ParameterName = "@storedOwnershipTokenId";
            tokenParameter.DbType = DbType.Int16;
            tokenParameter.Value = (object?)storedOwnershipTokenId ?? DBNull.Value;
            command.Parameters.Add(tokenParameter);

            DbParameter uuidParameter = command.CreateParameter();
            uuidParameter.ParameterName = "@documentUuid";
            uuidParameter.Value = documentId;
            command.Parameters.Add(uuidParameter);

            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }

        (await ReadStoredOwnershipTokenAsync(harness, documentId)).Should().Be(storedOwnershipTokenId);

        return documentId;
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

    private static string SeedableDescriptorPath(string tenant, Guid documentId) =>
        $"{string.Format(AcademicSubjectDescriptorsEndpointFormat, tenant)}/{documentId}";

    private static async Task<Guid> CreateSeedableDescriptorAsync(
        ApiIntegrationHarness harness,
        string tenant,
        string codeValue
    )
    {
        using HttpResponseMessage response = await SendJsonAsync(
            harness,
            HttpMethod.Post,
            string.Format(AcademicSubjectDescriptorsEndpointFormat, tenant),
            new JsonObject
            {
                ["codeValue"] = codeValue,
                ["namespace"] = "uri://ed-fi.org/AcademicSubjectDescriptor",
                ["shortDescription"] = codeValue,
            }
        );
        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, body);

        return Guid.Parse(GetLocationPath(response).Split('/', StringSplitOptions.RemoveEmptyEntries)[^1]);
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

        // Pinning the property set is what makes the scan below complete rather than best-effort: a field
        // added to a denial body in future has to be accounted for here before it can be excluded from the
        // scan, so it cannot arrive already exempt.
        problem.Select(static property => property.Key).Should().BeEquivalentTo(_problemDetailsProperties);

        foreach ((string propertyName, JsonNode? value) in problem)
        {
            // The correlation id is the request's own trace id, and the status is the 403 itself. Everything
            // else in the body is client-facing prose that has no reason to carry a number.
            if (
                string.Equals(propertyName, "correlationId", StringComparison.Ordinal)
                || string.Equals(propertyName, "status", StringComparison.Ordinal)
            )
            {
                continue;
            }

            foreach (string text in CollectScalarText(value))
            {
                text.Any(char.IsDigit)
                    .Should()
                    .BeFalse(
                        $"'{propertyName}' must not carry an ownership token value, and '{text}' contains a digit"
                    );
            }
        }
    }

    /// <summary>
    /// Every scalar under <paramref name="node"/> rendered as text, non-strings included. An ownership token
    /// leaked into a body would most naturally arrive as a JSON number, which a string-only walk would step
    /// straight over.
    /// </summary>
    private static IEnumerable<string> CollectScalarText(JsonNode? node)
    {
        switch (node)
        {
            case JsonValue value when value.TryGetValue(out string? text):
                yield return text;
                break;

            case JsonValue value:
                yield return value.ToJsonString();
                break;

            case JsonArray array:
                foreach (string text in array.SelectMany(CollectScalarText))
                {
                    yield return text;
                }
                break;

            case JsonObject jsonObject:
                foreach (string text in jsonObject.SelectMany(property => CollectScalarText(property.Value)))
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

    /// <summary>
    /// Every <c>dms.Document</c> row, whatever its stamp. A refused create returns no location to look a row
    /// up by, and the ownership-filtered GET-many cannot see a NULL-stamped row, so an unchanged count is the
    /// proof that nothing was written.
    /// </summary>
    private static async Task<long> CountDocumentsAsync(ApiIntegrationHarness harness)
    {
        await using DbCommand command = harness.DbConnection.CreateCommand();
        command.CommandText = IsMssql(harness.DbConnection)
            ? "SELECT COUNT_BIG(*) FROM [dms].[Document];"
            : """SELECT COUNT(*) FROM "dms"."Document";""";

        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
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
