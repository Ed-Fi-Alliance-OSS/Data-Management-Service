// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Dapper;
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

    private bool _payloadWasStored;

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

        await using var connection = await DataSource!.OpenConnectionAsync();
        _payloadWasStored = await connection.QuerySingleAsync<bool>(
            @"SELECT ""Payload"" IS NOT NULL
              FROM ""dmscs"".""OpenIddictToken""
              WHERE ""Id"" = @TokenId",
            new { TokenId = _tokenId }
        );
    }

    [Test]
    public void It_should_not_persist_the_token_payload()
    {
        _payloadWasStored.Should().BeFalse();
    }
}
