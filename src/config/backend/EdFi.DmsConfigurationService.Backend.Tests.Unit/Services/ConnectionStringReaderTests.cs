// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Data.Common;
using EdFi.DmsConfigurationService.Backend.Mssql;
using EdFi.DmsConfigurationService.Backend.Postgresql;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.Secrets;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Services;

[TestFixture]
public class ConnectionStringReaderTests
{
    private const string EncryptionKey = "TestEncryptionKey123456789012345678901234567890";

    private const int TimeoutSeconds = 10;

    /// <summary>
    /// Cipher text produced by <see cref="ConnectionStringEncryptionService"/> under
    /// <see cref="EncryptionKey"/> before the read rule existed, kept as literals so a token-free
    /// value is asserted against bytes this change did not produce.
    /// </summary>
    private const string TokenFreeCipherText =
        "9qM/kCr4nt1wvGLtZGy6IFJXKklsb4T8h7LEbGpc5AyOrmuSrkns2reIlJlrhilc9K0J0/r9AgMLhGgPN/fE8A==";

    /// <summary>Encrypts <c>Host=db;Password=p${secret:}x${secret:a b}${secret:</c>.</summary>
    private const string MalformedReferencesCipherText =
        "6gniKwKRDoMSoHOu06RqgMvTyuOkh+AloYgpf+0Xc8AC+QyW1Yk6FMMQyShgXurTiHZ5KY4k1phHpsCQHdVDUXFuplCet/DSc6OBmsdqKP0=";

    /// <summary>Encrypts <c>not a connection string ${ at all</c>, which no engine parses.</summary>
    private const string UnparseableTokenFreeCipherText =
        "4b0f8HK6xxGKZisopS4+dtehM0N1DnHgbVn8N1Q3m1s7jSe/QtejM6Z7Fcab6+NWh8A0QpJlleQGk01O/MVQ4g==";

    /// <summary>The resolver's own failure text, which must never leave the resolver.</summary>
    private const string ResolverFailureText = "vault refused hunter2-for-prod";

    private const string ResolvedSecret = "s3cr3t-resolved-value";

    private static readonly ConnectionStringEncryptionService _encryption = new(
        Options.Create(
            new DatabaseOptions { DatabaseConnection = "Server=test;", EncryptionKey = EncryptionKey }
        )
    );

    private static readonly IDataStoreConnectionStringBuilderSource _postgresql =
        new PostgresqlDataStoreConnectionStringValidator();

    private static readonly IDataStoreConnectionStringBuilderSource _sqlServer =
        new MssqlDataStoreConnectionStringValidator();

    protected static readonly ConnectionStringRow DataStoreRow = new ConnectionStringRow.DataStore(5);

    protected static readonly ConnectionStringRow ResourceDerivativeRow = new ConnectionStringRow.Derivative(
        7,
        5,
        "ReadReplica",
        DerivativeReadMode.Resource
    );

    protected static readonly ConnectionStringRow NestedDerivativeRow = new ConnectionStringRow.Derivative(
        7,
        5,
        "ReadReplica",
        DerivativeReadMode.PartOfDataStore
    );

    protected CountingEncryptionService Encryption { get; private set; } = null!;
    protected ScriptedResolver Resolver { get; private set; } = null!;
    protected CapturingLogger<ConnectionStringReader> Logger { get; private set; } = null!;
    protected TenantContextProvider Tenant { get; private set; } = null!;
    protected FakeTimeProvider Time { get; private set; } = null!;
    protected SecretValueCache Cache { get; private set; } = null!;

    [SetUp]
    public void CreateCollaborators()
    {
        Encryption = new CountingEncryptionService(_encryption);
        Resolver = new ScriptedResolver((_, _) => ValueTask.FromResult(ResolvedSecret));
        Logger = new CapturingLogger<ConnectionStringReader>();
        Tenant = new TenantContextProvider();
        Time = new FakeTimeProvider();
        Cache = CreateCache(300);
    }

    protected SecretValueCache CreateCache(int expirationSeconds) =>
        new(Options.Create(new SecretsOptions { CacheExpirationSeconds = expirationSeconds }), Time);

    protected ConnectionStringReader CreateReader(
        IDataStoreConnectionStringBuilderSource? builderSource = null,
        bool withResolver = true
    ) =>
        new(
            Encryption,
            builderSource ?? _postgresql,
            Cache,
            Options.Create(new SecretsOptions { ResolveTimeoutSeconds = TimeoutSeconds }),
            Tenant,
            Logger,
            withResolver ? Resolver : null,
            Time
        );

    protected static byte[] Stored(string plainText) => _encryption.Encrypt(plainText)!;

    protected static string Decrypted(string base64) =>
        _encryption.Decrypt(Convert.FromBase64String(base64))!;

    protected IEnumerable<string> LoggedText() =>
        Logger.Entries.SelectMany(entry =>
            entry.Fields.Select(field => field.Value?.ToString() ?? string.Empty).Append(entry.Message)
        );

    /// <summary>
    /// A real-time bound on a wait the fake clock should already have ended, so a regression fails the
    /// test rather than hanging the run. It is never what ends a passing test.
    /// </summary>
    protected static readonly TimeSpan SafetyBound = TimeSpan.FromSeconds(30);

    protected static async Task<ConnectionStringReadException> ReadFailure(Func<Task> read)
    {
        try
        {
            await read();
        }
        catch (ConnectionStringReadException exception)
        {
            return exception;
        }

        throw new AssertionException("The read was expected to fail.");
    }

    /// <summary>Delegates to the real encryption service, counting each direction.</summary>
    public sealed class CountingEncryptionService(IConnectionStringEncryptionService inner)
        : IConnectionStringEncryptionService
    {
        public int Decrypts { get; private set; }
        public int Encrypts { get; private set; }

        public byte[]? Encrypt(string? connectionString)
        {
            Encrypts++;
            return inner.Encrypt(connectionString);
        }

        public string? Decrypt(byte[]? encryptedConnectionString)
        {
            Decrypts++;
            return inner.Decrypt(encryptedConnectionString);
        }
    }

