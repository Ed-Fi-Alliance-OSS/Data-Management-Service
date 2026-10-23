// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Security.Cryptography;
using System.Text;
using EdFi.DmsConfigurationService.Backend.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace EdFi.DmsConfigurationService.Backend.Postgresql.Tests.Integration;

/// <summary>
/// The offline re-encryption pass against a real database. Legacy values are built here, independently
/// of the encryption service, and written to the tables directly.
/// </summary>
public class ConnectionStringReencryptionTests : DatabaseTest
{
    private static readonly byte[] EnvelopeHeader = [.. "EdFi.CS.Envelope"u8, 0x01];

    private static string EncryptionKey => Configuration.DatabaseOptions.Value.EncryptionKey;

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

    private readonly List<long> _tenantIds = [];

    protected static byte[] Legacy(string plainText, string? key = null, byte[]? iv = null) =>
        LegacyOfBytes(Encoding.UTF8.GetBytes(plainText), key, iv);

    protected static byte[] LegacyOfBytes(byte[] plainBytes, string? key = null, byte[]? iv = null)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.UTF8.GetBytes((key ?? EncryptionKey).PadRight(32, '0')[..32]);
        iv ??= RandomNumberGenerator.GetBytes(16);
        return [.. iv, .. aes.EncryptCbc(plainBytes, iv, PaddingMode.PKCS7)];
    }

    private static string WithExplicitId(int? id, string insert) =>
        id is null
            ? string.Format(insert, "", "", "")
            : string.Format(insert, "\"Id\", ", "@Id, ", "OVERRIDING SYSTEM VALUE ");

    private static void AddExplicitId(NpgsqlCommand command, int? id)
    {
        if (id is not null)
        {
            command.Parameters.AddWithValue("Id", id.Value);
        }
    }

    protected static byte[] Envelope(string plainText) => StrictReader.Encrypt(plainText)!;

    protected static ConnectionStringReencryption Reencryption(
        int batchSize = 500,
        Action<string, int>? rowRewritten = null
    ) =>
        new(Configuration.DatabaseOptions.Value, ConnectionStringReencryptionEngine.Postgresql, batchSize)
        {
            RowRewritten = rowRewritten,
        };

    protected async Task<long> InsertTenant(string name)
    {
        await using var command = new NpgsqlCommand(
            "INSERT INTO \"dmscs\".\"Tenant\" (\"Name\") VALUES (@Name) RETURNING \"Id\"",
            Connection
        );
        command.Parameters.AddWithValue("Name", name);
        long id = (long)(await command.ExecuteScalarAsync())!;
        _tenantIds.Add(id);
        return id;
    }

    /// <summary>
    /// An explicit <paramref name="id"/> stores a row an identity would never generate, such as zero
    /// or a negative value from an import.
    /// </summary>
    protected async Task<int> InsertDataStore(long? tenantId, byte[]? connectionString, int? id = null)
    {
        await using var command = new NpgsqlCommand(
            WithExplicitId(
                id,
                """
                INSERT INTO "dmscs"."DataStore" ({0}"DataStoreType", "Name", "ConnectionString", "TenantId", "ModifiedBy", "LastModifiedAt")
                {2}VALUES ({1}'Production', @Name, @ConnectionString, @TenantId, 'seed', '2020-01-02T03:04:05')
                RETURNING "Id"
                """
            ),
            Connection
        );
        AddExplicitId(command, id);
        command.Parameters.AddWithValue("Name", $"Store {Guid.NewGuid():N}");
        command.Parameters.Add(
            new NpgsqlParameter("ConnectionString", NpgsqlTypes.NpgsqlDbType.Bytea)
            {
                Value = (object?)connectionString ?? DBNull.Value,
            }
        );
        command.Parameters.Add(
            new NpgsqlParameter("TenantId", NpgsqlTypes.NpgsqlDbType.Bigint)
            {
                Value = (object?)tenantId ?? DBNull.Value,
            }
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
        await using var command = new NpgsqlCommand(
            WithExplicitId(
                id,
                """
                INSERT INTO "dmscs"."DataStoreDerivative" ({0}"DataStoreId", "DerivativeType", "ConnectionString", "ModifiedBy", "LastModifiedAt")
                {2}VALUES ({1}@DataStoreId, @DerivativeType, @ConnectionString, 'seed', '2020-01-02T03:04:05')
                RETURNING "Id"
                """
            ),
            Connection
        );
        AddExplicitId(command, id);
        command.Parameters.AddWithValue("DataStoreId", dataStoreId);
        command.Parameters.AddWithValue("DerivativeType", derivativeType);
        command.Parameters.Add(
            new NpgsqlParameter("ConnectionString", NpgsqlTypes.NpgsqlDbType.Bytea)
            {
                Value = (object?)connectionString ?? DBNull.Value,
            }
        );
        return (int)(await command.ExecuteScalarAsync())!;
    }

    protected async Task<byte[]?> Stored(string table, int id)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT \"ConnectionString\" FROM \"dmscs\".\"{table}\" WHERE \"Id\" = @Id",
            Connection
        );
        command.Parameters.AddWithValue("Id", id);
        object? value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : (byte[])value!;
    }

    protected async Task<string> Metadata(string table, int id)
    {
        await using var command = new NpgsqlCommand(
            $"SELECT \"ModifiedBy\" || '|' || \"LastModifiedAt\"::text FROM \"dmscs\".\"{table}\" WHERE \"Id\" = @Id",
            Connection
        );
        command.Parameters.AddWithValue("Id", id);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    [TearDown]
    public async Task DeleteTenants()
    {
        await using var connection = await DataSource!.OpenConnectionAsync();
        foreach (long tenantId in _tenantIds)
        {
            await using var command = new NpgsqlCommand(
                "DELETE FROM \"dmscs\".\"Tenant\" WHERE \"Id\" = @Id",
                connection
            );
            command.Parameters.AddWithValue("Id", tenantId);
            await command.ExecuteNonQueryAsync();
        }
        _tenantIds.Clear();
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

    /// <summary>
    /// The installer entry point, run in process: argument dispatch, exit status and output.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class Given_the_installer_reencrypt_mode : ConnectionStringReencryptionTests
    {
        private const string PlainText = "host=installer-legacy;password=Installer-S3cret!;database=edfi;";

        private string? _keyBefore;
        private TextWriter _outBefore = null!;
        private StringWriter _output = null!;

        [SetUp]
        public void CaptureProcessState()
        {
            _keyBefore = Environment.GetEnvironmentVariable(Installer.Options.EncryptionKeyVariable);
            _outBefore = Console.Out;
            _output = new StringWriter();
            Console.SetOut(_output);
            Environment.ExitCode = 0;
        }

        [TearDown]
        public void RestoreProcessState()
        {
            Console.SetOut(_outBefore);
            _output.Dispose();
            Environment.SetEnvironmentVariable(Installer.Options.EncryptionKeyVariable, _keyBefore);
            Environment.ExitCode = 0;
        }

        private static void RunInstaller(string? encryptionKey)
        {
            Environment.SetEnvironmentVariable(Installer.Options.EncryptionKeyVariable, encryptionKey);
            Installer.Program.Main([
                "-e",
                "postgresql",
                "-c",
                Configuration.DatabaseOptions.Value.DatabaseConnection,
                "--reencrypt-connection-strings",
            ]);
        }

        [Test]
        public async Task It_converts_and_reports_counts_with_exit_status_0()
        {
            int id = await InsertDataStore(null, Legacy(PlainText));

            RunInstaller(EncryptionKey);

            Environment.ExitCode.Should().Be(0);
            _output
                .ToString()
                .Should()
                .Contain("DataStore: 1 converted, 0 already current.")
                .And.Contain("DataStoreDerivative: 0 converted, 0 already current.")
                .And.Contain("Connection string re-encryption completed.");
            StrictReader.Decrypt((await Stored("DataStore", id))!).Should().Be(PlainText);
        }

        [Test]
        public async Task It_fails_with_exit_status_1_naming_the_row_and_no_secret()
        {
            byte[] legacy = Legacy(PlainText);
            int parent = await InsertDataStore(null, legacy);
            int derivative = await InsertDerivative(parent, "ReadReplica", new byte[33]);

            RunInstaller(EncryptionKey);

            Environment.ExitCode.Should().Be(1);
            string output = _output.ToString();
            output
                .Should()
                .Contain($"DataStoreDerivative Id {derivative}")
                .And.Contain("No changes were made.");
            output.Should().NotContain(EncryptionKey).And.NotContain("Installer-S3cret!");
            (await Stored("DataStore", parent)).Should().Equal(legacy);
        }

        [Test]
        public async Task It_refuses_a_missing_key_before_reading_with_exit_status_1()
        {
            byte[] legacy = Legacy(PlainText);
            int id = await InsertDataStore(null, legacy);

            RunInstaller(null);

            Environment.ExitCode.Should().Be(1);
            _output.ToString().Should().Contain("Missing required DatabaseSettings value: EncryptionKey");
            (await Stored("DataStore", id)).Should().Equal(legacy);
        }

        [TestCase(
            "--reencrypt-connection-strings",
            "--engine",
            "postgresql",
            TestName = "It_exits_1_when_the_connection_string_is_missing"
        )]
        [TestCase(
            "--reencrypt-connection-strings",
            "--engine",
            "postgresql",
            "--connectionString",
            "Host=unused",
            "--bogus",
            TestName = "It_exits_1_for_an_unknown_option"
        )]
        public void It_exits_1_when_the_migration_arguments_do_not_parse(params string[] arguments)
        {
            Environment.SetEnvironmentVariable(Installer.Options.EncryptionKeyVariable, EncryptionKey);

            Installer.Program.Main(arguments);

            Environment.ExitCode.Should().Be(1);
            _output.ToString().Should().NotContain("re-encryption completed");
        }

        /// <summary>
        /// Each would otherwise run the pass, deploy, or print help with exit status 0, and automation
        /// would read it as a run that found nothing to migrate.
        /// </summary>
        [TestCase("--reencrypt-connection-strings=true", TestName = "It_refuses_the_assignment_form")]
        [TestCase("--reencrypt-connection-strings=false", TestName = "It_refuses_an_assigned_false")]
        [TestCase("--reencrypt-connection-strings=bogus", TestName = "It_refuses_an_assigned_non_boolean")]
        [TestCase(
            "--check-connection-strings=true",
            TestName = "It_refuses_the_assignment_form_of_the_check"
        )]
        [TestCase("--reencrypt-connection-strings", "false", TestName = "It_refuses_a_stray_value")]
        [TestCase("--reencrypt-connection-string", TestName = "It_refuses_a_misspelled_switch")]
        [TestCase(
            "--reencrypt-connection-strings",
            "--check-connection-strings",
            TestName = "It_refuses_both_modes_at_once"
        )]
        public async Task It_exits_1_and_changes_nothing(params string[] switches)
        {
            byte[] legacy = Legacy(PlainText);
            int id = await InsertDataStore(null, legacy);
            Environment.SetEnvironmentVariable(Installer.Options.EncryptionKeyVariable, EncryptionKey);

            Installer.Program.Main([
                "-e",
                "postgresql",
                "-c",
                Configuration.DatabaseOptions.Value.DatabaseConnection,
                .. switches,
            ]);

            Environment.ExitCode.Should().Be(1);
            _output.ToString().Should().NotContain("completed");
            (await Stored("DataStore", id)).Should().Equal(legacy);
        }

        private static void RunCheck() =>
            Installer.Program.Main([
                "-e",
                "postgresql",
                "-c",
                Configuration.DatabaseOptions.Value.DatabaseConnection,
                "--check-connection-strings",
            ]);

        [Test]
        public async Task It_checks_with_exit_status_2_while_legacy_values_remain_and_changes_nothing()
        {
            byte[] legacy = Legacy(PlainText);
            int id = await InsertDataStore(null, legacy);
            Environment.SetEnvironmentVariable(Installer.Options.EncryptionKeyVariable, EncryptionKey);

            RunCheck();

            Environment.ExitCode.Should().Be(2);
            _output
                .ToString()
                .Should()
                .Contain("DataStore: 1 legacy, 0 current.")
                .And.Contain("1 legacy values remain");
            (await Stored("DataStore", id)).Should().Equal(legacy);
        }

        [Test]
        public async Task It_checks_with_exit_status_0_once_no_legacy_values_remain()
        {
            byte[] envelope = Envelope(PlainText);
            int id = await InsertDataStore(null, envelope);
            Environment.SetEnvironmentVariable(Installer.Options.EncryptionKeyVariable, EncryptionKey);

            RunCheck();

            Environment.ExitCode.Should().Be(0);
            _output.ToString().Should().Contain("no legacy values remain");
            (await Stored("DataStore", id)).Should().Equal(envelope);
        }

        [Test]
        public async Task It_checks_with_exit_status_1_for_a_value_it_cannot_verify()
        {
            int parent = await InsertDataStore(null, Envelope(PlainText));
            int derivative = await InsertDerivative(parent, "ReadReplica", new byte[33]);
            Environment.SetEnvironmentVariable(Installer.Options.EncryptionKeyVariable, EncryptionKey);

            RunCheck();

            Environment.ExitCode.Should().Be(1);
            _output.ToString().Should().Contain($"DataStoreDerivative Id {derivative}");
        }

        [Test]
        public void It_exits_0_for_a_requested_help_screen()
        {
            Installer.Program.Main(["--reencrypt-connection-strings", "--help"]);

            Environment.ExitCode.Should().Be(0);
            _output.ToString().Should().Contain("reencrypt-connection-strings");
        }

        /// <summary>
        /// The database error code tells the operator what to fix, without the connection string.
        /// </summary>
        [Test]
        public void It_names_the_database_error_code_when_the_database_does_not_exist()
        {
            string missingDatabase = new NpgsqlConnectionStringBuilder(
                Configuration.DatabaseOptions.Value.DatabaseConnection
            )
            {
                Database = $"missing_{Guid.NewGuid():N}",
            }.ConnectionString;
            Environment.SetEnvironmentVariable(Installer.Options.EncryptionKeyVariable, EncryptionKey);

            Installer.Program.Main(["-e", "postgresql", "-c", missingDatabase, "--check-connection-strings"]);

            Environment.ExitCode.Should().Be(1);
            _output.ToString().Should().Contain("SQLSTATE 3D000").And.NotContain(missingDatabase);
        }

        [Test]
        public void It_exits_1_when_the_deploy_fails()
        {
            Installer.Program.Main([
                "-e",
                "postgresql",
                "-c",
                "Host=localhost;Port=1;Username=nobody;Database=nothing;Timeout=1",
            ]);

            Environment.ExitCode.Should().Be(1);
            _output.ToString().Should().Contain("Failed to deploy database");
        }

        [Test]
        public async Task It_deploys_and_leaves_legacy_values_alone_without_the_switch()
        {
            byte[] legacy = Legacy(PlainText);
            int id = await InsertDataStore(null, legacy);

            Installer.Program.Main([
                "-e",
                "postgresql",
                "-c",
                Configuration.DatabaseOptions.Value.DatabaseConnection,
            ]);

            Environment.ExitCode.Should().Be(0);
            _output.ToString().Should().NotContain("re-encryption");
            (await Stored("DataStore", id)).Should().Equal(legacy);
        }
    }
}
