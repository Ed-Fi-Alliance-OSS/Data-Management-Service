// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Claims;
using EdFi.DataManagementService.Frontend.AspNetCore.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Security;

[TestFixture]
public class ApiClientDetailsProviderTests
{
    [Test]
    public void Retrieve_Expected_Scopes_From_ApiClientDetailsProvider()
    {
        // Arrange
        var claimSet = "ClaimSet01";
        var tokenId = "123455";
        var claims = new List<Claim> { new("scope", claimSet), new("jti", tokenId) };
        ApiClientDetailsProvider _apiClientDetailsProvider = new();

        // Act
        var apiClientDetails = _apiClientDetailsProvider.RetrieveApiClientDetailsFromToken(
            "token-hash",
            claims
        );

        // Assert
        apiClientDetails.Should().NotBeNull();
        apiClientDetails.TokenId.Equals(tokenId);
        apiClientDetails.ClaimSetName.Equals(claimSet);
    }

    [Test]
    public void Retrieve_Token_Hash_When_No_Jti_Scope()
    {
        // Arrange
        var claimSet = "ClaimSet01";
        var claims = new List<Claim> { new("scope", claimSet) };
        ApiClientDetailsProvider _apiClientDetailsProvider = new();

        // Act
        var apiClientDetails = _apiClientDetailsProvider.RetrieveApiClientDetailsFromToken(
            "token-hash",
            claims
        );

        // Assert
        apiClientDetails.Should().NotBeNull();
        apiClientDetails.TokenId.Equals("token-hash");
        apiClientDetails.ClaimSetName.Equals(claimSet);
    }
}
