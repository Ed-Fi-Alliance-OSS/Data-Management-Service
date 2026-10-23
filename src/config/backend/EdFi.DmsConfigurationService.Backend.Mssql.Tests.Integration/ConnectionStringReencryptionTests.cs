// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Data;
using System.Security.Cryptography;
using System.Text;
using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The offline re-encryption pass against a real database. Legacy values are built here, independently
/// of the encryption service, and written to the tables directly.
/// </summary>
public class ConnectionStringReencryptionTests : DatabaseTest
{
    private static readonly byte[] EnvelopeHeader = [.. "EdFi.CS.Envelope"u8, 0x01];

    private static string EncryptionKey => MssqlTestConfiguration.DatabaseOptions.Value.EncryptionKey;

    /// <summary>
    /// The reader the Configuration Service runs once legacy values are disallowed.
    /// </summary>
    private static readonly ConnectionStringEncryptionService StrictReader = new(
        Options.Create(
            new DatabaseOptions
            {
                DatabaseConnection = "unused",
                EncryptionKey = EncryptionKey,
                AllowLegacyConnectionStrings = false,
            }
        )
    );

    protected static byte[] Legacy(string plainText, string? key = null, byte[]? iv = null) =>
        LegacyOfBytes(Encoding.UTF8.GetBytes(plainText), key, iv);

