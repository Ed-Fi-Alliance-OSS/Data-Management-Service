// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

/// <summary>
/// Covers the derivative half of the Configuration Service data-store projection: deserialization of
/// the derivative collection, per-derivative decryption inside its own fault boundary, and the states
/// that mean a derivative is not configured.
/// </summary>
public class ConfigurationServiceDataStoreProviderDerivativeTests
{
    private const string TestEncryptionKey = "TestEncryptionKey123456789012345678901234567890";
    private const string OtherEncryptionKey = "OtherEncryptionKey09876543210987654321098765";

    private const string PrimaryConnectionString = "host=primary;port=5432;database=edfi;";
    private const string SnapshotConnectionString = "host=snapshot;port=5432;database=edfi;";
    private const string ReplicaConnectionString = "host=replica;port=5432;database=edfi;";

    /// <summary>
    /// Mirrors the CMS ConnectionStringEncryptionService.Encrypt() method exactly.
    /// </summary>
    private static string EncryptToBase64(string plainText, string encryptionKey)
    {
        byte[] keyBytes = Encoding.UTF8.GetBytes(encryptionKey.PadRight(32, '0')[..32]);
        using var aes = Aes.Create();
        aes.Key = keyBytes;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
        byte[] cipherBytes = encryptor.TransformFinalBlock(plainBytes, 0, plainBytes.Length);

        byte[] result = new byte[aes.IV.Length + cipherBytes.Length];
        Buffer.BlockCopy(aes.IV, 0, result, 0, aes.IV.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, aes.IV.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    private static object Derivative(string derivativeType, string? connectionString) =>
        new
        {
            Id = 10L,
            DataStoreId = 1L,
            DerivativeType = derivativeType,
            ConnectionString = connectionString,
        };

    private static object DataStore(
        long id,
        string name,
        string? encryptedConnectionString,
        object[] derivatives
    ) =>
        new
        {
            Id = id,
            DataStoreType = "Production",
            Name = name,
            ConnectionString = encryptedConnectionString,
            DataStoreContexts = Array.Empty<object>(),
            DataStoreDerivatives = derivatives,
        };

    private static ConfigurationServiceDataStoreProvider CreateProvider(
        object dataStoresResponse,
        ILogger<ConfigurationServiceDataStoreProvider> logger,
        IConnectionStringDecryptionService? decryptionService = null
    )
    {
        var tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
        A.CallTo(() =>
                tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._)
            )
            .Returns("valid-token");

        var handler = new DerivativeTestHttpMessageHandler();
        handler.SetResponse("v3/dataStores/", dataStoresResponse);

        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://api.example.com/") };

        return new ConfigurationServiceDataStoreProvider(
            new ConfigurationServiceApiClient(httpClient),
            tokenHandler,
            new ConfigurationServiceContext("clientId", "secret", "scope"),
            logger,
            decryptionService ?? new ConnectionStringDecryptionService(TestEncryptionKey)
        );
    }

    private static IReadOnlyList<string> ErrorMessages(
        RecordingLogger<ConfigurationServiceDataStoreProvider> logger
    ) => [.. logger.Records.Where(record => record.Level == LogLevel.Error).Select(record => record.Message)];

    /// <summary>
    /// Every rendered log line and every structured property value, so a secret cannot hide in either.
    /// </summary>
    private static IReadOnlyList<string> AllLoggedText(
        RecordingLogger<ConfigurationServiceDataStoreProvider> logger
    ) =>
        [
            .. logger.Records.Select(record => record.Message),
            .. logger.Records.SelectMany(record =>
                record.Properties.Values.Select(value => value?.ToString() ?? string.Empty)
            ),
            .. logger.Records.Select(record => record.Exception?.ToString() ?? string.Empty),
        ];

    [TestFixture]
    public class Given_DataStores_With_Configured_Derivatives
    {
        private RecordingLogger<ConfigurationServiceDataStoreProvider> _logger = null!;
        private IList<DataStore> _loaded = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger<ConfigurationServiceDataStoreProvider>();

            object response = new[]
            {
                DataStore(
                    1,
                    "With Both",
                    EncryptToBase64(PrimaryConnectionString, TestEncryptionKey),
                    [
                        Derivative("Snapshot", EncryptToBase64(SnapshotConnectionString, TestEncryptionKey)),
                        Derivative(
                            "ReadReplica",
                            EncryptToBase64(ReplicaConnectionString, TestEncryptionKey)
                        ),
                    ]
                ),
                DataStore(
                    2,
                    "With None",
                    EncryptToBase64("host=other;database=edfi;", TestEncryptionKey),
                    []
                ),
            };

            _loaded = await CreateProvider(response, _logger).LoadDataStores();
        }

        [Test]
        public void It_should_load_both_data_stores()
        {
            _loaded.Should().HaveCount(2);
        }

        [Test]
        public void It_should_decrypt_the_snapshot_connection_string()
        {
            _loaded[0]
                .TryGetDerivative(DataStoreDerivativeType.Snapshot, out string? connectionString)
                .Should()
                .BeTrue();

            connectionString.Should().Be(SnapshotConnectionString);
        }

        [Test]
        public void It_should_decrypt_the_read_replica_connection_string()
        {
            _loaded[0]
                .TryGetDerivative(DataStoreDerivativeType.ReadReplica, out string? connectionString)
                .Should()
                .BeTrue();

            connectionString.Should().Be(ReplicaConnectionString);
        }

        [Test]
        public void It_should_leave_a_data_store_without_derivatives_empty()
        {
            _loaded[1].Derivatives.Should().BeEmpty();
        }

        [Test]
        public void It_should_not_log_an_error()
        {
            ErrorMessages(_logger).Should().BeEmpty();
        }

        [Test]
        public void It_should_never_log_a_connection_string()
        {
            AllLoggedText(_logger)
                .Should()
                .NotContain(text =>
                    text.Contains(SnapshotConnectionString, StringComparison.Ordinal)
                    || text.Contains(ReplicaConnectionString, StringComparison.Ordinal)
                    || text.Contains(PrimaryConnectionString, StringComparison.Ordinal)
                );
        }
    }

