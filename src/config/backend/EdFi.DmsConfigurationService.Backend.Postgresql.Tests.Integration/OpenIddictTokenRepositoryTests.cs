// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

[TestFixture]
public class Given_OpenIddictDataRepository_When_Storing_Token : DatabaseTestBase
{
    private readonly Guid _tokenId = Guid.NewGuid();
    private readonly Guid _applicationId = Guid.NewGuid();
    private const string Subject = "client-id";
    private const string Payload = "eyJhbGciOiJSUzI1NiJ9.eyJzdWIiOiJjbGllbnQtaWQifQ.signature";

    private TokenInfo _token = null!;

    [SetUp]
    public async Task Setup()
    {
        var repository = new OpenIddictDataRepository(Configuration.DatabaseOptions);

        await repository.StoreTokenAsync(
            _tokenId,
            _applicationId,
            Subject,
            Payload,
            DateTimeOffset.UtcNow.AddHours(1)
        );

        _token =
            await repository.GetTokenByIdAsync(_tokenId)
            ?? throw new InvalidOperationException("Stored token was not found.");
    }

    [Test]
    public void It_should_persist_the_token_payload()
    {
        _token.Payload.Should().Be(Payload);
    }
}