    /// <summary>A resolver whose behavior each test scripts, recording every reference it is asked for.</summary>
    public sealed class ScriptedResolver(Func<SecretReference, CancellationToken, ValueTask<string>> behavior)
        : ISecretResolver
    {
        public Func<SecretReference, CancellationToken, ValueTask<string>> Behavior { get; set; } = behavior;

        public ConcurrentQueue<SecretReference> Calls { get; } = new();

        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
        {
            Calls.Enqueue(reference);
            return Behavior(reference, cancellationToken);
        }
    }

    /// <summary>Fails any parse, proving a path never reached the builder.</summary>
    private sealed class RefusingBuilderSource : IDataStoreConnectionStringBuilderSource
    {
        public DbConnectionStringBuilder CreateBuilder(string connectionString) =>
            throw new InvalidOperationException("The builder must not be asked to parse this value.");
    }

    [TestFixture]
    public class Given_a_null_stored_value : ConnectionStringReaderTests
    {
        private string? _result = "unset";

        [SetUp]
        public async Task Act() => _result = await CreateReader().ReadAsync(null, DataStoreRow);

        [Test]
        public void It_returns_null() => _result.Should().BeNull();

        [Test]
        public void It_does_not_decrypt() => Encryption.Decrypts.Should().Be(0);
    }

    [TestFixture]
    public class Given_a_token_free_value_stored_before_this_change : ConnectionStringReaderTests
    {
        private string? _result;

        [SetUp]
        public async Task Act() =>
            _result = await CreateReader(new RefusingBuilderSource())
                .ReadAsync(Convert.FromBase64String(TokenFreeCipherText), DataStoreRow);

        [Test]
        public void It_returns_the_stored_bytes_unchanged() => _result.Should().Be(TokenFreeCipherText);

        [Test]
        public void It_decrypts_the_value() => Encryption.Decrypts.Should().Be(1);

        [Test]
        public void It_does_not_re_encrypt() => Encryption.Encrypts.Should().Be(0);

        [Test]
        public void It_does_not_call_the_resolver() => Resolver.Calls.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_token_free_value_no_engine_parses : ConnectionStringReaderTests
    {
        private string? _result;

        [SetUp]
        public async Task Act() =>
            _result = await CreateReader()
                .ReadAsync(Convert.FromBase64String(UnparseableTokenFreeCipherText), DataStoreRow);

        [Test]
        public void It_returns_the_stored_bytes_unchanged() =>
            _result.Should().Be(UnparseableTokenFreeCipherText);
    }

    [TestFixture]
    public class Given_a_value_carrying_only_malformed_references : ConnectionStringReaderTests
    {
        private string? _result;

        [SetUp]
        public async Task Act() =>
            _result = await CreateReader(new RefusingBuilderSource())
                .ReadAsync(Convert.FromBase64String(MalformedReferencesCipherText), DataStoreRow);

        [Test]
        public void It_returns_the_stored_bytes_unchanged() =>
            _result.Should().Be(MalformedReferencesCipherText);

        [Test]
        public void It_leaves_the_malformed_text_verbatim() =>
            Decrypted(_result!).Should().Be("Host=db;Password=p${secret:}x${secret:a b}${secret:");

        [Test]
        public void It_does_not_call_the_resolver() => Resolver.Calls.Should().BeEmpty();
    }

    /// <summary>Not a whole number of AES blocks after the initialization vector, so decryption throws.</summary>
    protected static readonly byte[] Undecryptable = [.. Enumerable.Range(0, 20).Select(i => (byte)i)];

    [TestFixture("data store")]
    [TestFixture("derivative resource")]
    public class Given_a_value_that_cannot_be_decrypted(string rowKind) : ConnectionStringReaderTests
    {
        private ConnectionStringRow _row = null!;
        private ConnectionStringReadException _failure = null!;

        [SetUp]
        public async Task Act()
        {
            _row = rowKind == "data store" ? DataStoreRow : ResourceDerivativeRow;
            _failure = await ReadFailure(() => CreateReader().ReadAsync(Undecryptable, _row));
        }

        [Test]
        public void It_fails_naming_the_row() =>
            _failure.Message.Should().Be($"The stored connection string for {_row} could not be decrypted.");

        [Test]
        public void It_does_not_mention_secrets_or_resolvers() =>
            _failure.Message.Should().NotContainAny("secret", "Secret", "resolver", "Resolver");

        [Test]
        public void It_carries_no_inner_exception() => _failure.InnerException.Should().BeNull();

        [Test]
        public void It_does_not_call_the_resolver() => Resolver.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// A derivative still under an old encryption key, read as part of its data store, reads as not
    /// configured, as DMS treats a derivative it cannot decrypt, rather than failing the read and
    /// emptying the derivatives of every data store beside it.
    /// </summary>
    [TestFixture]
    public class Given_a_nested_derivative_that_cannot_be_decrypted : ConnectionStringReaderTests
    {
        private string? _result = "unset";

        [SetUp]
        public async Task Act()
        {
            Tenant.Context = new TenantContext.Multitenant(42, "district-a");
            _result = await CreateReader().ReadAsync(Undecryptable, NestedDerivativeRow);
        }

        [Test]
        public void It_reads_as_not_configured() => _result.Should().BeNull();

        [Test]
        public void It_logs_one_warning_naming_the_row_and_the_problem() =>
            Logger
                .Entries.Should()
                .ContainSingle()
                .Which.Should()
                .Match<LogEntry>(entry =>
                    entry.Level == LogLevel.Warning
                    && Equals(entry.Field("DerivativeId"), 7L)
                    && Equals(entry.Field("DataStoreId"), 5L)
                    && (string)entry.Field("Tenant")! == "district-a"
                    && (string)entry.Field("Problem")! == "could not be decrypted"
                );

