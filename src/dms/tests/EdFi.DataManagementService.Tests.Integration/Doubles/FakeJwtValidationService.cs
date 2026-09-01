// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using FakeItEasy;

namespace EdFi.DataManagementService.Tests.Integration.Doubles;

/// <summary>
/// Builds a JWT validation service stub that unconditionally returns the same principal
/// and <see cref="ClientAuthorizations"/> for every token. The returned authorization
/// references the smoke claim set name and the single stable DMS instance id.
/// </summary>
internal static class FakeJwtValidationService
{
    /// <param name="namespacePrefixes">
    /// Namespace prefixes carried on the returned <see cref="ClientAuthorizations"/> for NamespaceBased
    /// authorization scenarios. Defaults to none, which is the historical behavior.
    /// </param>
    public static IJwtValidationService Allowing(
        string tokenId,
        string clientId,
        IReadOnlyList<long>? educationOrganizationIds = null,
        IReadOnlyList<string>? namespacePrefixes = null
    )
    {
        var fake = A.Fake<IJwtValidationService>();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("client_id", clientId)], "test"));
        var authorizations = new ClientAuthorizations(
            tokenId,
            clientId,
            ExternalDoublesConstants.SmokeClaimSetName,
            educationOrganizationIds is null
                ? []
                : [.. educationOrganizationIds.Select(static id => new EducationOrganizationId(id))],
            namespacePrefixes is null
                ? []
                : [.. namespacePrefixes.Select(static prefix => new NamespacePrefix(prefix))],
            [new DataStoreId(ExternalDoublesConstants.StableDataStoreId)]
        );

        A.CallTo(() => fake.ValidateAndExtractClientAuthorizationsAsync(A<string>._, A<CancellationToken>._))
            .Returns(Task.FromResult(((ClaimsPrincipal?)principal, (ClientAuthorizations?)authorizations)));

        A.CallTo(() =>
                fake.ValidateAndExtractClientAuthorizationsAsync(
                    A<string>._,
                    A<int>._,
                    A<CancellationToken>._
                )
            )
            .Returns(Task.FromResult(((ClaimsPrincipal?)principal, (ClientAuthorizations?)authorizations)));

        return fake;
    }
}

/// <summary>
/// The same stub with the caller's namespace prefixes held behind a volatile reference a test can
/// replace between requests.
/// </summary>
/// <remarks>
/// A namespace-authorized fixture cannot create a row the caller may not read, because the write is
/// authorized too. Widening the caller for the seed and narrowing it for the assertions is what makes
/// an unauthorized row exist at all - and without one, a filtered read returning nothing proves
/// nothing, since an ordinary query filter would return nothing either.
/// </remarks>
public sealed class MutableNamespacePrefixJwtValidationService : IJwtValidationService
{
    private readonly string _tokenId;
    private readonly string _clientId;
    private readonly IReadOnlyList<long> _educationOrganizationIds;
    private IReadOnlyList<string> _namespacePrefixes;

    public MutableNamespacePrefixJwtValidationService(
        string tokenId,
        string clientId,
        IReadOnlyList<long> educationOrganizationIds,
        IReadOnlyList<string> namespacePrefixes
    )
    {
        _tokenId = tokenId;
        _clientId = clientId;
        _educationOrganizationIds = educationOrganizationIds;
        _namespacePrefixes = namespacePrefixes;
    }

    /// <summary>Replaces the prefixes every later request's authorization is built from.</summary>
    public void SetNamespacePrefixes(IReadOnlyList<string> namespacePrefixes) =>
        Volatile.Write(ref _namespacePrefixes, namespacePrefixes);

    public Task<(ClaimsPrincipal?, ClientAuthorizations?)> ValidateAndExtractClientAuthorizationsAsync(
        string token,
        CancellationToken cancellationToken
    ) => Task.FromResult(Current());

    public Task<(ClaimsPrincipal?, ClientAuthorizations?)> ValidateAndExtractClientAuthorizationsAsync(
        string authorizationHeader,
        int tokenStartIndex,
        CancellationToken cancellationToken
    ) => Task.FromResult(Current());

    private (ClaimsPrincipal?, ClientAuthorizations?) Current()
    {
        ClaimsPrincipal principal = new(new ClaimsIdentity([new Claim("client_id", _clientId)], "test"));

        ClientAuthorizations authorizations = new(
            _tokenId,
            _clientId,
            ExternalDoublesConstants.SmokeClaimSetName,
            [.. _educationOrganizationIds.Select(static id => new EducationOrganizationId(id))],
            [.. Volatile.Read(ref _namespacePrefixes).Select(static prefix => new NamespacePrefix(prefix))],
            [new DataStoreId(ExternalDoublesConstants.StableDataStoreId)]
        );

        return (principal, authorizations);
    }
}
