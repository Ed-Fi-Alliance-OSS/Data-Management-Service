// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using FakeItEasy;
using FluentAssertions;
using MssqlTokenRepository = EdFi.DmsConfigurationService.Backend.Mssql.OpenIddict.Repositories.OpenIddictTokenRepository;
using PostgresqlTokenRepository = EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories.OpenIddictTokenRepository;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

/// <summary>
/// Both engines' token repositories hand the caller's cancellation token to the data repository's public-key read
/// (spec D-8), and the existing parameterless read stays uncancellable.
/// </summary>
public class OpenIddictTokenRepositoryCancellationTests
{
    private static readonly (string KeyId, byte[] PublicKey)[] _rows = [("key-1", [1, 2, 3])];

    private static IOpenIddictDataRepository DataRepository()
    {
        var dataRepository = A.Fake<IOpenIddictDataRepository>();
        A.CallTo(() => dataRepository.GetActivePublicKeysInternalAsync(A<CancellationToken>._))
            .Returns(_rows);
        return dataRepository;
    }

    private static IOpenIddictTokenRepository Create(
        string engine,
        IOpenIddictDataRepository dataRepository
    ) =>
        engine switch
        {
            "postgresql" => new PostgresqlTokenRepository(dataRepository),
            "mssql" => new MssqlTokenRepository(dataRepository),
            _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, null),
        };

    [TestFixture("postgresql")]
    [TestFixture("mssql")]
    public class Given_a_cancellation_token(string engine)
    {
        private CancellationTokenSource _source = null!;
        private IOpenIddictDataRepository _dataRepository = null!;
        private List<PublicKeyInfo> _keys = null!;

        [SetUp]
        public async Task Act()
        {
            // A new source per test: TearDown disposes it, and NUnit reuses the fixture instance across tests.
            _source = new CancellationTokenSource();
            _dataRepository = DataRepository();
            _keys = [.. await Create(engine, _dataRepository).GetActivePublicKeysAsync(_source.Token)];
        }

        [TearDown]
        public void TearDown() => _source.Dispose();

        [Test]
        public void It_passes_the_token_to_the_data_repository() =>
            A.CallTo(() => _dataRepository.GetActivePublicKeysInternalAsync(_source.Token))
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_maps_the_rows_as_before()
        {
            _keys.Select(key => key.KeyId).Should().Equal("key-1");
            _keys[0].PublicKey.Should().Equal(1, 2, 3);
        }
    }

    [TestFixture("postgresql")]
    [TestFixture("mssql")]
    public class Given_the_existing_parameterless_call(string engine)
    {
        private IOpenIddictDataRepository _dataRepository = null!;
        private List<PublicKeyInfo> _keys = null!;

        [SetUp]
        public async Task Act()
        {
            _dataRepository = DataRepository();
            _keys = [.. await Create(engine, _dataRepository).GetActivePublicKeysAsync()];
        }

        [Test]
        public void It_reads_without_a_cancellation_token() =>
            A.CallTo(() => _dataRepository.GetActivePublicKeysInternalAsync(CancellationToken.None))
                .MustHaveHappenedOnceExactly();

        [Test]
        public void It_returns_the_same_keys() => _keys.Select(key => key.KeyId).Should().Equal("key-1");
    }
}