        [Test]
        public void It_does_not_call_the_resolver() => Resolver.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// The measured cases from the design: each value must reach the driver exactly, and a textual
    /// substitution of the same values is recorded alongside so a rewrite that reintroduces one fails.
    /// </summary>
    [TestFixture("postgresql")]
    [TestFixture("mssql")]
    public class Given_a_resolved_value_needing_the_providers_quoting(string engine)
        : ConnectionStringReaderTests
    {
        private const string Token = "${secret:db/password}";

        private IDataStoreConnectionStringBuilderSource Builder =>
            engine == "postgresql" ? _postgresql : _sqlServer;

        private string Template =>
            engine == "postgresql"
                ? $"host=db;username=edfi;database=edfi;password={Token}"
                : $"Server=db;User Id=sa;Database=edfi;Password={Token};Encrypt=False";

        private string Rendering(string password) =>
            engine == "postgresql"
                ? $"Host=db;Username=edfi;Database=edfi;Password={password}"
                : $"Data Source=db;Initial Catalog=edfi;User ID=sa;Password={password};Encrypt=False";

        private async Task<string> Read(string secret)
        {
            Resolver.Behavior = (_, _) => ValueTask.FromResult(secret);
            return (await CreateReader(Builder).ReadAsync(Stored(Template), DataStoreRow))!;
        }

        [TestCase("simple", "simple")]
        [TestCase("has\"quote", "'has\"quote'")]
        [TestCase("has=equals", "\"has=equals\"")]
        [TestCase("has'apos", "\"has'apos\"")]
        [TestCase("has;semi", "\"has;semi\"")]
        [TestCase("a;b=c", "\"a;b=c\"")]
        [TestCase(" leading-space", "\" leading-space\"")]
        public async Task It_returns_the_builders_rendering(string secret, string renderedPassword) =>
            Decrypted(await Read(secret)).Should().Be(Rendering(renderedPassword));

        [TestCase("simple")]
        [TestCase("has\"quote")]
        [TestCase("has=equals")]
        [TestCase("has'apos")]
        [TestCase("has;semi")]
        [TestCase("a;b=c")]
        [TestCase(" leading-space")]
        public async Task It_hands_the_driver_the_exact_value(string secret) =>
            Builder.CreateBuilder(Decrypted(await Read(secret)))["Password"].Should().Be(secret);

        [Test]
        public async Task It_returns_the_builders_text_rather_than_the_operators() =>
            Decrypted(await Read("simple")).Should().NotBe(Template.Replace(Token, "simple"));

        [Test]
        public async Task It_re_encrypts_the_substituted_value()
        {
            await Read("simple");
            Encryption.Encrypts.Should().Be(1);
        }

        [TestCase("simple", "round-trips")]
        [TestCase("has\"quote", "round-trips")]
        [TestCase("has=equals", "round-trips")]
        [TestCase("has'apos", "round-trips")]
        [TestCase("has;semi", "throws")]
        [TestCase("a;b=c", "throws")]
        [TestCase(" leading-space", "loses the leading space")]
        public void It_records_what_textual_substitution_would_do(string secret, string outcome)
        {
            string textual = Template.Replace(Token, secret);
            string observed;

            try
            {
                object password = Builder.CreateBuilder(textual)["Password"];
                observed = $"becomes {password}";

                if (Equals(password, secret))
                {
                    observed = "round-trips";
                }
                else if (Equals(password, secret.TrimStart()))
                {
                    observed = "loses the leading space";
                }
            }
            catch (ArgumentException)
            {
                observed = "throws";
            }

            observed.Should().Be(outcome);
        }
    }

    [TestFixture]
    public class Given_several_references_in_several_values : ConnectionStringReaderTests
    {
        private DbConnectionStringBuilder _result = null!;

        [SetUp]
        public async Task Act()
        {
            Resolver.Behavior = (reference, _) => ValueTask.FromResult($"<{reference.Name}>");
            string? read = await CreateReader()
                .ReadAsync(
                    Stored(
                        "Host=db;Username=${secret:user};Password=pre${secret:a}${secret:b}mid${secret:a}post"
                    ),
                    DataStoreRow
                );
            _result = _postgresql.CreateBuilder(Decrypted(read!));
        }

        [Test]
        public void It_resolves_a_value_holding_one_reference() => _result["Username"].Should().Be("<user>");

        [Test]
        public void It_resolves_adjacent_references_and_keeps_the_literal_segments() =>
            _result["Password"].Should().Be("pre<a><b>mid<a>post");

        [Test]
        public void It_asks_the_resolver_once_per_name() =>
            Resolver.Calls.Select(call => call.Name).Should().Equal("user", "a", "b");
    }

    [TestFixture]
    public class Given_a_resolved_value_that_itself_reads_as_a_reference : ConnectionStringReaderTests
    {
        private string _password = null!;

        [SetUp]
        public async Task Act()
        {
            Resolver.Behavior = (_, _) => ValueTask.FromResult("${secret:other}");
            string? read = await CreateReader()
                .ReadAsync(Stored("Host=db;Password=${secret:first}"), DataStoreRow);
            _password = (string)_postgresql.CreateBuilder(Decrypted(read!))["Password"];
        }

        [Test]
        public void It_writes_the_value_through_unchanged() => _password.Should().Be("${secret:other}");

        [Test]
        public void It_does_not_scan_the_resolved_value() =>
            Resolver.Calls.Select(call => call.Name).Should().Equal("first");
    }

    [TestFixture("single-tenant")]
    [TestFixture("multi-tenant")]
    public class Given_the_request_tenant(string tenancy) : ConnectionStringReaderTests
    {
        [SetUp]
        public async Task Act()
        {
            if (tenancy == "multi-tenant")
            {
                Tenant.Context = new TenantContext.Multitenant(42, "district-a");
            }

            await CreateReader().ReadAsync(Stored("Host=db;Password=${secret:prod/dms}"), DataStoreRow);
        }

        [Test]
        public void It_passes_the_name_and_the_tenant() =>
            Resolver
                .Calls.Should()
                .Equal(new SecretReference("prod/dms", tenancy == "multi-tenant" ? "district-a" : null));
    }

    [TestFixture]
    public class Given_no_resolver_is_registered : ConnectionStringReaderTests
    {
        private ConnectionStringReadException _failure = null!;
        private ConnectionStringReadException _resolverFailure = null!;
        private string? _nested = "unset";

        [SetUp]
        public async Task Act()
        {
            byte[] stored = Stored("Host=db;Password=${secret:prod/dms}");
            _failure = await ReadFailure(() =>
                CreateReader(withResolver: false).ReadAsync(stored, DataStoreRow)
            );
            _nested = await CreateReader(withResolver: false).ReadAsync(stored, NestedDerivativeRow);

            Resolver.Behavior = (_, _) => throw new InvalidOperationException(ResolverFailureText);
            _resolverFailure = await ReadFailure(() => CreateReader().ReadAsync(stored, DataStoreRow));
        }

