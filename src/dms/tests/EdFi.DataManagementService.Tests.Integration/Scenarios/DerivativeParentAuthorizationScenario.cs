// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Tests.Integration.Doubles;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Integration.Scenarios;

/// <summary>
/// Authorization is the parent's, the rows are the derivative's. The client identity, its claim set, and
/// the route context all come from the parent's configuration; the SQL that decides which rows the
/// client may see runs against the database the request was routed to.
/// </summary>
/// <remarks>
/// Real authorization runs here - the fixture does not bypass it - and the outcome is asserted in both
/// directions. A test that only showed an allowed request succeeding would pass just as well if
/// authorization had been skipped entirely, which is why a document the client may not see is seeded
/// alongside one it may.
/// </remarks>
internal static class DerivativeParentAuthorizationScenario
{
    /// <summary>
    /// The qualifier segment every request in this fixture carries. The parent's route context names
    /// it, so a request without it resolves to no data store and is refused before anything else -
    /// which is what makes the qualifier load-bearing rather than decorative.
    /// </summary>
    public const string DistrictQualifierSegment = "255901";

    private const string DescriptorsEndpoint =
        $"/{DistrictQualifierSegment}/data/ed-fi/academicSubjectDescriptors";

    /// <summary>The code value of the descriptor the caller may read, held only by the snapshot.</summary>
    private const string SnapshotAuthorizedCode = "snapshot-authorized";

    /// <summary>
    /// A descriptor that exists on the snapshot in a namespace the caller does not hold. It has to
    /// exist for the denial to mean anything: without it, a filtered read returning nothing would
    /// return nothing whether or not authorization ran at all.
    /// </summary>
    private const string SnapshotUnauthorizedCode = "snapshot-unauthorized";

    /// <summary>The code value held only by the parent, so a leak from it is unmistakable.</summary>
    private const string PrimaryAuthorizedCode = "primary-authorized";

    public static async Task SeedAsync(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        MutableNamespacePrefixJwtValidationService clientIdentity,
        long dataStoreId,
        RelationalProviderToken providerToken,
        IReadOnlyDictionary<RouteQualifierName, RouteQualifierValue> routeContext,
        string primaryConnectionString,
        string snapshotConnectionString
    )
    {
        // Widened only for the seed. A namespace-authorized write is refused for a namespace the
        // caller does not hold, so the unauthorized row could not otherwise be created; the caller is
        // narrowed back before any assertion runs.
        clientIdentity.SetNamespacePrefixes([
            CursorPartitionAuthorizationMatrixSupport.AuthorizedNamespacePrefix,
            CursorPartitionAuthorizationMatrixSupport.UnauthorizedNamespacePrefix,
        ]);

        // Every publication carries the same route context, because the host only resolves a data
        // store whose qualifiers match the ones the request path carries.
        provider.Publish([
            new FakeDataStoreDefinition(
                dataStoreId,
                snapshotConnectionString,
                providerToken,
                RouteContext: routeContext
            ),
        ]);
        await CreateDescriptorAsync(harness, SnapshotAuthorizedCode, authorized: true);
        await CreateDescriptorAsync(harness, SnapshotUnauthorizedCode, authorized: false);

        provider.Publish([
            new FakeDataStoreDefinition(
                dataStoreId,
                primaryConnectionString,
                providerToken,
                RouteContext: routeContext
            ),
        ]);
        await CreateDescriptorAsync(harness, PrimaryAuthorizedCode, authorized: true);

        provider.Publish([
            new FakeDataStoreDefinition(
                dataStoreId,
                primaryConnectionString,
                providerToken,
                new Dictionary<DataStoreDerivativeType, string>
                {
                    [DataStoreDerivativeType.Snapshot] = snapshotConnectionString,
                },
                routeContext
            ),
        ]);

        // Narrowed back, so every assertion below runs as a caller that holds one prefix.
        clientIdentity.SetNamespacePrefixes([
            CursorPartitionAuthorizationMatrixSupport.AuthorizedNamespacePrefix,
        ]);
    }

    /// <summary>
    /// The route qualifiers a routed request resolves through are the parent's. The derivative hangs
    /// off that same parent and carries no context of its own, so routing cannot move a request into a
    /// different tenant or district.
    /// </summary>
    public static async Task AssertRouteContextResolvedFromTheParent(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        long dataStoreId,
        IReadOnlyDictionary<RouteQualifierName, RouteQualifierValue> expectedRouteContext
    )
    {
        DataStore parent =
            provider.GetById(dataStoreId)
            ?? throw new InvalidOperationException("The parent data store is not published.");

        parent.RouteContext.Should().BeEquivalentTo(expectedRouteContext);
        parent.Derivatives.Should().ContainKey(DataStoreDerivativeType.Snapshot);

        // And the qualifier is load-bearing: the same routed read without it is not found. With a
        // qualifier segment configured, that path shape matches no route at all, so the refusal comes
        // from routing rather than from data-store resolution - either way the request never reaches a
        // database, which is the property being asserted.
        using HttpResponseMessage withoutQualifier = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            "/data/ed-fi/academicSubjectDescriptors",
            useSnapshotHeaderValue: "true"
        );

