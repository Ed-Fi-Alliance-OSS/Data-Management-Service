// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Frontend.AspNetCore.Security;
using FakeItEasy;
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
        var authority = "ValidIssuer";
        var audience = "Account";
        var claimSet = "ClaimSet01";
        var tokenId = "123455";
        var expectedClaims = new Dictionary<string, string> { { "scope", claimSet }, { "jti", tokenId } };
        var token = MockTokenProvider.GenerateJwtToken(authority, audience, expectedClaims);

        var _fakeTokenProcessor = A.Fake<ITokenProcessor>();
        A.CallTo(() => _fakeTokenProcessor.DecodeToken(token)).Returns(expectedClaims);

        ApiClientDetailsProvider _apiClientDetailsProvider = new(_fakeTokenProcessor);

        // Act
        var apiClientDetails = _apiClientDetailsProvider.RetrieveApiClientDetailsFromToken(token);

        // Assert
        A.CallTo(() => _fakeTokenProcessor.DecodeToken(token)).MustHaveHappenedOnceExactly();
        apiClientDetails.Should().NotBeNull();
        apiClientDetails.TokenId.Equals(tokenId);
        apiClientDetails.ClaimSetName.Equals(claimSet);
    }

    [Test]
    public void InvalidToken_ShouldThrowException()
    {
        // Arrange
        var token = "Invalid token";

        var _fakeTokenProcessor = A.Fake<ITokenProcessor>();
        A.CallTo(() => _fakeTokenProcessor.DecodeToken(token))
            .Throws(new ArgumentException("Invalid token format."));

        ApiClientDetailsProvider _apiClientDetailsProvider = new(_fakeTokenProcessor);

        // Act & Assert
        Assert.Throws<ArgumentException>(
            () => _apiClientDetailsProvider.RetrieveApiClientDetailsFromToken(token)
        );
    }
}