        [Test]
        public void It_fails_the_data_store_read_naming_the_row_the_token_and_the_contract() =>
            _failure
                .Message.Should()
                .Be(
                    "The stored connection string for data store 5 cannot be read: secret prod/dms needs a Configuration Service plugin registering ISecretResolver, and none is registered."
                );

        [Test]
        public void It_uses_a_message_distinct_from_a_failed_resolver() =>
            _failure.Message.Should().NotBe(_resolverFailure.Message);

        [Test]
        public void It_treats_a_nested_derivative_as_not_configured() => _nested.Should().BeNull();

        [Test]
        public void It_logs_the_missing_contract_for_the_data_store() =>
            Logger
                .Entries.Should()
                .Contain(entry =>
                    entry.Level == LogLevel.Error
                    && Equals(entry.Field("Row"), "data store 5")
                    && Equals(entry.Field("Token"), "prod/dms")
                    && entry.Message.Contains("ISecretResolver")
                );
    }

    [TestFixture("throws")]
    [TestFixture("throws synchronously")]
    [TestFixture("cancels")]
    [TestFixture("returns null")]
    [TestFixture("returns empty")]
    public class Given_a_resolver_that_fails(string failure) : ConnectionStringReaderTests
    {
        private readonly byte[] _stored = Stored("Host=db;Password=${secret:prod/dms}");
        private ConnectionStringReadException _dataStoreFailure = null!;
        private ConnectionStringReadException _derivativeFailure = null!;
        private string? _nested = "unset";

        [SetUp]
        public async Task Act()
        {
            Tenant.Context = new TenantContext.Multitenant(42, "district-a");
            Resolver.Behavior = FailingBehavior(failure);

            ConnectionStringReader reader = CreateReader();
            _dataStoreFailure = await ReadFailure(() => reader.ReadAsync(_stored, DataStoreRow));
            _derivativeFailure = await ReadFailure(() => reader.ReadAsync(_stored, ResourceDerivativeRow));
            _nested = await reader.ReadAsync(_stored, NestedDerivativeRow);
        }

        private static Func<SecretReference, CancellationToken, ValueTask<string>> FailingBehavior(
            string failure
        )
        {
            switch (failure)
            {
                case "throws":
                    return ThrowsAfterYielding;
                case "throws synchronously":
                    return (_, _) => throw new InvalidOperationException(ResolverFailureText);
                case "cancels":
                    return (_, _) => ValueTask.FromCanceled<string>(new CancellationToken(true));
                case "returns null":
                    return (_, _) => ValueTask.FromResult<string>(null!);
                default:
                    return (_, _) => ValueTask.FromResult(string.Empty);
            }
        }

        private static async ValueTask<string> ThrowsAfterYielding(
            SecretReference reference,
            CancellationToken token
        )
        {
            await Task.Yield();
            throw new InvalidOperationException(ResolverFailureText);
        }

        private string ExpectedOutcome =>
            failure switch
            {
                "throws" or "throws synchronously" =>
                    "the resolver failed (System.InvalidOperationException)",
                "cancels" => "the resolver was cancelled (System.Threading.Tasks.TaskCanceledException)",
                _ => "the resolver returned no value",
            };

        [Test]
        public void It_fails_the_data_store_read() =>
            _dataStoreFailure
                .Message.Should()
                .Be(
                    $"The stored connection string for data store 5 cannot be read: secret prod/dms could not be resolved: {ExpectedOutcome}."
                );

        [Test]
        public void It_fails_the_standalone_derivative_read() =>
            _derivativeFailure
                .Message.Should()
                .StartWith($"The stored connection string for {ResourceDerivativeRow}");

        [Test]
        public void It_treats_the_nested_derivative_as_not_configured() => _nested.Should().BeNull();

        [Test]
        public void It_carries_no_inner_exception() => _dataStoreFailure.InnerException.Should().BeNull();

        [Test]
        public void It_keeps_the_resolvers_text_out_of_the_failure() =>
            _dataStoreFailure.Message.Should().NotContain(ResolverFailureText);

        [Test]
        public void It_keeps_the_resolvers_text_out_of_the_log() =>
            LoggedText().Should().NotContain(text => text.Contains(ResolverFailureText));

        [Test]
        public void It_logs_the_data_store_the_tenant_and_the_token() =>
            Logger
                .Entries.Should()
                .Contain(entry =>
                    entry.Level == LogLevel.Error
                    && Equals(entry.Field("Row"), "data store 5")
                    && Equals(entry.Field("Tenant"), "district-a")
                    && Equals(entry.Field("Token"), "prod/dms")
                    && Equals(entry.Field("Outcome"), $"could not be resolved: {ExpectedOutcome}")
                );

        [Test]
        public void It_logs_the_nested_derivatives_parent_tenant_type_and_token() =>
            Logger
                .Entries.Should()
                .Contain(entry =>
                    entry.Level == LogLevel.Warning
                    && Equals(entry.Field("DataStoreId"), 5L)
                    && Equals(entry.Field("DerivativeType"), "ReadReplica")
                    && Equals(entry.Field("Tenant"), "district-a")
                    && Equals(entry.Field("Token"), "prod/dms")
                );

        [Test]
        public void It_caches_nothing_so_each_read_asks_again() => Resolver.Calls.Should().HaveCount(3);
    }

    [TestFixture]
    public class Given_a_resolver_that_succeeds : ConnectionStringReaderTests
    {
        [SetUp]
        public async Task Act()
        {
            Tenant.Context = new TenantContext.Multitenant(42, "district-a");
            await CreateReader().ReadAsync(Stored("Host=db;Password=${secret:prod/dms}"), DataStoreRow);
        }

        [Test]
        public void It_never_logs_the_resolved_value() =>
            LoggedText().Should().NotContain(text => text.Contains(ResolvedSecret));
    }

    [TestFixture]
    public class Given_a_failure_then_a_recovery : ConnectionStringReaderTests
    {
        private string _password = null!;

        [SetUp]
        public async Task Act()
        {
            byte[] stored = Stored("Host=db;Password=${secret:prod/dms}");
            Resolver.Behavior = (_, _) => throw new InvalidOperationException(ResolverFailureText);
            await ReadFailure(() => CreateReader().ReadAsync(stored, DataStoreRow));

            Resolver.Behavior = (_, _) => ValueTask.FromResult("recovered");
            string? read = await CreateReader().ReadAsync(stored, DataStoreRow);
            _password = (string)_postgresql.CreateBuilder(Decrypted(read!))["Password"];
        }