        string body = await withoutQualifier.Content.ReadAsStringAsync();

        withoutQualifier
            .StatusCode.Should()
            .Be(
                HttpStatusCode.NotFound,
                $"a request that carries no district qualifier resolves to nothing: {body}"
            );

        // The qualified path, by contrast, resolves and is served.
        using HttpResponseMessage withQualifier = await DerivativeRoutingSupport.SendAsync(
            harness,
            HttpMethod.Get,
            DescriptorsEndpoint,
            useSnapshotHeaderValue: "true"
        );

        withQualifier
            .StatusCode.Should()
            .Be(HttpStatusCode.OK, await withQualifier.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The routed read returns the derivative's authorized descriptor and withholds its unauthorized
    /// one. The parent is unreachable throughout, so the authorization SQL cannot have run anywhere but
    /// the snapshot - while the claim set and client identity that shaped it came from the parent's
    /// configuration.
    /// </summary>
    public static async Task It_applies_parent_authorization_to_derivative_rows(
        ApiIntegrationHarness harness,
        IDerivativeTargetReachability reachability,
        string primaryConnectionString
    )
    {
        await reachability.MakeUnreachableAsync(primaryConnectionString);

        try
        {
            using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Get,
                DescriptorsEndpoint,
                useSnapshotHeaderValue: "true"
            );

            string body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(HttpStatusCode.OK, body);

            string[] codeValues =
            [
                .. JsonNode
                    .Parse(body)!
                    .AsArray()
                    .Select(element => element!["codeValue"]!.GetValue<string>()),
            ];

            codeValues
                .Should()
                .Contain(
                    SnapshotAuthorizedCode,
                    "the client's namespace prefix authorizes this one, and it lives on the snapshot"
                );
            codeValues
                .Should()
                .NotContain(
                    SnapshotUnauthorizedCode,
                    "it exists on the serving database, and only authorization keeps it out"
                );
            codeValues.Should().NotContain(PrimaryAuthorizedCode, "the parent was never read");
        }
        finally
        {
            await reachability.MakeReachableAsync(primaryConnectionString);
        }
    }

    /// <summary>
    /// A read filtered to a namespace the client does not hold yields nothing on the routed path. The
    /// filter is applied by the authorization SQL, which ran against the snapshot - the parent is
    /// unreachable - using the prefixes the parent's client identity carries.
    /// </summary>
    /// <remarks>
    /// A GET-many narrows to what the caller may see rather than refusing outright; the outright
    /// refusal is the write below. Both are asserted, so the pair covers the allowed and the denied
    /// direction without either standing in for the other.
    /// </remarks>
    public static async Task It_yields_nothing_for_an_unauthorized_namespace_on_the_routed_path(
        ApiIntegrationHarness harness,
        IDerivativeTargetReachability reachability,
        string primaryConnectionString
    )
    {
        await reachability.MakeUnreachableAsync(primaryConnectionString);

        try
        {
            string unauthorizedNamespace =
                CursorPartitionAuthorizationMatrixSupport.UnauthorizedNamespacePrefix
                + "AcademicSubjectDescriptor";

            using HttpResponseMessage response = await DerivativeRoutingSupport.SendAsync(
                harness,
                HttpMethod.Get,
                $"{DescriptorsEndpoint}?namespace={Uri.EscapeDataString(unauthorizedNamespace)}",
                useSnapshotHeaderValue: "true"
            );

            string body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().Be(HttpStatusCode.OK, body);
            JsonNode
                .Parse(body)!
                .AsArray()
                .Should()
                .BeEmpty(
                    "a row in this namespace exists on the snapshot, so an empty result can only be "
                        + "the parent's namespace claim excluding it"
                );
        }
        finally
        {
            await reachability.MakeReachableAsync(primaryConnectionString);
        }
    }

    /// <summary>
    /// A write carrying a namespace the client does not hold is refused as well, so the denial is not
    /// an artifact of how reads are filtered.
    /// </summary>
    public static async Task It_refuses_an_unauthorized_write_while_a_derivative_is_configured(
        ApiIntegrationHarness harness
    )
    {
        using HttpContent content = DescriptorContent("routed-write-attempt", authorized: false);
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(DescriptorsEndpoint, content);

        string body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, body);
        body.Should().Contain("namespace-mismatch");
    }

    private static async Task CreateDescriptorAsync(
        ApiIntegrationHarness harness,
        string codeValue,
        bool authorized
    )
    {
        using HttpContent content = DescriptorContent(codeValue, authorized);
        using HttpResponseMessage response = await harness.HttpClient.PostAsync(DescriptorsEndpoint, content);

        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
    }

    private static HttpContent DescriptorContent(string codeValue, bool authorized)
    {
        string prefix = authorized
            ? CursorPartitionAuthorizationMatrixSupport.AuthorizedNamespacePrefix
            : CursorPartitionAuthorizationMatrixSupport.UnauthorizedNamespacePrefix;

        JsonObject payload = new()
        {
            ["namespace"] = prefix + "AcademicSubjectDescriptor",
            ["codeValue"] = codeValue,
            ["shortDescription"] = codeValue,
        };

        return new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
    }
}
