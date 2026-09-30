// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.SigningKeys;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys.SigningKeyTestSupport;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.SigningKeys;

public class SigningKeySourceTests
{
    private static DatabaseSigningKeySource DatabaseSource(KeyRepositoryHarness harness) =>
        new(harness.Repository, NullLogger<DatabaseSigningKeySource>.Instance);

    [TestFixture]
    public class Given_database_rows_in_every_supported_format
    {
        private KeyRepositoryHarness _harness = null!;
        private CancellationTokenSource _source = null!;
        private SigningKeySourceResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            _harness = new KeyRepositoryHarness(NewTime());
            using RSA pkcs1 = RSA.Create(2048);
            using RSA base64 = RSA.Create(2048);
            _harness.Returns(
                KeyRepositoryHarness.Row("spki"),
                new PublicKeyInfo { KeyId = "pkcs1", PublicKey = pkcs1.ExportRSAPublicKey() },
                new PublicKeyInfo
                {
                    KeyId = "base64",
                    PublicKey = Encoding.UTF8.GetBytes(
                        Convert.ToBase64String(base64.ExportSubjectPublicKeyInfo())
                    ),
                }
            );

            _source = new CancellationTokenSource();
            _result = await DatabaseSource(_harness).LoadAsync(_source.Token);
        }

        [TearDown]
        public void TearDown() => _source.Dispose();

        [Test]
        public void It_passes_the_token_to_the_repository() =>
            _harness.Calls.Single().Token.Should().Be(_source.Token);

        [Test]
        public void It_returns_every_key_in_order() =>
            _result.Entries.Select(entry => entry.KeyId).Should().Equal("spki", "pkcs1", "base64");