        [Test]
        public void It_resolves_on_the_next_read() => _password.Should().Be("recovered");
    }

    [TestFixture]
    public class Given_two_rows_sharing_one_failing_fetch : ConnectionStringReaderTests
    {
        private ConnectionStringReadException _first = null!;
        private ConnectionStringReadException _second = null!;

        [SetUp]
        public async Task Act()
        {
            TaskCompletionSource<string> gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Resolver.Behavior = (_, _) => new ValueTask<string>(gate.Task);
            byte[] stored = Stored("Host=db;Password=${secret:prod/dms}");

            Task<ConnectionStringReadException> first = ReadFailure(() =>
                CreateReader().ReadAsync(stored, new ConnectionStringRow.DataStore(1))
            );
            Task<ConnectionStringReadException> second = ReadFailure(() =>
                CreateReader().ReadAsync(stored, new ConnectionStringRow.DataStore(2))
            );

            gate.SetException(new InvalidOperationException(ResolverFailureText));
            _first = await first;
            _second = await second;
        }

        [Test]
        public void It_calls_the_resolver_once() => Resolver.Calls.Should().ContainSingle();

        [Test]
        public void It_names_each_callers_own_row()
        {
            _first.Message.Should().StartWith("The stored connection string for data store 1 ");
            _second.Message.Should().StartWith("The stored connection string for data store 2 ");
        }

        [Test]
        public void It_logs_each_callers_own_row() =>
            Logger
                .Entries.Select(entry => entry.Field("Row"))
                .Should()
                .BeEquivalentTo(["data store 1", "data store 2"]);
    }

    /// <summary>
    /// Both providers refuse a value holding a NUL, and their message may repeat it. The refusal is
    /// the row's, so a nested derivative reads as not configured rather than failing its data store's
    /// read, and neither the value nor the provider's text reaches the message or the log.
    /// </summary>
    [TestFixture("postgresql")]
    [TestFixture("mssql")]
    public class Given_a_resolved_value_the_provider_refuses(string engine) : ConnectionStringReaderTests
    {
        private const string RefusedSecret = "before\0after-hunter2";

        private ConnectionStringReadException _failure = null!;
        private string? _nested = "unset";

        private IDataStoreConnectionStringBuilderSource Builder =>
            engine == "postgresql" ? _postgresql : _sqlServer;

        [SetUp]
        public async Task Act()
        {
            Resolver.Behavior = (_, _) => ValueTask.FromResult(RefusedSecret);
            byte[] stored = Stored("Server=db;Password=${secret:prod/dms}");

            _failure = await ReadFailure(() => CreateReader(Builder).ReadAsync(stored, DataStoreRow));
            _nested = await CreateReader(Builder).ReadAsync(stored, NestedDerivativeRow);
        }

        [Test]
        public void It_fails_a_data_store_naming_the_problem() =>
            _failure
                .Message.Should()
                .Be("The stored connection string for data store 5 could not take a resolved secret value.");

        [Test]
        public void It_reads_a_nested_derivative_as_not_configured() => _nested.Should().BeNull();

        [Test]
        public void It_carries_no_inner_exception() => _failure.InnerException.Should().BeNull();

        [Test]
        public void It_keeps_the_value_out_of_the_log() =>
            LoggedText().Should().NotContain(text => text.Contains("hunter2"));
    }

    [TestFixture]
    public class Given_a_tokenized_value_the_engine_cannot_parse : ConnectionStringReaderTests
    {
        private ConnectionStringReadException _failure = null!;
        private ConnectionStringReadException _resourceFailure = null!;
        private string? _nested = "unset";

        [SetUp]
        public async Task Act()
        {
            byte[] stored = Stored("Host=db;Password=${secret:prod/dms};dangling");

            _failure = await ReadFailure(() => CreateReader().ReadAsync(stored, DataStoreRow));
            _resourceFailure = await ReadFailure(() =>
                CreateReader().ReadAsync(stored, ResourceDerivativeRow)
            );
            _nested = await CreateReader().ReadAsync(stored, NestedDerivativeRow);
        }

        [Test]
        public void It_fails_with_the_parse_message() =>
            _failure
                .Message.Should()
                .Be(
                    "The stored connection string for data store 5 could not be parsed by the configured database engine."
                );

        [Test]
        public void It_fails_a_derivative_read_as_the_resource() =>
            _resourceFailure
                .Message.Should()
                .EndWith("could not be parsed by the configured database engine.");

        [Test]
        public void It_reads_a_nested_derivative_as_not_configured() => _nested.Should().BeNull();

        [Test]
        public void It_carries_no_inner_exception() => _failure.InnerException.Should().BeNull();

        [Test]
        public void It_fails_before_asking_the_resolver() => Resolver.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// Both engines accept a keyword assigned more than once, and a synonym of it, keeping the last
    /// value. Resolution follows the provider: the value it kept is the one resolved, a discarded
    /// reference is never resolved, and the rendering never carries it.
    /// </summary>
    [TestFixture("postgresql")]
    [TestFixture("mssql")]
    public class Given_a_keyword_assigned_more_than_once(string engine) : ConnectionStringReaderTests
    {
        private IDataStoreConnectionStringBuilderSource Builder =>
            engine == "postgresql" ? _postgresql : _sqlServer;

        private IDataStoreConnectionStringValidator Validator => (IDataStoreConnectionStringValidator)Builder;

        private string Head => engine == "postgresql" ? "Host=db;" : "Server=db;";

        private string RenderedHead => engine == "postgresql" ? "Host=db;" : "Data Source=db;";

        private async Task<string> Read(string tail)
        {
            Resolver.Behavior = (reference, _) => ValueTask.FromResult($"<{reference.Name}>");
            return Decrypted((await CreateReader(Builder).ReadAsync(Stored(Head + tail), DataStoreRow))!);
        }

