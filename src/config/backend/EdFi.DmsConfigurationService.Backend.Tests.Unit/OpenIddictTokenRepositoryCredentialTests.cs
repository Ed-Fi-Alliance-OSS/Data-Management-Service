// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Repositories;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using EdFi.DmsConfigurationService.Secrets;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MssqlTokenRepository = EdFi.DmsConfigurationService.Backend.Mssql.OpenIddict.Repositories.OpenIddictTokenRepository;
using PostgresqlTokenRepository = EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories.OpenIddictTokenRepository;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// Each engine's shipped token repository forwards the credential read to the data repository it
/// wraps, and a grant through it reads the credential before the full application.
/// </summary>
public class OpenIddictTokenRepositoryCredentialTests
{
    private const string ClientId = "credential-client";
    private const string ClientSecret = "plain-secret";
    private const string StoredSecret = "hashed-secret";
    private const string EncryptionKey = "wrapper-test-encryption-key";

    /// <summary>The shipped token repository of <paramref name="engine"/> over <paramref name="dataRepository"/>.</summary>
    private static IOpenIddictTokenRepository TokenRepositoryOver(
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
    public class Given_a_token_repository_over_a_data_repository(string engine)
        : OpenIddictTokenRepositoryCredentialTests
    {
        private IOpenIddictDataRepository _dataRepository = null!;
        private ClientCredential _stored = null!;
        private ClientCredential? _forwarded;
        private ClientCredential? _forwardedAbsence;

        [SetUp]
        public async Task Setup()
        {
            _dataRepository = A.Fake<IOpenIddictDataRepository>();
            _stored = new ClientCredential(Guid.NewGuid(), ClientId, StoredSecret, IsApproved: true);
            A.CallTo(() => _dataRepository.GetClientCredentialAsync(ClientId)).Returns(_stored);
            A.CallTo(() => _dataRepository.GetClientCredentialAsync("absent-client"))
                .Returns((ClientCredential?)null);
            IOpenIddictTokenRepository tokenRepository = TokenRepositoryOver(engine, _dataRepository);

            _forwarded = await tokenRepository.GetClientCredentialAsync(ClientId);
            _forwardedAbsence = await tokenRepository.GetClientCredentialAsync("absent-client");
        }

        [Test]
        public void It_returns_the_wrapped_repositorys_credential() => _forwarded.Should().BeSameAs(_stored);

        [Test]
        public void It_forwards_an_absent_client_as_null() => _forwardedAbsence.Should().BeNull();

        [Test]
        public void It_reads_each_credential_once() =>
            A.CallTo(() => _dataRepository.GetClientCredentialAsync(ClientId)).MustHaveHappenedOnceExactly();
    }

    [TestFixture("postgresql")]
    [TestFixture("mssql")]
    public class Given_a_token_grant_through_a_token_repository(string engine)
        : OpenIddictTokenRepositoryCredentialTests
    {
        private IOpenIddictDataRepository _dataRepository = null!;
        private TokenResult _result = null!;

        /// <summary>
        /// Arranges an approved client on <paramref name="dataRepository"/>, with a signing key, so a
        /// grant through the token manager can succeed. Returns the application id.
        /// </summary>
        private static Guid ArrangeGrantableClient(IOpenIddictDataRepository dataRepository)
        {
            Guid applicationId = Guid.NewGuid();
            A.CallTo(() => dataRepository.GetApplicationByClientIdAsync(ClientId))
                .Returns(
                    new ApplicationInfo
                    {
                        Id = applicationId,
                        ClientId = ClientId,
                        ClientSecret = StoredSecret,
                        IsApproved = true,
                        ProtocolMappers = "[]",
                    }
                );
            using RSA rsa = RSA.Create(2048);
            A.CallTo(() => dataRepository.GetActivePrivateKeyInternalAsync(EncryptionKey))
                .Returns((Convert.ToBase64String(rsa.ExportPkcs8PrivateKey()), "wrapper-key"));
            A.CallTo(() => dataRepository.GetClientRolesAsync(applicationId)).Returns([]);
            A.CallTo(() =>
                    dataRepository.StoreTokenAsync(
                        A<Guid>._,
                        A<Guid>._,
                        A<string>._,
                        A<DateTimeOffset>._,
                        A<int>._,
                        A<string>._
                    )
                )
                .Returns(TokenStoreOutcome.Stored);
            return applicationId;
        }

        private static async Task<TokenResult> GrantThroughAsync(IOpenIddictTokenRepository tokenRepository)
        {
            IClientSecretHasher secretHasher = A.Fake<IClientSecretHasher>();
            A.CallTo(() => secretHasher.VerifySecretAsync(ClientSecret, StoredSecret)).Returns(true);
            A.CallTo(() => secretHasher.HashSecretAsync(A<string>._)).Returns("wrapper-placeholder");
            ClientSecretPlaceholderHash placeholderHash = new(secretHasher);
            await placeholderHash.InitializeAsync();
            IdentityOptions options = new()
            {
                Authority = "https://cms.example.test",
                Audience = "wrapper-tests",
                EncryptionKey = EncryptionKey,
            };
            OpenIddictTokenManager manager = new(
                Options.Create(options),
                NullLogger<OpenIddictTokenManager>.Instance,
                secretHasher,
                tokenRepository,
                A.Fake<ISigningKeySnapshotProvider>(),
                new DevelopmentCertificateStore(
                    Options.Create(options),
                    NullLogger<DevelopmentCertificateStore>.Instance
                ),
                placeholderHash
            );
            return await manager.GetAccessTokenAsync([
                new("client_id", ClientId),
                new("client_secret", ClientSecret),
            ]);
        }

        [SetUp]
        public async Task Setup()
        {
            _dataRepository = A.Fake<IOpenIddictDataRepository>();
            Guid applicationId = ArrangeGrantableClient(_dataRepository);
            A.CallTo(() => _dataRepository.GetClientCredentialAsync(ClientId))
                .Returns(new ClientCredential(applicationId, ClientId, StoredSecret, IsApproved: true));

            _result = await GrantThroughAsync(TokenRepositoryOver(engine, _dataRepository));
        }

        [Test]
        public void It_issues_a_token() => _result.Should().BeOfType<TokenResult.Success>();

        [Test]
        public void It_reads_the_credential_and_then_the_application_once_each() =>
            A.CallTo(() => _dataRepository.GetClientCredentialAsync(ClientId))
                .MustHaveHappenedOnceExactly()
                .Then(
                    A.CallTo(() => _dataRepository.GetApplicationByClientIdAsync(ClientId))
                        .MustHaveHappenedOnceExactly()
                );
    }
}