        [Test]
        public void It_discards_nothing() => _result.DiscardedCount.Should().Be(0);
    }

    [TestFixture]
    public class Given_database_rows_that_cannot_be_used
    {
        private SigningKeySourceResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            KeyRepositoryHarness harness = new(NewTime());
            harness.Returns(
                KeyRepositoryHarness.Row("good"),
                KeyRepositoryHarness.Garbage("garbage"),
                new PublicKeyInfo
                {
                    KeyId = string.Empty,
                    PublicKey = KeyRepositoryHarness.Row("good").PublicKey,
                }
            );
            _result = await DatabaseSource(harness).LoadAsync(CancellationToken.None);
        }

        [Test]
        public void It_keeps_the_usable_key() =>
            _result.Entries.Select(entry => entry.KeyId).Should().Equal("good");

        // An unknown format and an entry the snapshot model refuses (empty key id) are both discarded records.
        [Test]
        public void It_counts_both_unusable_rows() => _result.DiscardedCount.Should().Be(2);
    }

    [TestFixture]
    public class Given_a_repository_that_throws
    {
        [Test]
        public async Task It_propagates_the_failure()
        {
            KeyRepositoryHarness harness = new(NewTime());
            var failure = new TimeoutException("store down");
            harness.Fails(failure);

            Func<Task> load = () => DatabaseSource(harness).LoadAsync(CancellationToken.None);

            (await load.Should().ThrowAsync<TimeoutException>()).Which.Should().BeSameAs(failure);
        }
    }

    [TestFixture]
    public class Given_a_configured_certificate_file
    {
        private string _directory = null!;
        private X509Certificate2 _certificate = null!;
        private SigningKeySourceResult _result = null!;

        [SetUp]
        public async Task Act()
        {
            _directory = Directory.CreateTempSubdirectory("dms1556-cert-").FullName;
            using RSA rsa = RSA.Create(2048);
            _certificate = new CertificateRequest(
                "CN=Configured",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1
            ).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            string path = Path.Combine(_directory, "configured.pfx");
            await File.WriteAllBytesAsync(path, _certificate.Export(X509ContentType.Pfx, "secret"));

            IOptions<IdentityOptions> options = Options.Create(
                new IdentityOptions
                {
                    UseCertificates = true,
                    CertificatePath = path,
                    CertificatePassword = "secret",
                }
            );
            using DevelopmentCertificateStore store = new(
                options,
                NullLogger<DevelopmentCertificateStore>.Instance
            );
            _result = await new CertificateSigningKeySource(options, store).LoadAsync(CancellationToken.None);
        }

        [TearDown]
        public void TearDown()
        {
            _certificate.Dispose();
            Directory.Delete(_directory, recursive: true);
        }

        [Test]
        public void It_uses_the_thumbprint_as_the_key_id() =>
            _result.Entries.Single().KeyId.Should().Be(_certificate.Thumbprint);

        [Test]
        public void It_publishes_the_certificate_public_key()
        {
            using RSA expected = _certificate.GetRSAPublicKey()!;
            _result
                .Entries.Single()
                .PublicParameters.Modulus.Should()
                .Equal(expected.ExportParameters(false).Modulus);
        }
    }

    [TestFixture]
    public class Given_certificate_mode_without_a_certificate_path
    {
        [Test]
        public async Task It_fails_the_load()
        {
            IOptions<IdentityOptions> options = Options.Create(
                new IdentityOptions { UseCertificates = true }
            );
            using DevelopmentCertificateStore store = new(
                options,
                NullLogger<DevelopmentCertificateStore>.Instance
            );

            Func<Task> load = () =>
                new CertificateSigningKeySource(options, store).LoadAsync(CancellationToken.None);

            await load.Should()
                .ThrowAsync<InvalidOperationException>()
                .WithMessage("CertificatePath must be set when not using development certificates.");
        }
    }

    // (r)
    [TestFixture]
    public class Given_a_first_start_with_concurrent_validation_and_issuance
    {
        private string _directory = null!;
        private string _path = null!;
        private ILogger<DevelopmentCertificateStore> _logger = null!;
        private List<string> _publishedKeyIds = null!;
        private List<string> _issuedThumbprints = null!;
        private bool _issuedCertificatesHavePrivateKeys;

        [SetUp]
        public async Task Act()
        {
            _directory = Directory.CreateTempSubdirectory("dms1556-devcert-").FullName;
            _path = Path.Combine(_directory, "devcert.pfx");
            IOptions<IdentityOptions> options = Options.Create(
                new IdentityOptions
                {
                    UseCertificates = true,
                    UseDevelopmentCertificates = true,
                    DevCertificatePath = _path,
                    DevCertificatePassword = "password",
                }
            );
            _logger = A.Fake<ILogger<DevelopmentCertificateStore>>();
            using DevelopmentCertificateStore store = new(options, _logger);
            CertificateSigningKeySource source = new(options, store);

            using Barrier start = new(32);
            Task<SigningKeySourceResult>[] validation =
            [
                .. Enumerable
                    .Range(0, 16)
                    .Select(_ =>
                        Task.Run(() =>
                        {
                            start.SignalAndWait();
                            return source.LoadAsync(CancellationToken.None);
                        })
                    ),
            ];
            Task<X509Certificate2>[] issuance =
            [
                .. Enumerable
                    .Range(0, 16)
                    .Select(_ =>
                        Task.Run(() =>
                        {
                            start.SignalAndWait();
                            return store.GetAsync(CancellationToken.None);
                        })
                    ),
            ];

            _publishedKeyIds =
            [
                .. (await Task.WhenAll(validation).WaitAsync(TimeSpan.FromSeconds(30))).Select(result =>
                    result.Entries.Single().KeyId
                ),
            ];
            X509Certificate2[] issued = await Task.WhenAll(issuance).WaitAsync(TimeSpan.FromSeconds(30));
            _issuedThumbprints = [.. issued.Select(certificate => certificate.Thumbprint)];
            _issuedCertificatesHavePrivateKeys = Array.TrueForAll(
                issued,
                certificate => certificate.HasPrivateKey
            );
            foreach (X509Certificate2 certificate in issued)
            {
                certificate.Dispose();
            }
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_directory, recursive: true);

        [Test]
        public void It_creates_the_certificate_once() =>
            MessagesAt(_logger, LogLevel.Information)
                .Should()
                .Equal($"Development certificate created at {_path}");

        [Test]
        public void It_leaves_only_the_certificate_file() =>
            Directory.GetFiles(_directory).Select(Path.GetFileName).Should().Equal("devcert.pfx");

        [Test]
        public void It_publishes_one_key_id_to_every_validator() =>
            _publishedKeyIds.Distinct().Should().ContainSingle();

        // I-10: whatever issuance signs with is the certificate validation publishes.
        [Test]
        public void It_issues_with_the_published_certificate() =>
            _issuedThumbprints.Should().OnlyContain(thumbprint => thumbprint == _publishedKeyIds[0]);

        [Test]
        public void It_gives_issuance_the_private_key() =>
            _issuedCertificatesHavePrivateKeys.Should().BeTrue();

        [Test]
        public void It_writes_a_loadable_certificate()
        {
            using X509Certificate2 onDisk = X509CertificateLoader.LoadPkcs12FromFile(_path, "password");
            onDisk.Thumbprint.Should().Be(_publishedKeyIds[0]);
        }
    }

    [TestFixture]
    public class Given_an_existing_development_certificate
    {
        private string _directory = null!;
        private string _expectedThumbprint = null!;
        private string _thumbprint = null!;
        private ILogger<DevelopmentCertificateStore> _logger = null!;

        [SetUp]
        public async Task Act()
        {
            _directory = Directory.CreateTempSubdirectory("dms1556-devcert-").FullName;
            string path = Path.Combine(_directory, "devcert.pfx");
            using (RSA rsa = RSA.Create(2048))
            using (
                X509Certificate2 existing = new CertificateRequest(
                    "CN=DevCert",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1
                ).CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1))
            )
            {
                _expectedThumbprint = existing.Thumbprint;
                await File.WriteAllBytesAsync(path, existing.Export(X509ContentType.Pfx, "password"));
            }

            _logger = A.Fake<ILogger<DevelopmentCertificateStore>>();
            using DevelopmentCertificateStore store = new(
                Options.Create(
                    new IdentityOptions { DevCertificatePath = path, DevCertificatePassword = "password" }
                ),
                _logger
            );
            using X509Certificate2 loaded = await store.GetAsync(CancellationToken.None);
            _thumbprint = loaded.Thumbprint;
        }

        [TearDown]
        public void TearDown() => Directory.Delete(_directory, recursive: true);

        [Test]
        public void It_loads_the_existing_certificate() => _thumbprint.Should().Be(_expectedThumbprint);

        [Test]
        public void It_does_not_create_a_new_one() =>
            MessagesAt(_logger, LogLevel.Information).Should().BeEmpty();
    }
}