    protected static byte[] LegacyOfBytes(byte[] plainBytes, string? key = null, byte[]? iv = null)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes((key ?? EncryptionKey).PadRight(32, '0')[..32]);
        iv ??= RandomNumberGenerator.GetBytes(16);
        return [.. iv, .. aes.EncryptCbc(plainBytes, iv, PaddingMode.PKCS7)];
    }

    private static string WithExplicitId(string table, int? id, string insert) =>
        id is null
            ? string.Format(insert, "", "")
            : $"SET IDENTITY_INSERT dmscs.{table} ON; {string.Format(insert, "Id, ", "@Id, ")}; SET IDENTITY_INSERT dmscs.{table} OFF;";

    protected static byte[] Envelope(string plainText) => StrictReader.Encrypt(plainText)!;

    protected static ConnectionStringReencryption Reencryption(
        int batchSize = 500,
        Action<string, int>? rowRewritten = null
    ) =>
        new(MssqlTestConfiguration.DatabaseOptions.Value, ConnectionStringReencryptionEngine.Mssql, batchSize)
        {
            RowRewritten = rowRewritten,
        };

    private static SqlParameter Binary(string name, byte[]? value) =>
        new(name, SqlDbType.VarBinary, -1) { Value = (object?)value ?? DBNull.Value };

    /// <summary>
    /// The base fixture resets Tenant, so tenants need no cleanup of their own.
    /// </summary>
    protected async Task<long> InsertTenant(string name)
    {
        await using var command = new SqlCommand(
            "INSERT INTO dmscs.Tenant (Name) OUTPUT INSERTED.Id VALUES (@Name)",
            Connection
        );
        command.Parameters.AddWithValue("@Name", name);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// An explicit <paramref name="id"/> stores a row an identity would never generate, such as zero
    /// or a negative value from an import.
    /// </summary>
    protected async Task<int> InsertDataStore(long? tenantId, byte[]? connectionString, int? id = null)
    {
        await using var command = new SqlCommand(
            WithExplicitId(
                "DataStore",
                id,
                """
                INSERT INTO dmscs.DataStore ({0}DataStoreType, Name, ConnectionString, TenantId, ModifiedBy, LastModifiedAt)
                OUTPUT INSERTED.Id
                VALUES ({1}'Production', @Name, @ConnectionString, @TenantId, 'seed', '2020-01-02T03:04:05')
                """
            ),
            Connection
        );
        command.Parameters.AddWithValue("@Id", (object?)id ?? DBNull.Value);
        command.Parameters.AddWithValue("@Name", $"Store {Guid.NewGuid():N}");
        command.Parameters.Add(Binary("@ConnectionString", connectionString));
        command.Parameters.Add(
            new SqlParameter("@TenantId", SqlDbType.BigInt) { Value = (object?)tenantId ?? DBNull.Value }
        );
        return (int)(await command.ExecuteScalarAsync())!;
    }

    protected async Task<int> InsertDerivative(
        int dataStoreId,
        string derivativeType,
        byte[]? connectionString,
        int? id = null
    )
    {
        await using var command = new SqlCommand(
            WithExplicitId(
                "DataStoreDerivative",
                id,
                """
                INSERT INTO dmscs.DataStoreDerivative ({0}DataStoreId, DerivativeType, ConnectionString, ModifiedBy, LastModifiedAt)
                OUTPUT INSERTED.Id
                VALUES ({1}@DataStoreId, @DerivativeType, @ConnectionString, 'seed', '2020-01-02T03:04:05')
                """
            ),
            Connection
        );
        command.Parameters.AddWithValue("@Id", (object?)id ?? DBNull.Value);
        command.Parameters.AddWithValue("@DataStoreId", dataStoreId);
        command.Parameters.AddWithValue("@DerivativeType", derivativeType);
        command.Parameters.Add(Binary("@ConnectionString", connectionString));
        return (int)(await command.ExecuteScalarAsync())!;
    }

    protected async Task<byte[]?> Stored(string table, int id)
    {
        await using var command = new SqlCommand(
            $"SELECT ConnectionString FROM dmscs.{table} WHERE Id = @Id",
            Connection
        );
        command.Parameters.AddWithValue("@Id", id);
        object? value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : (byte[])value!;
    }

    protected async Task<string> Metadata(string table, int id)
    {
        await using var command = new SqlCommand(
            $"SELECT ModifiedBy + '|' + CONVERT(varchar(30), LastModifiedAt, 126) FROM dmscs.{table} WHERE Id = @Id",
            Connection
        );
        command.Parameters.AddWithValue("@Id", id);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Legacy, envelope and null values in both tables and two tenants, read two rows at a time so the
    /// later legacy rows are only reached by paging. Ids include a negative parent and a zero
    /// derivative, which an identity never generates but an import can.
    /// </summary>
    [TestFixture]
    public class Given_mixed_rows_in_both_tables_across_tenants : ConnectionStringReencryptionTests
    {
        private const string SecretReferencePlainText =
            "host=legacy-a;database=edfi;password=${secret:tenant-a-password};";
        private const string UnicodePlainText = "host=legacy-ü;database=édfi;";

        /// <summary>
        /// A real U+FFFD, validly encoded: it has to migrate unchanged.
        /// </summary>
        private const string ReplacementCharacterPlainText = "host=legacy-negative;password=pa\uFFFDss;";
        private const string LaterLegacyPlainText = "host=legacy-late;database=edfi;";
        private const string EnvelopePlainText = "host=already-current;database=edfi;";
        private const string LegacyDerivativePlainText = "host=replica-legacy;database=edfi;";
        private const string ZeroIdDerivativePlainText = "host=snapshot-zero;database=edfi;";
        private const string EnvelopeDerivativePlainText = "host=replica-current;database=edfi;";

        private readonly Dictionary<int, string> _convertedStores = [];
        private readonly Dictionary<int, string> _convertedDerivatives = [];
        private int _envelopeStore;
        private byte[] _envelopeStoreBytes = null!;
        private int _nullStore;
        private int _envelopeDerivative;
        private byte[] _envelopeDerivativeBytes = null!;
        private int _nullDerivative;
        private int _metadataStore;
        private string _metadataBefore = null!;
        private IReadOnlyList<ConnectionStringReencryptionCounts> _firstRun = null!;
        private IReadOnlyList<ConnectionStringReencryptionCounts> _rerun = null!;
        private readonly Dictionary<int, byte[]?> _storesAfterFirstRun = [];
        private readonly Dictionary<int, byte[]?> _derivativesAfterFirstRun = [];

        [SetUp]
        public async Task Setup()
        {
            _convertedStores.Clear();
            _convertedDerivatives.Clear();
            _storesAfterFirstRun.Clear();
            _derivativesAfterFirstRun.Clear();
            long tenantA = await InsertTenant($"Tenant A {Guid.NewGuid():N}");
            long tenantB = await InsertTenant($"Tenant B {Guid.NewGuid():N}");

            int negativeStore = await InsertDataStore(tenantA, Legacy(ReplacementCharacterPlainText), id: -5);
            _convertedStores[negativeStore] = ReplacementCharacterPlainText;
            int secretStore = await InsertDataStore(tenantA, Legacy(SecretReferencePlainText));
            _convertedStores[secretStore] = SecretReferencePlainText;
            _envelopeStoreBytes = Envelope(EnvelopePlainText);
            _envelopeStore = await InsertDataStore(tenantB, _envelopeStoreBytes);
            _nullStore = await InsertDataStore(tenantB, null);
            int unicodeStore = await InsertDataStore(tenantB, Legacy(UnicodePlainText));
            _convertedStores[unicodeStore] = UnicodePlainText;
            int lateStore = await InsertDataStore(null, Legacy(LaterLegacyPlainText));
            _convertedStores[lateStore] = LaterLegacyPlainText;

            int zeroDerivative = await InsertDerivative(
                secretStore,
                "Snapshot",
                Legacy(ZeroIdDerivativePlainText),
                id: 0
            );
            _convertedDerivatives[zeroDerivative] = ZeroIdDerivativePlainText;
            int legacyDerivative = await InsertDerivative(
                secretStore,
                "ReadReplica",
                Legacy(LegacyDerivativePlainText)
            );
            _convertedDerivatives[legacyDerivative] = LegacyDerivativePlainText;
            _envelopeDerivativeBytes = Envelope(EnvelopeDerivativePlainText);
            _envelopeDerivative = await InsertDerivative(
                _envelopeStore,
                "ReadReplica",
                _envelopeDerivativeBytes
            );
            _nullDerivative = await InsertDerivative(unicodeStore, "Snapshot", null);

            _metadataStore = secretStore;
            _metadataBefore = await Metadata("DataStore", secretStore);

            _firstRun = await Reencryption(batchSize: 2).RunAsync(Connection!, CancellationToken.None);

            foreach (int id in _convertedStores.Keys.Append(_envelopeStore))
            {
                _storesAfterFirstRun[id] = await Stored("DataStore", id);
            }
            foreach (int id in _convertedDerivatives.Keys.Append(_envelopeDerivative))
            {
                _derivativesAfterFirstRun[id] = await Stored("DataStoreDerivative", id);
            }

            _rerun = await Reencryption(batchSize: 2).RunAsync(Connection!, CancellationToken.None);
        }

        [Test]
        public void It_reports_counts_by_table() =>
            _firstRun
                .Should()
                .Equal(
                    new ConnectionStringReencryptionCounts("DataStore", 4, 1),
                    new ConnectionStringReencryptionCounts("DataStoreDerivative", 2, 1)
                );

        [Test]
        public async Task It_rewrites_every_legacy_parent_as_an_envelope_the_strict_reader_decrypts_exactly()
        {
            foreach ((int id, string plainText) in _convertedStores)
            {
                byte[] stored = (await Stored("DataStore", id))!;
                stored[..EnvelopeHeader.Length].Should().Equal(EnvelopeHeader);
                StrictReader.Decrypt(stored).Should().Be(plainText);
            }
        }

        [Test]
        public async Task It_rewrites_every_legacy_derivative_as_an_envelope_the_strict_reader_decrypts_exactly()
        {
            foreach ((int id, string plainText) in _convertedDerivatives)
            {
                byte[] stored = (await Stored("DataStoreDerivative", id))!;
                stored[..EnvelopeHeader.Length].Should().Equal(EnvelopeHeader);
                StrictReader.Decrypt(stored).Should().Be(plainText);
            }
        }

        [Test]
        public async Task It_leaves_existing_envelopes_byte_for_byte()
        {
            (await Stored("DataStore", _envelopeStore)).Should().Equal(_envelopeStoreBytes);
            (await Stored("DataStoreDerivative", _envelopeDerivative))
                .Should()
                .Equal(_envelopeDerivativeBytes);
        }

        [Test]
        public async Task It_leaves_null_values_null()
        {
            (await Stored("DataStore", _nullStore)).Should().BeNull();
            (await Stored("DataStoreDerivative", _nullDerivative)).Should().BeNull();
        }

        [Test]
        public async Task It_leaves_the_audit_metadata_unchanged() =>
            (await Metadata("DataStore", _metadataStore)).Should().Be(_metadataBefore);

        [Test]
        public void It_converts_nothing_on_a_rerun() =>
            _rerun
                .Should()
                .Equal(
                    new ConnectionStringReencryptionCounts("DataStore", 0, 5),
                    new ConnectionStringReencryptionCounts("DataStoreDerivative", 0, 3)
                );

        [Test]
        public async Task It_keeps_the_first_run_bytes_on_a_rerun()
        {
            foreach ((int id, byte[]? bytes) in _storesAfterFirstRun)
            {
                (await Stored("DataStore", id)).Should().Equal(bytes);
            }
            foreach ((int id, byte[]? bytes) in _derivativesAfterFirstRun)
            {
                (await Stored("DataStoreDerivative", id)).Should().Equal(bytes);
            }
        }
    }

    /// <summary>
    /// Cancelled once the first legacy value is rewritten, at a point the pass reports rather than
    /// after a delay: the rewrite must be rolled back with the rest.
    /// </summary>
    [TestFixture]
    public class Given_a_pass_cancelled_after_its_first_rewrite : ConnectionStringReencryptionTests
    {
        private readonly Dictionary<(string Table, int Id), byte[]> _before = [];
        private readonly List<(string Table, int Id)> _rewritten = [];
        private Func<Task> _act = null!;

        [SetUp]
        public async Task Setup()
        {
            _before.Clear();
            _rewritten.Clear();
            int first = await InsertDataStore(null, Legacy("host=first;database=edfi;"));
            _before[("DataStore", first)] = (await Stored("DataStore", first))!;
            int second = await InsertDataStore(null, Legacy("host=second;database=edfi;"));
            _before[("DataStore", second)] = (await Stored("DataStore", second))!;
            int derivative = await InsertDerivative(
                first,
                "ReadReplica",
                Legacy("host=replica;database=edfi;")
            );
            _before[("DataStoreDerivative", derivative)] = (await Stored("DataStoreDerivative", derivative))!;

            using CancellationTokenSource cancellation = new();
            ConnectionStringReencryption reencryption = Reencryption(
                rowRewritten: (table, id) =>
                {
                    _rewritten.Add((table, id));
                    cancellation.Cancel();
                }
            );
            _act = () => reencryption.RunAsync(Connection!, cancellation.Token);
            await _act.Should().ThrowAsync<OperationCanceledException>();
        }

        [Test]
        public void It_stops_after_the_first_rewrite() => _rewritten.Should().ContainSingle();

        [Test]
        public async Task It_leaves_every_value_as_it_was()
        {
            foreach (((string table, int id), byte[] bytes) in _before)
            {
                (await Stored(table, id)).Should().Equal(bytes);
            }
        }
    }

    [TestFixture]
    public class Given_a_check : ConnectionStringReencryptionTests
    {
        private byte[] _legacy = null!;
        private int _legacyStore;
        private byte[] _envelope = null!;
        private int _envelopeDerivative;
        private IReadOnlyList<ConnectionStringReencryptionCounts> _counts = null!;

        [SetUp]
        public async Task Setup()
        {
            _legacy = Legacy("host=legacy;database=edfi;");
            _legacyStore = await InsertDataStore(null, _legacy);
            _envelope = Envelope("host=current;database=edfi;");
            _envelopeDerivative = await InsertDerivative(_legacyStore, "ReadReplica", _envelope);

            _counts = await Reencryption().CheckAsync(Connection!, CancellationToken.None);
        }

        [Test]
        public void It_counts_legacy_and_current_values_by_table() =>
            _counts
                .Should()
                .Equal(
                    new ConnectionStringReencryptionCounts("DataStore", 1, 0),
                    new ConnectionStringReencryptionCounts("DataStoreDerivative", 0, 1)
                );

        [Test]
        public async Task It_changes_nothing()
        {
            (await Stored("DataStore", _legacyStore)).Should().Equal(_legacy);
            (await Stored("DataStoreDerivative", _envelopeDerivative)).Should().Equal(_envelope);
        }
    }

    [TestFixture]
    public class Given_a_check_meeting_a_value_the_pass_cannot_migrate : ConnectionStringReencryptionTests
    {
        [Test]
        public async Task It_fails_naming_the_row()
        {
            int parent = await InsertDataStore(null, Legacy("host=parent;database=edfi;"));
            int derivative = await InsertDerivative(parent, "ReadReplica", new byte[33]);

            var act = () => Reencryption().CheckAsync(Connection!, CancellationToken.None);

            (await act.Should().ThrowAsync<ConnectionStringReencryptionException>()).WithMessage(
                $"*DataStoreDerivative Id {derivative}*"
            );
        }
    }

    /// <summary>
    /// Each value is preceded by a legacy parent the pass converts first; the failure must roll that
    /// conversion back.
    /// </summary>
    [TestFixture]
    public class Given_a_value_the_pass_cannot_migrate_after_a_converted_parent
        : ConnectionStringReencryptionTests
    {
        [TestCase("malformed", "*neither an envelope nor legacy AES-CBC cipher text*")]
        [TestCase("tampered", "*envelope failed authentication*")]
        [TestCase("wrongkey", "*legacy value*with the configured key*")]
        [TestCase("empty", "*neither an envelope nor legacy AES-CBC cipher text*")]
        [TestCase("invalidutf8", "*legacy value does not decrypt to UTF-8 text with the configured key*")]
        [TestCase("emptyplaintext", "*legacy value decrypts to an empty connection string*")]
        public async Task It_fails_naming_the_derivative_and_rolls_the_parent_back(
            string kind,
            string expectedReason
        )
        {
            byte[] legacyParent = Legacy("host=parent;database=edfi;");
            int parent = await InsertDataStore(null, legacyParent);
            byte[] envelope = Envelope("host=derivative;database=edfi;");
            envelope[^1] ^= 0x01;
            byte[] derivativeValue = kind switch
            {
                "malformed" => new byte[33],
                "tampered" => envelope,
                // A fixed IV keeps the wrong-key failure deterministic.
                "wrongkey" => Legacy(
                    "host=derivative;database=edfi;",
                    "Another-Key-Entirely-0123456789ABCDEF",
                    [.. Enumerable.Range(0, 16).Select(value => (byte)value)]
                ),
                // Valid padding around bytes that are not UTF-8: 0xC3 starts a sequence 0x28 cannot continue.
                "invalidutf8" => LegacyOfBytes([0x68, 0x6F, 0x73, 0x74, 0x3D, 0xC3, 0x28]),
                "emptyplaintext" => Legacy(""),
                _ => [],
            };
            int derivative = await InsertDerivative(parent, "ReadReplica", derivativeValue);

            var act = () => Reencryption().RunAsync(Connection!, CancellationToken.None);

            (await act.Should().ThrowAsync<ConnectionStringReencryptionException>())
                .WithMessage($"*DataStoreDerivative Id {derivative}*")
                .WithMessage(expectedReason)
                .WithMessage("*No changes were made.");
            (await Stored("DataStore", parent)).Should().Equal(legacyParent);
            (await Stored("DataStoreDerivative", derivative)).Should().Equal(derivativeValue);
        }
    }
}
