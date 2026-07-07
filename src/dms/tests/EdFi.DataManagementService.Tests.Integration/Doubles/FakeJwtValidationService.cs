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
    public static IJwtValidationService Allowing(
        string tokenId,
        string clientId,
        IReadOnlyList<long>? educationOrganizationIds = null
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
            [],
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