        [TestCase("Password=${secret:a};Password=${secret:b}", "Password=<b>", "b")]
        [TestCase("Password=${secret:a};PWD=${secret:b}", "Password=<b>", "b")]
        [TestCase("Password=literal;PWD=${secret:b}", "Password=<b>", "b")]
        [TestCase("Password=${secret:a};Password=literal", "Password=literal")]
        [TestCase("PWD=${secret:a};Password=literal", "Password=literal")]
        public async Task It_resolves_only_the_value_the_provider_kept(
            string tail,
            string renderedTail,
            params string[] resolved
        )
        {
            Validator.Validate(Head + tail).Should().BeOfType<ConnectionStringValidationResult.Valid>();

            (await Read(tail)).Should().Be(RenderedHead + renderedTail);
            Resolver.Calls.Select(call => call.Name).Should().Equal(resolved);
        }

        [Test]
        public async Task It_re_renders_a_value_whose_every_reference_was_discarded()
        {
            string rendered = await Read("Password=${secret:a};Password=literal");

            rendered.Should().NotContain("${secret:");
            Encryption.Encrypts.Should().Be(1);
        }
    }

    /// <summary>
    /// The secret's name is logged exactly as the resolver was asked for it, including the '@' and '+'
    /// the grammar permits, while the tenant and derivative type, which arrive from clients, are
    /// sanitized.
    /// </summary>
    [TestFixture]
    public class Given_a_failure_for_a_name_using_every_permitted_punctuation : ConnectionStringReaderTests
    {
        private const string Name = "prod/user@host+version_1.2-a:b";

        private ConnectionStringReadException _failure = null!;
        private readonly ConnectionStringRow _nestedRow = new ConnectionStringRow.Derivative(
            7,
            5,
            "Read\r\nReplica\u0007",
            DerivativeReadMode.PartOfDataStore
        );

        [SetUp]
        public async Task Act()
        {
            Tenant.Context = new TenantContext.Multitenant(42, "district\r\n-a\u0000");
            Resolver.Behavior = (_, _) => throw new InvalidOperationException(ResolverFailureText);
            byte[] stored = Stored($"Host=db;Password=${{secret:{Name}}}");

            _failure = await ReadFailure(() => CreateReader().ReadAsync(stored, DataStoreRow));
            await CreateReader().ReadAsync(stored, _nestedRow);
        }

        [Test]
        public void It_asks_the_resolver_for_the_exact_name() =>
            Resolver.Calls.Select(call => call.Name).Should().AllBe(Name);

        [Test]
        public void It_names_the_exact_secret_in_the_failure() =>
            _failure.Message.Should().Contain($"secret {Name} could not be resolved");

        [Test]
        public void It_logs_the_exact_name_on_the_resource_path() =>
            Logger
                .Entries.Should()
                .Contain(entry => entry.Level == LogLevel.Error && Equals(entry.Field("Token"), Name));

        [Test]
        public void It_logs_the_exact_name_on_the_nested_path() =>
            Logger
                .Entries.Should()
                .Contain(entry => entry.Level == LogLevel.Warning && Equals(entry.Field("Token"), Name));

        [Test]
        public void It_sanitizes_the_tenant() =>
            Logger.Entries.Select(entry => entry.Field("Tenant")).Should().AllBeEquivalentTo("district-a");

        [Test]
        public void It_sanitizes_the_derivative_type() =>
            Logger
                .Entries.Where(entry => entry.Level == LogLevel.Warning)
                .Select(entry => entry.Field("DerivativeType"))
                .Should()
                .Equal("ReadReplica");
    }

    /// <summary>
    /// The deadline is armed before the resolver is invoked, so advancing the fake clock after the
    /// resolver has been entered fires it deterministically.
    /// </summary>
    [TestFixture]
    public class Given_a_resolver_that_never_completes_and_ignores_cancellation : ConnectionStringReaderTests
    {
        private ConnectionStringReadException _failure = null!;
        private CancellationToken _token;

        [SetUp]
        public async Task Act()
        {
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Resolver.Behavior = (_, cancellationToken) =>
            {
                _token = cancellationToken;
                entered.SetResult();
                return new ValueTask<string>(new TaskCompletionSource<string>().Task);
            };

            Task<ConnectionStringReadException> read = ReadFailure(() =>
                CreateReader().ReadAsync(Stored("Host=db;Password=${secret:prod/dms}"), DataStoreRow)
            );
            await entered.Task.WaitAsync(SafetyBound);
            Time.Advance(TimeSpan.FromSeconds(TimeoutSeconds));
            _failure = await read.WaitAsync(SafetyBound);
        }

        [Test]
        public void It_fails_naming_the_timeout() =>
            _failure
                .Message.Should()
                .EndWith("the resolver did not return within the 10 seconds this read may spend on it.");

        [Test]
        public void It_passed_a_cancellable_token() => _token.CanBeCanceled.Should().BeTrue();

        [Test]
        public void It_requests_cancellation_at_the_deadline() =>
            _token.IsCancellationRequested.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_resolver_that_blocks_before_returning : ConnectionStringReaderTests
    {
        private readonly ManualResetEventSlim _release = new();
        private ConnectionStringReadException _failure = null!;

        [SetUp]
        public async Task Act()
        {
            _release.Reset();
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Resolver.Behavior = (_, _) =>
            {
                entered.SetResult();
                _release.Wait();
                return ValueTask.FromResult("too late");
            };

            // Started off the test thread, so a seam invoking the resolver on the caller's thread fails
            // this test at the safety bound instead of blocking it.
            ConnectionStringReader reader = CreateReader();
            byte[] stored = Stored("Host=db;Password=${secret:prod/dms}");
            Task<ConnectionStringReadException> read = Task.Run(() =>
                ReadFailure(() => reader.ReadAsync(stored, DataStoreRow))
            );
            await entered.Task.WaitAsync(SafetyBound);
            Time.Advance(TimeSpan.FromSeconds(TimeoutSeconds));
            _failure = await read.WaitAsync(SafetyBound);
        }

        [TearDown]
        public void Release() => _release.Set();

        [OneTimeTearDown]
        public void Dispose() => _release.Dispose();

        [Test]
        public void It_fails_naming_the_timeout() =>
            _failure
                .Message.Should()
                .EndWith("the resolver did not return within the 10 seconds this read may spend on it.");
    }

    [TestFixture]
    public class Given_a_cooperative_resolver_at_the_deadline : ConnectionStringReaderTests
    {
        private Task _observed = null!;

