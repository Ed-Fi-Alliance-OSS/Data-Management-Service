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
using PostgresqlTokenRepository = EdFi.DmsConfigurationService.Backend.Postgresql.OpenIddict.Repositories.OpenIddictTokenRepository;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// The shipped token repository offers the credential read exactly when the data repository it
/// wraps does, and the token manager takes its single full lookup when it does not.
/// </summary>
public class OpenIddictTokenRepositoryCredentialTests
{
    private const string ClientId = "credential-client";
    private const string ClientSecret = "plain-secret";
    private const string StoredSecret = "hashed-secret";
    private const string EncryptionKey = "wrapper-test-encryption-key";

    /// <summary>A data repository fake that also implements the credential read.</summary>
    private static IOpenIddictDataRepository CredentialReadingDataRepository(bool canReadClientCredentials)
    {
        IOpenIddictDataRepository dataRepository = A.Fake<IOpenIddictDataRepository>(options =>
            options.Implements<IClientCredentialReader>()
        );
        A.CallTo(() => ((IClientCredentialReader)dataRepository).CanReadClientCredentials)
            .Returns(canReadClientCredentials);
        return dataRepository;
    }

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
                    A<int>._
                )
            )
            .Returns(TokenStoreOutcome.Stored);
        return applicationId;
    }

    private static Task<TokenResult> GrantThroughAsync(IOpenIddictTokenRepository tokenRepository)
    {
        IClientSecretHasher secretHasher = A.Fake<IClientSecretHasher>();
        A.CallTo(() => secretHasher.VerifySecretAsync(ClientSecret, StoredSecret)).Returns(true);
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
            )
        );
        return manager.GetAccessTokenAsync([new("client_id", ClientId), new("client_secret", ClientSecret)]);
    }

    [TestFixture]
    public class Given_a_postgresql_token_repository_over_a_data_repository_that_reads_credentials
        : OpenIddictTokenRepositoryCredentialTests
    {
        private IOpenIddictDataRepository _dataRepository = null!;
        private ClientCredential _stored = null!;
        private bool _canRead;
        private ClientCredential? _forwarded;
        private ClientCredential? _forwardedAbsence;

        [SetUp]
        public async Task Setup()
        {
            _dataRepository = CredentialReadingDataRepository(canReadClientCredentials: true);
            var reader = (IClientCredentialReader)_dataRepository;
            _stored = new ClientCredential(Guid.NewGuid(), ClientId, StoredSecret, IsApproved: true);
            A.CallTo(() => reader.GetClientCredentialAsync(ClientId)).Returns(_stored);
            A.CallTo(() => reader.GetClientCredentialAsync("absent-client")).Returns((ClientCredential?)null);
            PostgresqlTokenRepository tokenRepository = new(_dataRepository);

            _canRead = tokenRepository.CanReadClientCredentials;
            _forwarded = await tokenRepository.GetClientCredentialAsync(ClientId);
            _forwardedAbsence = await tokenRepository.GetClientCredentialAsync("absent-client");
        }

        [Test]
        public void It_reports_that_it_reads_credentials() => _canRead.Should().BeTrue();

        [Test]
        public void It_returns_the_wrapped_repositorys_credential() => _forwarded.Should().BeSameAs(_stored);

        [Test]
        public void It_forwards_an_absent_client_as_null() => _forwardedAbsence.Should().BeNull();

        [Test]
        public void It_reads_each_credential_once() =>
            A.CallTo(() => ((IClientCredentialReader)_dataRepository).GetClientCredentialAsync(ClientId))
                .MustHaveHappenedOnceExactly();
    }

    [TestFixture("legacy-only")]
    [TestFixture("reports-it-cannot-read")]
    public class Given_a_postgresql_token_repository_over_a_data_repository_that_cannot_read_credentials(
        string dataRepositoryKind
    ) : OpenIddictTokenRepositoryCredentialTests
    {
        private IOpenIddictDataRepository _dataRepository = null!;
        private PostgresqlTokenRepository _tokenRepository = null!;

        [SetUp]
        public void Setup()
        {
            _dataRepository =
                dataRepositoryKind == "legacy-only"
                    ? A.Fake<IOpenIddictDataRepository>()
                    : CredentialReadingDataRepository(canReadClientCredentials: false);
            _tokenRepository = new PostgresqlTokenRepository(_dataRepository);
        }

        [Test]
        public void It_reports_that_it_does_not_read_credentials() =>
            _tokenRepository.CanReadClientCredentials.Should().BeFalse();

        [Test]
        public async Task It_refuses_a_direct_credential_read_as_unsupported() =>
            await _tokenRepository
                .Invoking(repository => repository.GetClientCredentialAsync(ClientId))
                .Should()
                .ThrowAsync<NotSupportedException>();
    }

    // The manager's choice follows the wrapped data repository: over a legacy-only one it takes
    // the single full lookup and issues from it; over a reader it reads the credential first.
    [TestFixture]
    public class Given_a_token_grant_through_a_postgresql_token_repository_over_a_legacy_data_repository
        : OpenIddictTokenRepositoryCredentialTests
    {
        private IOpenIddictDataRepository _dataRepository = null!;
        private TokenResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _dataRepository = A.Fake<IOpenIddictDataRepository>();
            ArrangeGrantableClient(_dataRepository);

            _result = await GrantThroughAsync(new PostgresqlTokenRepository(_dataRepository));
        }

        [Test]
        public void It_issues_a_token() => _result.Should().BeOfType<TokenResult.Success>();

        [Test]
        public void It_loads_the_application_exactly_once() =>
            A.CallTo(() => _dataRepository.GetApplicationByClientIdAsync(ClientId))
                .MustHaveHappenedOnceExactly();
    }

    [TestFixture]
    public class Given_a_token_grant_through_a_postgresql_token_repository_over_a_credential_reading_data_repository
        : OpenIddictTokenRepositoryCredentialTests
    {
        private IOpenIddictDataRepository _dataRepository = null!;
        private TokenResult _result = null!;

        [SetUp]
        public async Task Setup()
        {
            _dataRepository = CredentialReadingDataRepository(canReadClientCredentials: true);
            Guid applicationId = ArrangeGrantableClient(_dataRepository);
            A.CallTo(() => ((IClientCredentialReader)_dataRepository).GetClientCredentialAsync(ClientId))
                .Returns(new ClientCredential(applicationId, ClientId, StoredSecret, IsApproved: true));

            _result = await GrantThroughAsync(new PostgresqlTokenRepository(_dataRepository));
        }

        [Test]
        public void It_issues_a_token() => _result.Should().BeOfType<TokenResult.Success>();

        [Test]
        public void It_reads_the_credential_and_then_the_application_once_each() =>
            A.CallTo(() => ((IClientCredentialReader)_dataRepository).GetClientCredentialAsync(ClientId))
                .MustHaveHappenedOnceExactly()
                .Then(
                    A.CallTo(() => _dataRepository.GetApplicationByClientIdAsync(ClientId))
                        .MustHaveHappenedOnceExactly()
                );
    }
}
