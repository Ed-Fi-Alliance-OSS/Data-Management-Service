// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
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
    private const string DescriptorsEndpoint = "/data/ed-fi/academicSubjectDescriptors";

    /// <summary>The code value of the descriptor held only by the snapshot.</summary>
    private const string SnapshotAuthorizedCode = "snapshot-authorized";

    /// <summary>The code value held only by the parent, so a leak from it is unmistakable.</summary>
    private const string PrimaryAuthorizedCode = "primary-authorized";

    public static async Task SeedAsync(
        ApiIntegrationHarness harness,
        MutableInstanceProvider provider,
        long dataStoreId,
        RelationalProviderToken providerToken,
        string primaryConnectionString,
        string snapshotConnectionString
    )
    {
        provider.Publish([
            DerivativeRoutingSupport.ParentOnly(dataStoreId, snapshotConnectionString, providerToken),
        ]);
        await CreateDescriptorAsync(harness, SnapshotAuthorizedCode, authorized: true);

        provider.Publish([
            DerivativeRoutingSupport.ParentOnly(dataStoreId, primaryConnectionString, providerToken),
        ]);
        await CreateDescriptorAsync(harness, PrimaryAuthorizedCode, authorized: true);

        provider.Publish([
            DerivativeRoutingSupport.ParentWith(
                dataStoreId,
                primaryConnectionString,
                providerToken,
                new Dictionary<DataStoreDerivativeType, string>
                {
                    [DataStoreDerivativeType.Snapshot] = snapshotConnectionString,
                }
            ),
        ]);
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
                    "the parent's namespace claim governs the routed read, and it holds no prefix "
                        + "matching this namespace"
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
