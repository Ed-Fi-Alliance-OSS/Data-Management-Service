// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Dapper;
using EdFi.DmsConfigurationService.Backend.Services;
using EdFi.DmsConfigurationService.DataModel.Infrastructure;
using EdFi.DmsConfigurationService.DataModel.Model.Authorization;
using EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.Infrastructure;
using EdFi.DmsConfigurationService.Secrets;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EdFi.DmsConfigurationService.Backend.Mssql.Tests.Integration;

/// <summary>
/// The read rule through the whole Configuration Service pipeline against a real database: rows are
/// written through the real endpoints and validator, read back through the real endpoints and
/// repositories, and a controlled resolver stands in for a plugin. Logs are captured with their
/// request scopes, so a failure line is correlated to its response the way an operator would.
/// </summary>
public class ConnectionStringResolutionApiTests : DatabaseTest
{
    protected const string Token = "prod/dms@primary+v2";

    /// <summary>What a resolver returned before it started failing, so its absence later means something.</summary>
    protected const string PriorSecret = "prior-secret-value-7f3c";

    /// <summary>A plugin's own failure text, which reads like a secret and must never leave the resolver.</summary>
    protected const string SentinelFailure = "vault said hunter2-sentinel-9b1e";

    protected static string ConnectionStringWithPassword(string password) =>
        $"Server=db;User Id=edfi;Database=edfi;Password={password}";

    private const string StoredParentSql = """
        SELECT ConnectionString FROM dmscs.DataStore WHERE Id = @Id;
        """;

    /// <summary>A resolver whose behavior each test sets, counting every call.</summary>
    protected sealed class ControlledResolver : ISecretResolver
    {
        public Func<SecretReference, string> Behavior { get; set; } =
            reference => $"value-of-{reference.Name}";

        public ConcurrentQueue<SecretReference> Calls { get; } = new();

        public ValueTask<string> ResolveAsync(SecretReference reference, CancellationToken cancellationToken)
        {
            Calls.Enqueue(reference);
            return ValueTask.FromResult(Behavior(reference));
        }
    }

    /// <summary>One captured log line with the request scopes active when it was written.</summary>
    protected sealed record ScopedLog(
        string Category,
        LogLevel Level,
        string Message,
        IReadOnlyDictionary<string, object?> Fields,
        IReadOnlyDictionary<string, object?> Scopes,
        Exception? Exception
    )
    {
        public object? Field(string name) => Fields.GetValueOrDefault(name);

        /// <summary>Every piece of text this line carries, for non-disclosure checks.</summary>
        public IEnumerable<string> Text()
        {
            yield return Message;
            foreach (object? value in Fields.Values.Concat(Scopes.Values))
            {
                yield return value?.ToString() ?? string.Empty;
            }

            for (
                Exception? exception = Exception;
                exception is not null;
                exception = exception.InnerException
            )
            {
                yield return exception.ToString();
            }
        }
    }

    /// <summary>
    /// Captures every log line with its fields and, through the factory's external scope provider,
    /// the scopes active at the time, including the trace identifier the request middleware opens.
    /// </summary>
    protected sealed class ScopedCapturingLoggerProvider : ILoggerProvider, ISupportExternalScope
    {
        private IExternalScopeProvider _scopes = new LoggerExternalScopeProvider();

        public ConcurrentQueue<ScopedLog> Entries { get; } = new();

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, this);

        public void Dispose() { }