        [SetUp]
        public async Task Act()
        {
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Resolver.Behavior = async (_, cancellationToken) =>
            {
                cancellationToken.Register(() => cancelled.SetResult());
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return "unreachable";
            };

            Task<ConnectionStringReadException> read = ReadFailure(() =>
                CreateReader().ReadAsync(Stored("Host=db;Password=${secret:prod/dms}"), DataStoreRow)
            );
            await entered.Task.WaitAsync(SafetyBound);
            Time.Advance(TimeSpan.FromSeconds(TimeoutSeconds));
            await read.WaitAsync(SafetyBound);
            _observed = cancelled.Task;
            await _observed.WaitAsync(SafetyBound);
        }

        [Test]
        public void It_cancels_the_resolvers_token() => _observed.IsCompletedSuccessfully.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_resolver_that_returns_just_inside_the_deadline : ConnectionStringReaderTests
    {
        private string _password = null!;

        [SetUp]
        public async Task Act()
        {
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string> value = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Resolver.Behavior = (_, _) =>
            {
                entered.SetResult();
                return new ValueTask<string>(value.Task);
            };

            Task<string?> read = CreateReader()
                .ReadAsync(Stored("Host=db;Password=${secret:prod/dms}"), DataStoreRow);
            await entered.Task.WaitAsync(SafetyBound);
            Time.Advance(TimeSpan.FromSeconds(TimeoutSeconds) - TimeSpan.FromTicks(1));
            value.SetResult("in-time");
            _password = (string)
                _postgresql.CreateBuilder(Decrypted((await read.WaitAsync(SafetyBound))!))["Password"];
        }

        [Test]
        public void It_succeeds() => _password.Should().Be("in-time");
    }

    [TestFixture]
    public class Given_a_timed_out_call_that_returns_after_a_later_fetch : ConnectionStringReaderTests
    {
        private string _passwordAfterLateReturn = null!;

        [SetUp]
        public async Task Act()
        {
            byte[] stored = Stored("Host=db;Password=${secret:prod/dms}");
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string> late = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Resolver.Behavior = (_, _) =>
            {
                entered.SetResult();
                return new ValueTask<string>(late.Task);
            };

            ConnectionStringReader timedOutReader = CreateReader();
            Task<ConnectionStringReadException> timedOut = ReadFailure(() =>
                timedOutReader.ReadAsync(stored, DataStoreRow)
            );
            await entered.Task.WaitAsync(SafetyBound);
            Time.Advance(TimeSpan.FromSeconds(TimeoutSeconds));
            await timedOut.WaitAsync(SafetyBound);

            Resolver.Behavior = (_, _) => ValueTask.FromResult("fresh");
            await CreateReader().ReadAsync(stored, DataStoreRow);

            // Waits until the late outcome has been handled, so a seam that let it reach the cache
            // fails this test every time rather than only when the handling happens to run first.
            late.SetResult("stale");
            await timedOutReader.AbandonedCallObserved.WaitAsync(SafetyBound);
            string? read = await CreateReader().ReadAsync(stored, DataStoreRow);
            _passwordAfterLateReturn = (string)_postgresql.CreateBuilder(Decrypted(read!))["Password"];
        }

        [Test]
        public void It_asks_again_after_the_timeout() => Resolver.Calls.Should().HaveCount(2);

        [Test]
        public void It_keeps_the_later_value_cached() => _passwordAfterLateReturn.Should().Be("fresh");
    }

    /// <summary>
    /// A hung resolver is asked once per read, not once per row: rows are read one at a time, so
    /// asking again for each would make a read of many derivatives wait out one timeout apiece. The
    /// rows use different names, so it is the timeout and not a shared fetch that stops the later
    /// calls, and a value already cached is still served.
    /// </summary>
    [TestFixture]
    public class Given_a_resolver_that_hangs_for_a_read_of_several_rows : ConnectionStringReaderTests
    {
        private readonly List<string?> _nested = [];
        private string? _cachedName;
        private ConnectionStringReadException _dataStoreFailure = null!;
        private string? _nextRead;

        [SetUp]
        public async Task Act()
        {
            _nested.Clear();
            Resolver.Behavior = (_, _) => ValueTask.FromResult("cached");
            await CreateReader().ReadAsync(Stored("Host=db;Password=${secret:warm}"), DataStoreRow);

            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Resolver.Behavior = (_, _) =>
            {
                entered.TrySetResult();
                return new ValueTask<string>(new TaskCompletionSource<string>().Task);
            };

            ConnectionStringReader reader = CreateReader();
            Task<string?> first = reader.ReadAsync(
                Stored("Host=db;Password=${secret:a}"),
                NestedDerivativeRow
            );
            await entered.Task.WaitAsync(SafetyBound);
            Time.Advance(TimeSpan.FromSeconds(TimeoutSeconds));
            _nested.Add(await first.WaitAsync(SafetyBound));

            // Each of these would hang until the clock moved again if it asked the resolver.
            _nested.Add(
                await reader
                    .ReadAsync(Stored("Host=db;Password=${secret:b}"), NestedDerivativeRow)
                    .WaitAsync(SafetyBound)
            );
            _nested.Add(
                await reader
                    .ReadAsync(Stored("Host=db;Password=${secret:c}"), NestedDerivativeRow)
                    .WaitAsync(SafetyBound)
            );
            string? cached = await reader
                .ReadAsync(Stored("Host=db;Password=${secret:warm}"), NestedDerivativeRow)
                .WaitAsync(SafetyBound);
            _cachedName = (string)_postgresql.CreateBuilder(Decrypted(cached!))["Password"];
            _dataStoreFailure = await ReadFailure(() =>
                    reader.ReadAsync(Stored("Host=db;Password=${secret:d}"), DataStoreRow)
                )
                .WaitAsync(SafetyBound);

            // A later read is a new instance, and asks again.
            Resolver.Behavior = (_, _) => ValueTask.FromResult("recovered");
            _nextRead = await CreateReader()
                .ReadAsync(Stored("Host=db;Password=${secret:b}"), NestedDerivativeRow)
                .WaitAsync(SafetyBound);
        }

        [Test]
        public void It_asks_the_hung_resolver_once() =>
            Resolver.Calls.Select(call => call.Name).Should().Equal("warm", "a", "b");

        [Test]
        public void It_reads_every_nested_row_as_not_configured() => _nested.Should().Equal(null, null, null);