    /// <summary>
    /// A missing row and a null, empty, or whitespace stored connection string all mean the derivative
    /// is not configured. That is an ordinary state rather than a defect, so it must stay silent and
    /// therefore distinguishable from the undecryptable case.
    /// </summary>
    [TestFixture]
    public class Given_A_Derivative_With_A_Blank_Connection_String
    {
        private RecordingLogger<ConfigurationServiceDataStoreProvider> _logger = null!;
        private IList<DataStore> _loaded = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger<ConfigurationServiceDataStoreProvider>();

            object response = new[]
            {
                DataStore(
                    1,
                    "Blank Derivatives",
                    EncryptToBase64(PrimaryConnectionString, TestEncryptionKey),
                    [Derivative("Snapshot", null), Derivative("ReadReplica", "   ")]
                ),
            };

            _loaded = await CreateProvider(response, _logger).LoadDataStores();
        }

        [Test]
        public void It_should_still_load_the_parent_data_store()
        {
            _loaded.Should().ContainSingle();
            _loaded[0].ConnectionString.Should().Be(PrimaryConnectionString);
        }

        [Test]
        public void It_should_treat_both_derivatives_as_not_configured()
        {
            _loaded[0].Derivatives.Should().BeEmpty();
        }

        [Test]
        public void It_should_not_log_an_error_for_an_unconfigured_derivative()
        {
            ErrorMessages(_logger).Should().BeEmpty();
        }
    }

    /// <summary>
    /// Each derivative is decrypted in its own fault boundary, so one unreadable optional row cannot
    /// take down its parent, its sibling derivative, or another data store in the same response.
    /// </summary>
    [TestFixture]
    public class Given_An_Undecryptable_Derivative_Connection_String
    {
        private static readonly string _invalidBase64 = "not-valid-base64!!!";
        private static readonly string _shorterThanIv = Convert.ToBase64String(new byte[4]);

        private RecordingLogger<ConfigurationServiceDataStoreProvider> _logger = null!;
        private IList<DataStore> _loaded = null!;
        private string _wrongKeyPayload = string.Empty;

        /// <summary>
        /// Produces a payload encrypted under a different key that provably fails to decrypt under the
        /// test key. AES-CBC padding validation rejects a wrong key nearly always but not certainly, so
        /// the sample is regenerated until it genuinely fails rather than left to chance.
        /// </summary>
        private static string EncryptedUnderADifferentKey(string plainText)
        {
            ConnectionStringDecryptionService service = new(TestEncryptionKey);

            for (int attempt = 0; attempt < 64; attempt++)
            {
                string candidate = EncryptToBase64(plainText, OtherEncryptionKey);

                try
                {
                    service.DecryptFromBase64(candidate);
                }
                catch (InvalidOperationException)
                {
                    return candidate;
                }
            }

            throw new AssertionException(
                "Could not produce a wrong-key payload that fails to decrypt after 64 attempts."
            );
        }

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger<ConfigurationServiceDataStoreProvider>();
            _wrongKeyPayload = EncryptedUnderADifferentKey(SnapshotConnectionString);

            object response = new[]
            {
                DataStore(
                    1,
                    "Invalid Base64 Snapshot",
                    EncryptToBase64(PrimaryConnectionString, TestEncryptionKey),
                    [
                        Derivative("Snapshot", _invalidBase64),
                        Derivative(
                            "ReadReplica",
                            EncryptToBase64(ReplicaConnectionString, TestEncryptionKey)
                        ),
                    ]
                ),
                DataStore(
                    2,
                    "Undersized Snapshot",
                    EncryptToBase64(PrimaryConnectionString, TestEncryptionKey),
                    [Derivative("Snapshot", _shorterThanIv)]
                ),
                DataStore(
                    3,
                    "Wrong Key Snapshot",
                    EncryptToBase64(PrimaryConnectionString, TestEncryptionKey),
                    [Derivative("Snapshot", _wrongKeyPayload)]
                ),
                DataStore(
                    4,
                    "Healthy Neighbor",
                    EncryptToBase64(PrimaryConnectionString, TestEncryptionKey),
                    [Derivative("Snapshot", EncryptToBase64(SnapshotConnectionString, TestEncryptionKey))]
                ),
            };

            _loaded = await CreateProvider(response, _logger).LoadDataStores();
        }

        [Test]
        public void It_should_load_every_data_store_in_the_response()
        {
            _loaded.Should().HaveCount(4);
        }

        [Test]
        public void It_should_treat_each_undecryptable_derivative_as_not_configured()
        {
            _loaded[0].Derivatives.Should().NotContainKey(DataStoreDerivativeType.Snapshot);
            _loaded[1].Derivatives.Should().BeEmpty();
            _loaded[2].Derivatives.Should().BeEmpty();
        }

        [Test]
        public void It_should_keep_the_parent_primary_connection_string()
        {
            _loaded[0].ConnectionString.Should().Be(PrimaryConnectionString);
        }

        [Test]
        public void It_should_keep_the_healthy_sibling_derivative()
        {
            _loaded[0]
                .TryGetDerivative(DataStoreDerivativeType.ReadReplica, out string? connectionString)
                .Should()
                .BeTrue();

            connectionString.Should().Be(ReplicaConnectionString);
        }

        [Test]
        public void It_should_keep_another_data_stores_derivative()
        {
            _loaded[3]
                .TryGetDerivative(DataStoreDerivativeType.Snapshot, out string? connectionString)
                .Should()
                .BeTrue();

            connectionString.Should().Be(SnapshotConnectionString);
        }

        [Test]
        public void It_should_log_one_error_for_each_undecryptable_derivative()
        {
            ErrorMessages(_logger).Should().HaveCount(3);
        }

        [Test]
        public void It_should_identify_the_derivative_type_and_parent_in_the_error()
        {
            ErrorMessages(_logger)
                .Should()
                .AllSatisfy(message => message.Should().Contain("Snapshot").And.Contain("derivative"));
        }

        [Test]
        public void It_should_never_log_ciphertext_plaintext_or_the_encryption_key()
        {
            AllLoggedText(_logger)
                .Should()
                .NotContain(text =>
                    text.Contains(_invalidBase64, StringComparison.Ordinal)
                    || text.Contains(_shorterThanIv, StringComparison.Ordinal)
                    || text.Contains(_wrongKeyPayload, StringComparison.Ordinal)
                    || text.Contains(SnapshotConnectionString, StringComparison.Ordinal)
                    || text.Contains(PrimaryConnectionString, StringComparison.Ordinal)
                    || text.Contains(TestEncryptionKey, StringComparison.Ordinal)
                );
        }
    }

    /// <summary>
    /// A decrypted, non-blank value the provider would reject is not one of the not-configured states.
    /// DMS cannot know it is malformed without asking a provider, so it stays configured, and the
    /// failure surfaces later at connection acquisition instead of being silently reinterpreted here.
    /// </summary>
    [TestFixture]
    public class Given_A_Derivative_That_Decrypts_To_A_Provider_Invalid_Value
    {
        private const string ProviderInvalidValue = "this is not a connection string at all";

        private RecordingLogger<ConfigurationServiceDataStoreProvider> _logger = null!;
        private IList<DataStore> _loaded = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger<ConfigurationServiceDataStoreProvider>();

            object response = new[]
            {
                DataStore(
                    1,
                    "Provider Invalid Snapshot",
                    EncryptToBase64(PrimaryConnectionString, TestEncryptionKey),
                    [Derivative("Snapshot", EncryptToBase64(ProviderInvalidValue, TestEncryptionKey))]
                ),
            };

            _loaded = await CreateProvider(response, _logger).LoadDataStores();
        }

        [Test]
        public void It_should_keep_the_derivative_configured()
        {
            _loaded[0]
                .TryGetDerivative(DataStoreDerivativeType.Snapshot, out string? connectionString)
                .Should()
                .BeTrue();

            connectionString.Should().Be(ProviderInvalidValue);
        }

        [Test]
        public void It_should_not_normalize_or_rewrite_the_decrypted_value()
        {
            _loaded[0].Derivatives[DataStoreDerivativeType.Snapshot].Should().Be(ProviderInvalidValue);
        }

        [Test]
        public void It_should_not_log_an_error()
        {
            ErrorMessages(_logger).Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_A_Derivative_With_An_Unrecognized_Type
    {
        private RecordingLogger<ConfigurationServiceDataStoreProvider> _logger = null!;
        private IList<DataStore> _loaded = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger<ConfigurationServiceDataStoreProvider>();

            object response = new[]
            {
                DataStore(
                    1,
                    "Unrecognized Derivative Types",
                    EncryptToBase64(PrimaryConnectionString, TestEncryptionKey),
                    [
                        Derivative("SNAPSHOT", EncryptToBase64(SnapshotConnectionString, TestEncryptionKey)),
                        Derivative("Mirror", EncryptToBase64(SnapshotConnectionString, TestEncryptionKey)),
                        Derivative(
                            "ReadReplica",
                            EncryptToBase64(ReplicaConnectionString, TestEncryptionKey)
                        ),
                    ]
                ),
            };

            _loaded = await CreateProvider(response, _logger).LoadDataStores();
        }

        [Test]
        public void It_should_still_load_the_parent_data_store()
        {
            _loaded.Should().ContainSingle();
        }

        [Test]
        public void It_should_ignore_the_unrecognized_derivatives()
        {
            _loaded[0].Derivatives.Should().NotContainKey(DataStoreDerivativeType.Snapshot);
        }

        [Test]
        public void It_should_keep_the_recognized_derivative()
        {
            _loaded[0].Derivatives.Should().ContainKey(DataStoreDerivativeType.ReadReplica);
        }

        [Test]
        public void It_should_log_one_error_for_each_unrecognized_type()
        {
            ErrorMessages(_logger).Should().HaveCount(2);
        }

        [Test]
        public void It_should_never_log_the_derivative_connection_string()
        {
            AllLoggedText(_logger)
                .Should()
                .NotContain(text => text.Contains(SnapshotConnectionString, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// The Configuration Service permits at most one derivative of each type per data store, so a
    /// duplicate is a violated invariant rather than a supported replacement. The first recognized
    /// value is retained deterministically and the later one is reported.
    /// </summary>
    [TestFixture]
    public class Given_Duplicate_Derivatives_Of_One_Type
    {
        private const string FirstSnapshot = "host=first-snapshot;database=edfi;";
        private const string SecondSnapshot = "host=second-snapshot;database=edfi;";

        private RecordingLogger<ConfigurationServiceDataStoreProvider> _logger = null!;
        private IList<DataStore> _loaded = null!;

        [SetUp]
        public async Task Setup()
        {
            _logger = new RecordingLogger<ConfigurationServiceDataStoreProvider>();

            object response = new[]
            {
                DataStore(
                    1,
                    "Duplicate Snapshots",
                    EncryptToBase64(PrimaryConnectionString, TestEncryptionKey),
                    [
                        Derivative("Snapshot", EncryptToBase64(FirstSnapshot, TestEncryptionKey)),
                        Derivative("Snapshot", EncryptToBase64(SecondSnapshot, TestEncryptionKey)),
                    ]
                ),
            };

            _loaded = await CreateProvider(response, _logger).LoadDataStores();
        }

        [Test]
        public void It_should_retain_the_first_recognized_value()
        {
            _loaded[0].Derivatives[DataStoreDerivativeType.Snapshot].Should().Be(FirstSnapshot);
        }

        [Test]
        public void It_should_hold_exactly_one_snapshot()
        {
            _loaded[0].Derivatives.Should().HaveCount(1);
        }

        [Test]
        public void It_should_log_an_error_for_the_duplicate()
        {
            ErrorMessages(_logger).Should().ContainSingle().Which.Should().Contain("duplicate");
        }
    }

    /// <summary>
    /// Only the decryption service's documented failure contract is caught. Anything else is a runtime
    /// or programming defect and must surface rather than being reinterpreted as absent configuration.
    /// </summary>
    [TestFixture]
    public class Given_Derivative_Decryption_Throws_An_Unexpected_Exception
    {
        private Func<Task> _load = null!;

        [SetUp]
        public void Setup()
        {
            string encryptedPrimary = EncryptToBase64(PrimaryConnectionString, TestEncryptionKey);
            string encryptedSnapshot = EncryptToBase64(SnapshotConnectionString, TestEncryptionKey);

            var decryptionService = A.Fake<IConnectionStringDecryptionService>();
            A.CallTo(() => decryptionService.DecryptFromBase64(encryptedPrimary))
                .Returns(PrimaryConnectionString);
            A.CallTo(() => decryptionService.DecryptFromBase64(encryptedSnapshot))
                .Throws(new InvalidTimeZoneException("unexpected decryption defect"));

            object response = new[]
            {
                DataStore(
                    1,
                    "Unexpected Decryption Failure",
                    encryptedPrimary,
                    [Derivative("Snapshot", encryptedSnapshot)]
                ),
            };

            var provider = CreateProvider(
                response,
                new RecordingLogger<ConfigurationServiceDataStoreProvider>(),
                decryptionService
            );

            _load = () => provider.LoadDataStores();
        }

        [Test]
        public async Task It_should_propagate_the_unexpected_exception()
        {
            await _load.Should().ThrowAsync<InvalidTimeZoneException>();
        }
    }

    /// <summary>
    /// Characterization of existing, deliberately unchanged behavior: the primary connection string is
    /// decrypted inside the enclosing projection, so an undecryptable primary fails the entire tenant
    /// data-store load rather than only its own data store. Narrowing that blast radius is out of scope.
    /// </summary>
    [TestFixture]
    public class Given_An_Undecryptable_Primary_Connection_String
    {
        private ConfigurationServiceDataStoreProvider _provider = null!;
        private Func<Task> _load = null!;

        [SetUp]
        public void Setup()
        {
            object response = new[]
            {
                DataStore(1, "Bad Primary", "not-valid-base64!!!", []),
                DataStore(
                    2,
                    "Healthy Sibling",
                    EncryptToBase64(PrimaryConnectionString, TestEncryptionKey),
                    [Derivative("Snapshot", EncryptToBase64(SnapshotConnectionString, TestEncryptionKey))]
                ),
            };

            _provider = CreateProvider(
                response,
                new RecordingLogger<ConfigurationServiceDataStoreProvider>()
            );
            _load = () => _provider.LoadDataStores();
        }

        [Test]
        public async Task It_should_fail_the_whole_tenant_load()
        {
            await _load.Should().ThrowAsync<InvalidOperationException>();
        }

        [Test]
        public async Task It_should_not_load_the_healthy_sibling_data_store()
        {
            await _load.Should().ThrowAsync<InvalidOperationException>();

            _provider.IsLoaded().Should().BeFalse();
            _provider.GetAll().Should().BeEmpty();
        }
    }

    private sealed class DerivativeTestHttpMessageHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, object> _responses = [];

        public void SetResponse(string path, object response)
        {
            _responses[path] = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string path = request.RequestUri?.PathAndQuery.TrimStart('/') ?? string.Empty;

            string content = _responses.TryGetValue(path, out object? response)
                ? JsonSerializer.Serialize(response)
                : string.Empty;

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(content) }
            );
        }
    }
}