        private sealed class Logger(string category, ScopedCapturingLoggerProvider provider) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => provider._scopes.Push(state);

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                Dictionary<string, object?> fields = [];
                if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach ((string key, object? value) in pairs)
                    {
                        fields[key] = value;
                    }
                }

                Dictionary<string, object?> scopes = [];
                provider._scopes.ForEachScope(
                    (scope, values) =>
                    {
                        if (scope is IEnumerable<KeyValuePair<string, object>> scopePairs)
                        {
                            foreach ((string key, object value) in scopePairs)
                            {
                                values[key] = value;
                            }
                        }
                    },
                    scopes
                );

                provider.Entries.Enqueue(
                    new ScopedLog(category, logLevel, formatter(state, exception), fields, scopes, exception)
                );
            }
        }
    }

    private WebApplicationFactory<Program>? _factory;

    protected ControlledResolver Resolver { get; private set; } = null!;
    protected ScopedCapturingLoggerProvider Logs { get; private set; } = null!;
    protected string? TenantName { get; private set; }

    [TearDown]
    public async Task StopHost()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
            _factory = null;
        }

        Logs?.Dispose();
    }

    /// <summary>
    /// Starts the host on the integration database with the controlled resolver registered as a
    /// plugin would register it, and the cache off so each read reaches the resolver. A behavior
    /// passed here is in place before the host starts.
    /// </summary>
    protected async Task StartHost(bool multiTenancy, Func<SecretReference, string>? resolverBehavior = null)
    {
        // Set before the host is built, so a fixture can start the host with a resolver that already fails.
        Resolver = new ControlledResolver();
        if (resolverBehavior is not null)
        {
            Resolver.Behavior = resolverBehavior;
        }

        Logs = new ScopedCapturingLoggerProvider();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("AppSettings:Datastore", "mssql");
            builder.UseSetting("AppSettings:MultiTenancy", multiTenancy.ToString());
            builder.UseSetting(
                "DatabaseSettings:DatabaseConnection",
                MssqlTestConfiguration.DatabaseConnectionString
            );
            builder.UseSetting("SecretsSettings:CacheExpirationSeconds", "0");
            builder.ConfigureServices(services =>
            {
                services.AddTestAuthentication();
                services.AddSingleton<ISecretResolver>(Resolver);
                services.AddSingleton<ILoggerProvider>(Logs);
            });
        });
        _factory.CreateClient().Dispose();

        if (multiTenancy)
        {
            TenantName = $"ResolutionTenant-{Guid.NewGuid():N}";
            using HttpResponseMessage created = await Send(
                HttpMethod.Post,
                "/v3/tenants/",
                new { name = TenantName },
                withTenant: false
            );
            created.StatusCode.Should().Be(HttpStatusCode.Created);
        }
    }

    protected IServiceProvider Services => _factory!.Services;

    protected async Task<HttpResponseMessage> Send(
        HttpMethod method,
        string path,
        object? body = null,
        bool withTenant = true
    )
    {
        using HttpClient client = _factory!.CreateClient();
        using HttpRequestMessage request = new(method, path);
        request.Headers.Add("X-Test-Scope", AuthorizationScopes.AdminScope.Name);
        if (withTenant && TenantName is not null)
        {
            request.Headers.Add("Tenant", TenantName);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return await client.SendAsync(request);
    }

    protected async Task<(HttpStatusCode Status, string Content)> Get(string path)
    {
        using HttpResponseMessage response = await Send(HttpMethod.Get, path);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    protected async Task<int> CreateDataStore(string connectionString)
    {
        using HttpResponseMessage response = await Send(
            HttpMethod.Post,
            "/v3/dataStores/",
            new
            {
                dataStoreType = "Production",
                name = $"Resolution-{Guid.NewGuid():N}",
                connectionString,
            }
        );
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<int>();
    }

    protected async Task<int> CreateDerivative(int dataStoreId, string type, string connectionString)
    {
        using HttpResponseMessage response = await Send(
            HttpMethod.Post,
            "/v3/dataStoreDerivatives/",
            new
            {
                dataStoreId,
                derivativeType = type,
                connectionString,
            }
        );
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["id"]!.GetValue<int>();
    }

    protected string? Password(JsonNode? connectionString)
    {
        if (connectionString is null)
        {
            return null;
        }

        string plainText = Services
            .GetRequiredService<IConnectionStringEncryptionService>()
            .Decrypt(Convert.FromBase64String(connectionString.GetValue<string>()))!;
        return (string?)
            Services.GetRequiredService<IDataStoreConnectionStringBuilderSource>().CreateBuilder(plainText)[
                "Password"
            ];
    }

    protected async Task<string> StoredPlainText(int dataStoreId) =>
        Services
            .GetRequiredService<IConnectionStringEncryptionService>()
            .Decrypt(await Connection!.QuerySingleAsync<byte[]>(StoredParentSql, new { Id = dataStoreId }))!;

    /// <summary>
    /// The response is exactly the generic failure the endpoints have always returned for a
    /// repository failure, and carries none of the reference, the values or the resolver's text.
    /// </summary>
    protected static string AssertGenericFailure(HttpStatusCode status, string content)
    {
        status.Should().Be(HttpStatusCode.InternalServerError);
        JsonNode body = JsonNode.Parse(content)!;
        string correlationId = body["correlationId"]!.GetValue<string>();
        JsonNode.DeepEquals(body, FailureResponse.ForUnknown(correlationId)).Should().BeTrue(content);
        content.Should().NotContainAny(Token, "${secret:", PriorSecret, SentinelFailure, "value-of-");
        return correlationId;
    }

    protected IReadOnlyList<ScopedLog> ReaderLogs(string traceId) =>
        [
            .. Logs.Entries.Where(entry =>
                entry.Category == typeof(ConnectionStringReader).FullName
                && Equals(entry.Scopes.GetValueOrDefault("TraceId"), traceId)
            ),
        ];

    protected void AssertNothingDisclosed() =>
        Logs
            .Entries.SelectMany(entry => entry.Text())
            .Should()
            .NotContain(text => text.Contains(PriorSecret) || text.Contains(SentinelFailure));

    [TestFixture]
    public class Given_a_data_store_created_with_a_secret_reference : ConnectionStringResolutionApiTests
    {
        private readonly string _submitted = ConnectionStringWithPassword($"${{secret:{Token}}}");
        private int _id;

        [SetUp]
        public async Task Arrange()
        {
            await StartHost(multiTenancy: false);
            _id = await CreateDataStore(_submitted);
        }

        [Test]
        public async Task It_stores_the_reference_as_submitted() =>
            (await StoredPlainText(_id)).Should().Be(_submitted);

        [Test]
        public async Task It_returns_the_resolved_value_from_the_collection_read()
        {
            (HttpStatusCode status, string content) = await Get("/v3/dataStores/");

            status.Should().Be(HttpStatusCode.OK);
            JsonNode dataStore = JsonNode
                .Parse(content)!
                .AsArray()
                .Single(d => d!["id"]!.GetValue<int>() == _id)!;
            Password(dataStore["connectionString"]).Should().Be($"value-of-{Token}");
        }

        [Test]
        public async Task It_returns_the_resolved_value_from_the_single_row_read()
        {
            (HttpStatusCode status, string content) = await Get($"/v3/dataStores/{_id}");

            status.Should().Be(HttpStatusCode.OK);
            Password(JsonNode.Parse(content)!["connectionString"]).Should().Be($"value-of-{Token}");
            Resolver.Calls.Should().Equal(new SecretReference(Token, null));
        }
    }

    [TestFixture]
    public class Given_a_resolver_that_starts_failing_in_a_multitenant_deployment
        : ConnectionStringResolutionApiTests
    {
        private int _id;
        private string? _priorPassword;
        private (HttpStatusCode Status, string Content) _collection;
        private (HttpStatusCode Status, string Content) _single;

        [SetUp]
        public async Task Arrange()
        {
            await StartHost(multiTenancy: true);
            _id = await CreateDataStore(ConnectionStringWithPassword($"${{secret:{Token}}}"));

            Resolver.Behavior = _ => PriorSecret;
            _priorPassword = Password(
                JsonNode.Parse((await Get($"/v3/dataStores/{_id}")).Content)!["connectionString"]
            );

            Resolver.Behavior = _ => throw new InvalidOperationException(SentinelFailure);
            _collection = await Get("/v3/dataStores/");
            _single = await Get($"/v3/dataStores/{_id}");
        }

        [Test]
        public void It_resolved_before_the_store_failed() => _priorPassword.Should().Be(PriorSecret);

        [Test]
        public void It_asks_the_resolver_for_the_requests_tenant() =>
            Resolver
                .Calls.Should()
                .NotBeEmpty()
                .And.AllBeEquivalentTo(new SecretReference(Token, TenantName));

        [Test]
        public void It_fails_the_collection_read_with_the_generic_failure() =>
            AssertGenericFailure(_collection.Status, _collection.Content);

        [Test]
        public void It_fails_the_single_row_read_with_the_generic_failure() =>
            AssertGenericFailure(_single.Status, _single.Content);

        [TestCase("collection")]
        [TestCase("single")]
        public void It_logs_the_failure_under_the_responses_correlation_id(string read)
        {
            (HttpStatusCode status, string content) = read == "collection" ? _collection : _single;
            string correlationId = AssertGenericFailure(status, content);

            ReaderLogs(correlationId)
                .Should()
                .ContainSingle()
                .Which.Should()
                .Match<ScopedLog>(entry =>
                    entry.Level == LogLevel.Error
                    && Equals(entry.Field("Row"), $"data store {_id}")
                    && Equals(entry.Field("Tenant"), TenantName)
                    && Equals(entry.Field("Token"), Token)
                    && entry.Field("Outcome")!.ToString()!.Contains("System.InvalidOperationException")
                );
        }

        [Test]
        public void It_discloses_neither_the_prior_value_nor_the_resolvers_text_in_any_log() =>
            AssertNothingDisclosed();
    }

    [TestFixture]
    public class Given_a_nested_derivative_whose_reference_cannot_be_resolved
        : ConnectionStringResolutionApiTests
    {
        private const string FailingToken = "replica/broken";

        private int _dataStoreId;
        private int _failingId;
        private int _siblingId;

        [SetUp]
        public async Task Arrange()
        {
            await StartHost(multiTenancy: true);
            _dataStoreId = await CreateDataStore(ConnectionStringWithPassword("${secret:parent}"));
            _failingId = await CreateDerivative(
                _dataStoreId,
                "ReadReplica",
                ConnectionStringWithPassword($"${{secret:{FailingToken}}}")
            );
            _siblingId = await CreateDerivative(
                _dataStoreId,
                "Snapshot",
                ConnectionStringWithPassword("${secret:snapshot}")
            );

            Resolver.Behavior = reference =>
                reference.Name == FailingToken
                    ? throw new InvalidOperationException(SentinelFailure)
                    : $"value-of-{reference.Name}";
        }

        private void AssertContained(JsonNode dataStore)
        {
            Password(dataStore["connectionString"]).Should().Be("value-of-parent");
            JsonArray derivatives = dataStore["dataStoreDerivatives"]!.AsArray();
            derivatives
                .Single(d => d!["id"]!.GetValue<int>() == _failingId)!["connectionString"]
                .Should()
                .BeNull();
            Password(derivatives.Single(d => d!["id"]!.GetValue<int>() == _siblingId)!["connectionString"])
                .Should()
                .Be("value-of-snapshot");
        }

        [Test]
        public async Task It_succeeds_the_collection_read_with_the_derivative_unconfigured()
        {
            (HttpStatusCode status, string content) = await Get("/v3/dataStores/");

            status.Should().Be(HttpStatusCode.OK);
            AssertContained(JsonNode.Parse(content)!.AsArray().Single()!);
        }

        [Test]
        public async Task It_succeeds_the_single_row_read_with_the_derivative_unconfigured()
        {
            (HttpStatusCode status, string content) = await Get($"/v3/dataStores/{_dataStoreId}");

            status.Should().Be(HttpStatusCode.OK);
            AssertContained(JsonNode.Parse(content)!);
        }

        [Test]
        public async Task It_logs_the_parent_the_tenant_the_derivative_type_and_the_token()
        {
            await Get($"/v3/dataStores/{_dataStoreId}");

            Logs.Entries.Should()
                .Contain(entry =>
                    entry.Category == typeof(ConnectionStringReader).FullName
                    && entry.Level == LogLevel.Warning
                    && Equals(entry.Field("DataStoreId"), (long)_dataStoreId)
                    && Equals(entry.Field("Tenant"), TenantName)
                    && Equals(entry.Field("DerivativeType"), "ReadReplica")
                    && Equals(entry.Field("Token"), FailingToken)
                );
            AssertNothingDisclosed();
        }

        [Test]
        public async Task It_fails_the_standalone_collection_read()
        {
            (HttpStatusCode status, string content) = await Get("/v3/dataStoreDerivatives/");
            AssertGenericFailure(status, content);
        }

        [Test]
        public async Task It_fails_the_standalone_single_row_read()
        {
            (HttpStatusCode status, string content) = await Get($"/v3/dataStoreDerivatives/{_failingId}");
            AssertGenericFailure(status, content);
        }
    }

    [TestFixture]
    public class Given_a_secret_store_that_is_unreachable : ConnectionStringResolutionApiTests
    {
        [SetUp]
        public async Task Arrange() =>
            await StartHost(
                multiTenancy: true,
                resolverBehavior: _ => throw new HttpRequestException(SentinelFailure)
            );

        [Test]
        public void It_starts_without_asking_the_unreachable_store() => Resolver.Calls.Should().BeEmpty();

        [Test]
        public async Task It_starts_and_serves_a_collection_without_references()
        {
            await CreateDataStore(ConnectionStringWithPassword("plain"));

            (HttpStatusCode status, string content) = await Get("/v3/dataStores/");

            status.Should().Be(HttpStatusCode.OK);
            Password(JsonNode.Parse(content)!.AsArray().Single()!["connectionString"]).Should().Be("plain");
            Resolver.Calls.Should().BeEmpty();
        }

        [Test]
        public async Task It_fails_a_collection_where_one_data_store_carries_a_reference()
        {
            await CreateDataStore(ConnectionStringWithPassword("plain"));
            await CreateDataStore(ConnectionStringWithPassword($"${{secret:{Token}}}"));

            (HttpStatusCode status, string content) = await Get("/v3/dataStores/");

            AssertGenericFailure(status, content);
        }

        [Test]
        public async Task It_serves_a_collection_where_only_a_derivative_carries_a_reference()
        {
            await CreateDataStore(ConnectionStringWithPassword("plain"));
            int carrier = await CreateDataStore(ConnectionStringWithPassword("also-plain"));
            await CreateDerivative(
                carrier,
                "ReadReplica",
                ConnectionStringWithPassword($"${{secret:{Token}}}")
            );

            (HttpStatusCode status, string content) = await Get("/v3/dataStores/");

            status.Should().Be(HttpStatusCode.OK);
            JsonArray dataStores = JsonNode.Parse(content)!.AsArray();
            dataStores
                .Select(d => Password(d!["connectionString"]))
                .Should()
                .BeEquivalentTo("plain", "also-plain");
            dataStores.Single(d => d!["id"]!.GetValue<int>() == carrier)!["dataStoreDerivatives"]!
                .AsArray()
                .Single()!["connectionString"]
                .Should()
                .BeNull();
        }
    }
}
