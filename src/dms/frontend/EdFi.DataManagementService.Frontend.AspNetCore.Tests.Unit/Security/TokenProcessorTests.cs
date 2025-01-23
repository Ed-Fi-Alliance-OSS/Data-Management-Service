// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Security;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit.Security;

[TestFixture]
public class TokenProcessorTests
{
    [Test]
    public void Retrieve_Expected_Scopes_From_Token()
    {
        // Arrange
        var authority = "ValidIssuer";
        var audience = "Account";
        var claimSet = "ClaimSet01";
        var tokenId = "123455";
        var expectedClaims = new Dictionary<string, string> { { "scope", claimSet }, { "jti", tokenId } };
        var token = MockTokenProvider.GenerateJwtToken(authority, audience, expectedClaims);

        var tokenProcessor = new TokenProcessor();

        // Act
        var claims = tokenProcessor.DecodeToken(token);

        // Assert
        claims.Should().NotBeNull();
        claims["scope"].Should().Be(claimSet);
        claims["jti"].Should().Be(tokenId);
    }

    [Test]
    public void InvalidToken_ShouldThrowException()
    {
        // Arrange
        var token = "Invalid token";

        var tokenProcessor = new TokenProcessor();

        // Act & Assert
        Assert.Throws<ArgumentException>(() => tokenProcessor.DecodeToken(token));
    }
}
