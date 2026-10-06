// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Tests.Integration.Doubles;

namespace EdFi.DataManagementService.Tests.Integration.Plugins.Identity;

/// <summary>One caller: a client id, the bearer token it presents, and the tenant it is bound to.</summary>
internal sealed record IdentityTestClient(string Name, string ClientId, string Token, string Tenant);

/// <summary>
/// The tenants, route qualifier values, clients and bindings every identity plugin case shares.
/// </summary>
/// <remarks>
/// Client A and client B are bound to tenant one and client C to tenant two, so a case can tell
/// same-tenant cross-client from cross-tenant access. <see cref="CaseVariantOfA"/> differs from
/// client A only by case and is bound to tenant one, so a case can tell a comparison that ignores
/// case from one that does not.
/// </remarks>
internal static class IdentityTestClients
{
    public const string TenantOne = "tenant-one";
    public const string TenantTwo = "tenant-two";

    public const string RouteQualifierSegments = "districtId,schoolYear";
    public const string SchoolYear = "2026";

    public static IReadOnlyList<string> Tenants { get; } = [TenantOne, TenantTwo];

    /// <summary>Two district qualifier values per tenant.</summary>
    public static IReadOnlyList<string> DistrictsOf(string tenant) =>
        tenant switch
        {
            TenantOne => ["255901", "255902"],
            TenantTwo => ["255903", "255904"],
            _ => throw new ArgumentOutOfRangeException(nameof(tenant), tenant, "Not a known test tenant."),
        };

    public static IdentityTestClient ClientA { get; } =
        new("A", "identity-client-a", "identity-token-a", TenantOne);

    public static IdentityTestClient ClientB { get; } =
        new("B", "identity-client-b", "identity-token-b", TenantOne);

    public static IdentityTestClient ClientC { get; } =
        new("C", "identity-client-c", "identity-token-c", TenantTwo);

    public static IdentityTestClient CaseVariantOfA { get; } =
        new("A-case-variant", "IDENTITY-CLIENT-A", "identity-token-a-case-variant", TenantOne);

    public static IReadOnlyList<IdentityTestClient> All { get; } =
    [ClientA, ClientB, ClientC, CaseVariantOfA];

    /// <summary>A token no client holds, for the real <c>401</c>.</summary>
    public const string UnknownToken = "identity-token-unknown";

    /// <summary>The token table the JWT double serves.</summary>
    public static PerTokenJwtValidationService CreateJwtValidationService() =>
        new(
            All.ToDictionary(
                client => client.Token,
                client => new ClientAuthorizations(
                    TokenId: $"token-id-{client.Name}",
                    ClientId: client.ClientId,
                    ClaimSetName: ExternalDoublesConstants.SmokeClaimSetName,
                    EducationOrganizationIds: [],
                    NamespacePrefixes: [],
                    DataStoreIds: []
                )
            )
        );

    /// <summary>
    /// Success for a client bound to the tenant, and not found for any other pair. The host's
    /// client-to-tenant binding step turns the second into a rejection.
    /// </summary>
    public static ApplicationContextResult ResolveBinding(string clientId, string? tenant)
    {
        IdentityTestClient? bound = All.FirstOrDefault(client =>
            string.Equals(client.ClientId, clientId, StringComparison.Ordinal)
            && string.Equals(client.Tenant, tenant, StringComparison.Ordinal)
        );

        return bound is null
            ? new ApplicationContextResult.NotFound()
            : new ApplicationContextResult.Success(
                new ApplicationContext(
                    Id: 1,
                    ApplicationId: 1,
                    ClientId: bound.ClientId,
                    ClientUuid: ExternalDoublesConstants.StableClientUuid,
                    DataStoreIds: [],
                    CreatorOwnershipTokenId: null,
                    OwnershipTokenIds: []
                )
            );
    }

    /// <summary>The route <c>/{tenant}/{districtId}/{schoolYear}/identity/v2/{path}</c>.</summary>
    public static string Route(
        string tenant,
        string districtId,
        string path,
        string schoolYear = SchoolYear
    ) => $"/{tenant}/{districtId}/{schoolYear}/identity/v2/{path.TrimStart('/')}";

    /// <summary>The route under the client's own tenant and that tenant's first district.</summary>
    public static string RouteFor(IdentityTestClient client, string path) =>
        Route(client.Tenant, DistrictsOf(client.Tenant)[0], path);
}