        [Test]
        public void It_still_serves_a_cached_value() => _cachedName.Should().Be("cached");

        [Test]
        public void It_fails_a_data_store_naming_the_earlier_timeout() =>
            _dataStoreFailure
                .Message.Should()
                .EndWith("this read had already waited the 10 seconds it may spend on the resolver.");

        [Test]
        public void It_asks_again_on_the_next_read() =>
            _postgresql.CreateBuilder(Decrypted(_nextRead!))["Password"].Should().Be("recovered");
    }

    /// <summary>
    /// The timeout is the read's, not each call's: a store that answers every call just inside the
    /// timeout would otherwise cost one wait per distinct uncached name, and a read of many rows could
    /// outlast DMS's HTTP client. The second call is cut off when the read's time runs out, the third
    /// name is never asked for, and a later read starts with a full allowance.
    /// </summary>
    [TestFixture]
    public class Given_a_resolver_that_answers_each_call_inside_the_timeout : ConnectionStringReaderTests
    {
        private readonly List<string?> _nested = [];
        private ConnectionStringReadException _dataStoreFailure = null!;
        private string? _nextRead;

        [SetUp]
        public async Task Act()
        {
            _nested.Clear();
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            TaskCompletionSource<string> value = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Resolver.Behavior = (_, _) =>
            {
                entered.TrySetResult();
                return new ValueTask<string>(value.Task);
            };

            ConnectionStringReader reader = CreateReader();

            // The first name answers after six of the read's ten seconds.
            Task<string?> first = reader.ReadAsync(
                Stored("Host=db;Password=${secret:a}"),
                NestedDerivativeRow
            );
            await entered.Task.WaitAsync(SafetyBound);
            Time.Advance(TimeSpan.FromSeconds(6));
            value.SetResult("first");
            _nested.Add(await first.WaitAsync(SafetyBound));

            // The second would answer inside its own ten seconds, but the read has four left.
            entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            value = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<string?> second = reader.ReadAsync(
                Stored("Host=db;Password=${secret:b}"),
                NestedDerivativeRow
            );
            await entered.Task.WaitAsync(SafetyBound);
            Time.Advance(TimeSpan.FromSeconds(4));
            _nested.Add(await second.WaitAsync(SafetyBound));

            _dataStoreFailure = await ReadFailure(() =>
                    reader.ReadAsync(Stored("Host=db;Password=${secret:c}"), DataStoreRow)
                )
                .WaitAsync(SafetyBound);

            value.SetResult("late");
            Resolver.Behavior = (_, _) => ValueTask.FromResult("recovered");
            _nextRead = await CreateReader()
                .ReadAsync(Stored("Host=db;Password=${secret:c}"), NestedDerivativeRow)
                .WaitAsync(SafetyBound);
        }

        [Test]
        public void It_asks_only_while_the_read_has_time_left() =>
            Resolver.Calls.Select(call => call.Name).Should().Equal("a", "b", "c");

        [Test]
        public void It_serves_the_value_returned_in_time() =>
            _postgresql.CreateBuilder(Decrypted(_nested[0]!))["Password"].Should().Be("first");

        [Test]
        public void It_reads_the_cut_off_row_as_not_configured() => _nested[1].Should().BeNull();

        [Test]
        public void It_fails_a_later_data_store_naming_the_spent_time() =>
            _dataStoreFailure
                .Message.Should()
                .EndWith("this read had already waited the 10 seconds it may spend on the resolver.");

        [Test]
        public void It_gives_the_next_read_a_full_allowance() =>
            _postgresql.CreateBuilder(Decrypted(_nextRead!))["Password"].Should().Be("recovered");
    }

    /// <summary>
    /// A read that joins a call another read started waits on that call's deadline, not its own. When
    /// the shared call times out first, the joining read reports that reference unresolved, saying it
    /// was the shared call, and still asks for the next reference with the time it has left.
    /// </summary>
    [TestFixture]
    public class Given_a_read_that_joins_a_call_which_times_out_first : ConnectionStringReaderTests
    {
        private ConnectionStringReadException _starterFailure = null!;
        private string? _joinedRead;
        private string? _nextRead;

        [SetUp]
        public async Task Act()
        {
            TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Resolver.Behavior = (reference, _) =>
            {
                if (reference.Name == "next")
                {
                    return ValueTask.FromResult("next-value");
                }

                entered.TrySetResult();
                return new ValueTask<string>(new TaskCompletionSource<string>().Task);
            };

            Task<ConnectionStringReadException> starter = ReadFailure(() =>
                CreateReader().ReadAsync(Stored("Host=db;Password=${secret:shared}"), DataStoreRow)
            );
            await entered.Task.WaitAsync(SafetyBound);
            Time.Advance(TimeSpan.FromSeconds(8));

            // Joins the call in flight, with its own ten seconds starting now.
            ConnectionStringReader joiner = CreateReader();
            Task<string?> joined = joiner.ReadAsync(
                Stored("Host=db;Password=${secret:shared}"),
                NestedDerivativeRow
            );
            Time.Advance(TimeSpan.FromSeconds(2));
            _starterFailure = await starter.WaitAsync(SafetyBound);
            _joinedRead = await joined.WaitAsync(SafetyBound);

            _nextRead = await joiner
                .ReadAsync(Stored("Host=db;Password=${secret:next}"), NestedDerivativeRow)
                .WaitAsync(SafetyBound);
        }

        [Test]
        public void It_fails_the_starting_read_on_its_own_allowance() =>
            _starterFailure
                .Message.Should()
                .EndWith("the resolver did not return within the 10 seconds this read may spend on it.");

        [Test]
        public void It_reads_the_joined_row_as_not_configured() => _joinedRead.Should().BeNull();

        [Test]
        public void It_reports_that_the_joined_call_timed_out() =>
            LoggedText()
                .Should()
                .Contain(
                    "could not be resolved: the call this read joined, which another read started, did not return within its 10 seconds"
                );

        [Test]
        public void It_asks_for_the_next_reference_with_the_time_left() =>
            Resolver.Calls.Select(call => call.Name).Should().Equal("shared", "next");

        [Test]
        public void It_serves_the_next_reference() =>
            _postgresql.CreateBuilder(Decrypted(_nextRead!))["Password"].Should().Be("next-value");
    }
}
