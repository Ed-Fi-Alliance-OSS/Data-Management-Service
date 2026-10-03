// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.EducationOrganizationProjection;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Identity;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// Redaction across the reused infrastructure the projection steps call, not only the steps
/// themselves: the real <see cref="ConfigurationServiceDataStoreProvider"/> (with
/// <see cref="IdentityTenantSnapshot"/> for tenant loading) and the real
/// <see cref="MappingSetProvider"/> with its <see cref="MappingSetCache"/>, with fakes only beneath
/// them for HTTP and compilation. Every logger category is captured at <c>Trace</c>.
/// </summary>
/// <remarks>
/// A record the provider has already written cannot be withdrawn by a later catch, so these
/// assert on what every participant logged, not on what the projection step answered alone.
/// </remarks>
public abstract class EducationOrganizationProjectionUpstreamRedactionTests
{
    /// <summary>Markers planted in everything hostile; none may reach a log.</summary>
    protected const string HostileMarker = "hostile";
    protected const string SecretMarker = "s3cret";
    protected const string Hostile = "Server=hostile-db;User Id=sa;Password=s3cret";

    protected const string Tenant = "Tenant_255901";
    protected const int DataStoreId = 3788;

    private protected RecordingLoggerFactory Logs { get; private set; } = null!;

    [SetUp]
    public void ResetLogs() => Logs = new RecordingLoggerFactory();

    [TearDown]
    public void DisposeLogs() => Logs.Dispose();

    /// <summary>
    /// No record from any category carries an exception object, and no message or property
    /// contains any of the given values.
    /// </summary>
    protected void AssertNoRecordCarries(params string[] values)
    {
        Logs.Records.Should().NotBeEmpty();
        Logs.Records.Should().OnlyContain(record => record.Exception == null);

        foreach (string value in values)
        {
            Logs.Records.Should().NotContain(record => record.Mentions(value));
        }
    }

    private protected static RequestInfo ProjectionRequest(
        IDataStoreSelection selection,
        DatabaseFingerprint? fingerprint = null
    )
    {
        FrontendRequest frontendRequest = new(
            Path: "/management/education-organizations",
            Body: null,
            Form: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("projection-redaction"),
            RouteQualifiers: [],
            Tenant: Tenant
        );

        IServiceProvider serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDataStoreSelection))).Returns(selection);

        return new RequestInfo(frontendRequest, RequestMethod.GET, serviceProvider)
        {
            EducationOrganizationProjectionRequest = new EducationOrganizationProjectionRequest(
                DataStoreId,
                Limit: 2000,
                ContractVersion: ProjectionContractVersions.Default,
                BindingHash: "binding",
                Cursor: null
            ),
            DatabaseFingerprint = fingerprint,
        };
    }

    private protected static void AssertProblem(RequestInfo requestInfo, int status, string type)
    {
        requestInfo.FrontendResponse.StatusCode.Should().Be(status);
        requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        requestInfo.FrontendResponse.Body!["type"]!.GetValue<string>().Should().Be(type);
    }

    /// <summary>
    /// The real catalog provider over a stubbed Configuration Service. The token handler is faked
    /// beneath it because the provider owns the catch that logs a token failure.
    /// </summary>
    public abstract class CatalogTests : EducationOrganizationProjectionUpstreamRedactionTests
    {
        protected const string EncryptionKey = "RedactionTestEncryptionKey0123456789";

        protected static IConfigurationServiceTokenHandler TokenHandler(Exception? failure = null)
        {
            IConfigurationServiceTokenHandler tokenHandler = A.Fake<IConfigurationServiceTokenHandler>();
            var call = A.CallTo(() =>
                tokenHandler.GetTokenAsync(A<string>._, A<string>._, A<string>._, A<CancellationToken>._)
            );

            if (failure is null)
            {
                call.Returns("token");
            }
            else
            {
                call.ThrowsAsync(failure);
            }

            return tokenHandler;
        }

        private protected ConfigurationServiceDataStoreProvider CatalogProvider(
            Func<HttpRequestMessage, HttpResponseMessage> respond,
            IConfigurationServiceTokenHandler? tokenHandler = null
        ) =>
            new(
                new ConfigurationServiceApiClient(
                    new HttpClient(new StubHandler(respond))
                    {
                        BaseAddress = new Uri("https://cms.example.test/"),
                    }
                ),
                tokenHandler ?? TokenHandler(),
                new ConfigurationServiceContext("dms-client", "dms-secret", "edfi_admin_api/full_access"),
                Logs.CreateLogger<ConfigurationServiceDataStoreProvider>(),
                new ConnectionStringDecryptionService(EncryptionKey)
            );

        /// <summary>
        /// Runs the projection target step against a catalog that does not yet hold the store, so it
        /// reloads once through the real provider.
        /// </summary>
        private protected async Task<RequestInfo> ResolveTarget(IDataStoreProvider provider)
        {
            RequestInfo requestInfo = ProjectionRequest(new DataStoreSelection());
            IRuntimeMappingSetCompiler compiler = A.Fake<IRuntimeMappingSetCompiler>();
            A.CallTo(() => compiler.Dialect).Returns(SqlDialect.Pgsql);

            ResolveEducationOrganizationProjectionTargetMiddleware middleware = new(
                provider,
                [compiler],
                Logs.CreateLogger<ResolveEducationOrganizationProjectionTargetMiddleware>()
            );

            await middleware.Execute(requestInfo, () => Task.CompletedTask);
            return requestInfo;
        }

        protected static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

        protected void AssertProviderLoggedAnError() =>
            Logs
                .Records.Should()
                .Contain(record =>
                    record.Category == typeof(ConfigurationServiceDataStoreProvider).FullName
                    && record.Level == LogLevel.Error
                );
    }

    [TestFixture]
    public class Given_The_Configuration_Service_Connection_Fails : CatalogTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup() =>
            _requestInfo = await ResolveTarget(
                CatalogProvider(_ =>
                    throw new HttpRequestException(Hostile, new InvalidOperationException(Hostile))
                )
            );

        [Test]
        public void It_answers_service_unavailable() =>
            AssertProblem(_requestInfo, 503, "urn:ed-fi:api:service-unavailable");

        [Test]
        public void It_logs_the_failure_without_the_exception()
        {
            AssertProviderLoggedAnError();
            AssertNoRecordCarries(HostileMarker, SecretMarker);
        }
    }

    [TestFixture]
    public class Given_The_Configuration_Service_Answers_With_An_Error_Status : CatalogTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup() =>
            _requestInfo = await ResolveTarget(
                CatalogProvider(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent(Hostile),
                })
            );

        [Test]
        public void It_answers_service_unavailable() =>
            AssertProblem(_requestInfo, 503, "urn:ed-fi:api:service-unavailable");

        [Test]
        public void It_logs_the_status_without_the_exception_or_the_body()
        {
            AssertProviderLoggedAnError();
            AssertNoRecordCarries(HostileMarker, SecretMarker);
        }
    }

    [TestFixture]
    public class Given_The_Configuration_Service_Answers_With_Malformed_Json : CatalogTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup() =>
            _requestInfo = await ResolveTarget(CatalogProvider(_ => Json($"[{{\"id\": {Hostile}")));

        [Test]
        public void It_answers_service_unavailable() =>
            AssertProblem(_requestInfo, 503, "urn:ed-fi:api:service-unavailable");

        [Test]
        public void It_logs_the_failure_without_the_exception()
        {
            AssertProviderLoggedAnError();
            AssertNoRecordCarries(HostileMarker, SecretMarker);
        }
    }

    [TestFixture]
    public class Given_The_Configuration_Service_Token_Request_Fails : CatalogTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup() =>
            _requestInfo = await ResolveTarget(
                CatalogProvider(
                    _ => Json("[]"),
                    TokenHandler(new HttpRequestException(Hostile, new InvalidOperationException(Hostile)))
                )
            );

        [Test]
        public void It_answers_service_unavailable() =>
            AssertProblem(_requestInfo, 503, "urn:ed-fi:api:service-unavailable");

        [Test]
        public void It_logs_the_failure_without_the_exception()
        {
            AssertProviderLoggedAnError();
            AssertNoRecordCarries(HostileMarker, SecretMarker);
        }
    }

    /// <summary>
    /// The undecryptable primary connection string fails the whole tenant catalog load inside the
    /// real provider, which is why it is answered as an unavailable catalog.
    /// </summary>
    [TestFixture]
    public class Given_The_Catalog_Holds_An_Undecryptable_Primary_Connection_String : CatalogTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup() =>
            _requestInfo = await ResolveTarget(
                CatalogProvider(_ =>
                    Json(
                        $$"""
                        [{"id": {{DataStoreId}}, "dataStoreType": "Relational", "name": "District",
                          "connectionString": "{{Convert.ToBase64String(
                            Encoding.UTF8.GetBytes(Hostile + " is not ciphertext of this key")
                        )}}",
                          "dataStoreContexts": [], "dataStoreDerivatives": []}]
                        """
                    )
                )
            );

        [Test]
        public void It_answers_service_unavailable_with_its_fixed_body()
        {
            AssertProblem(_requestInfo, 503, "urn:ed-fi:api:service-unavailable");
            JsonNode body = _requestInfo.FrontendResponse.Body!;
            body["title"]!.GetValue<string>().Should().Be("Service Unavailable");
            body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("The service is temporarily unable to handle the request. Retry the request later.");
            body["errors"]!.AsArray().Should().BeEmpty();
        }

        [Test]
        public void It_logs_nothing_the_exception_carries() =>
            AssertNoRecordCarries(HostileMarker, SecretMarker);
    }

    /// <summary>
    /// Tenant existence, which precedes target resolution in the projection pipeline, loads the
    /// tenant list through the same provider.
    /// </summary>
    [TestFixture(TenantListFailure.ConnectionFails)]
    [TestFixture(TenantListFailure.MalformedJson)]
    public class Given_The_Tenant_List_Cannot_Be_Loaded(TenantListFailure failure) : CatalogTests
    {
        private TenantExistenceOutcome _outcome;

        [SetUp]
        public async Task Setup()
        {
            IdentityTenantSnapshot snapshot = new(
                CatalogProvider(_ =>
                    failure == TenantListFailure.ConnectionFails
                        ? throw new HttpRequestException(Hostile, new InvalidOperationException(Hostile))
                        : Json($"[\"{Tenant}\", {Hostile}")
                ),
                TimeProvider.System,
                A.Fake<IHostApplicationLifetime>(),
                Logs.CreateLogger<IdentityTenantSnapshot>()
            );

            _outcome = await snapshot.CheckAsync(Tenant, CancellationToken.None);
        }

        [Test]
        public void It_reports_the_tenant_list_unavailable() =>
            _outcome.Should().Be(TenantExistenceOutcome.Unavailable);

        [Test]
        public void It_logs_the_failure_in_both_components_without_the_exception()
        {
            AssertProviderLoggedAnError();
            Logs.Records.Should()
                .Contain(record =>
                    record.Category == typeof(IdentityTenantSnapshot).FullName
                    && record.Level == LogLevel.Error
                );
            AssertNoRecordCarries(HostileMarker, SecretMarker);
        }
    }

    public enum TenantListFailure
    {
        ConnectionFails,
        MalformedJson,
    }

    /// <summary>
    /// The real mapping provider and cache over a faked compiler. The target's fingerprint carries a
    /// distinctive effective schema hash that no record may contain.
    /// </summary>
    public abstract class MappingTests : EducationOrganizationProjectionUpstreamRedactionTests
    {
        protected static readonly string SchemaHash = "5c4e3a" + new string('7', 58);

        protected IRuntimeMappingSetCompiler Compiler { get; private set; } = null!;

        private protected MappingSetProvider Provider { get; private set; } = null!;

        [SetUp]
        public void CreateProvider()
        {
            Compiler = A.Fake<IRuntimeMappingSetCompiler>();
            A.CallTo(() => Compiler.Dialect).Returns(SqlDialect.Pgsql);
        }

        private protected void UseProvider(
            MappingSetProviderOptions options,
            IMappingPackStore? packStore = null
        ) =>
            Provider = new MappingSetProvider(
                packStore ?? A.Fake<IMappingPackStore>(),
                [Compiler],
                Options.Create(options),
                Logs.CreateLogger<MappingSetProvider>()
            );

        private protected async Task<RequestInfo> ResolveMappingSet()
        {
            RequestInfo requestInfo = ProjectionRequest(
                new DataStoreSelection(),
                new DatabaseFingerprint("1.0", SchemaHash, 42, new byte[32].ToImmutableArray())
            );

            IEffectiveSchemaSetProvider effectiveSchemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();
            A.CallTo(() => effectiveSchemaSetProvider.EffectiveSchemaSet)
                .Returns(
                    new EffectiveSchemaSet(
                        new EffectiveSchemaInfo("1.0", "v3", SchemaHash, 0, new byte[32], [], []),
                        []
                    )
                );

            ResolveEducationOrganizationProjectionMappingSetMiddleware middleware = new(
                Provider,
                effectiveSchemaSetProvider,
                [Compiler],
                Logs.CreateLogger<ResolveEducationOrganizationProjectionMappingSetMiddleware>()
            );

            await middleware.Execute(requestInfo, () => Task.CompletedTask);
            return requestInfo;
        }

        protected void AssertMappingProviderLogged(LogLevel level, string messageStart) =>
            Logs
                .Records.Should()
                .Contain(record =>
                    record.Category == typeof(MappingSetProvider).FullName
                    && record.Level == level
                    && record.Message.StartsWith(messageStart, StringComparison.Ordinal)
                );
    }

    [TestFixture]
    public class Given_The_Mapping_Set_Compiles_And_Is_Then_Served_From_The_Cache : MappingTests
    {
        private RequestInfo _first = null!;
        private RequestInfo _second = null!;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => Compiler.CompileAsync(A<MappingSetKey>._, A<CancellationToken>._))
                .Returns(
                    ResolveEducationOrganizationProjectionMappingSetMiddlewareTests.CreateMappingSet(
                        SqlDialect.Pgsql
                    )
                );
            UseProvider(new MappingSetProviderOptions { Enabled = false });

            _first = await ResolveMappingSet();
            _second = await ResolveMappingSet();
        }

        [Test]
        public void It_serves_both_requests() =>
            _second.MappingSet.Should().NotBeNull().And.BeSameAs(_first.MappingSet);

        [Test]
        public void It_compiles_once_and_hits_the_cache_once()
        {
            A.CallTo(() => Compiler.CompileAsync(A<MappingSetKey>._, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
            AssertMappingProviderLogged(LogLevel.Information, "Runtime mapping set compiled successfully");
            AssertMappingProviderLogged(LogLevel.Debug, "Mapping set cache hit");
        }

        [Test]
        public void It_never_logs_the_effective_schema_hash() => AssertNoRecordCarries(SchemaHash);
    }

    [TestFixture]
    public class Given_The_Mapping_Set_Compilation_Fails_With_Hostile_Text : MappingTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => Compiler.CompileAsync(A<MappingSetKey>._, A<CancellationToken>._))
                .ThrowsAsync(new InvalidOperationException(Hostile));
            UseProvider(new MappingSetProviderOptions { Enabled = false });

            _requestInfo = await ResolveMappingSet();
        }

        [Test]
        public void It_answers_projection_unsupported() =>
            AssertProblem(
                _requestInfo,
                409,
                "urn:ed-fi:api:education-organization-projection:projection-unsupported"
            );

        [Test]
        public void It_logs_neither_the_failure_text_nor_the_hash()
        {
            AssertMappingProviderLogged(LogLevel.Information, "Compiling runtime mapping set");
            AssertNoRecordCarries(HostileMarker, SecretMarker, SchemaHash);
        }
    }

    [TestFixture]
    public class Given_A_Required_Mapping_Pack_Is_Missing : MappingTests
    {
        private RequestInfo _requestInfo = null!;

        [SetUp]
        public async Task Setup()
        {
            IMappingPackStore packStore = A.Fake<IMappingPackStore>();
            A.CallTo(() => packStore.TryLoadPayloadAsync(A<MappingSetKey>._, A<CancellationToken>._))
                .Returns(Task.FromResult<MappingPackPayload?>(null));
            UseProvider(new MappingSetProviderOptions { Enabled = true, Required = true }, packStore);
            _requestInfo = await ResolveMappingSet();
        }

        [Test]
        public void It_answers_projection_unsupported() =>
            AssertProblem(
                _requestInfo,
                409,
                "urn:ed-fi:api:education-organization-projection:projection-unsupported"
            );

        [Test]
        public void It_logs_the_missing_pack_without_the_hash()
        {
            AssertMappingProviderLogged(LogLevel.Warning, "Mapping pack required but not found");
            AssertNoRecordCarries(SchemaHash);
        }
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }

    /// <summary>
    /// Records every category at every level, <c>Trace</c> included.
    /// </summary>
    private protected sealed class RecordingLoggerFactory : IDisposable
    {
        private readonly List<CategoryLogRecord> _records = [];
        private readonly ILoggerFactory _factory;

        public RecordingLoggerFactory() =>
            _factory = LoggerFactory.Create(builder =>
                builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new Provider(_records))
            );

        public IReadOnlyList<CategoryLogRecord> Records
        {
            get
            {
                lock (_records)
                {
                    return [.. _records];
                }
            }
        }

        public ILogger<T> CreateLogger<T>() => _factory.CreateLogger<T>();

        public void Dispose() => _factory.Dispose();

        private sealed class Provider(List<CategoryLogRecord> records) : ILoggerProvider
        {
            public ILogger CreateLogger(string categoryName) => new Logger(categoryName, records);

            public void Dispose() { }
        }

        private sealed class Logger(string category, List<CategoryLogRecord> records) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter
            )
            {
                Dictionary<string, string?> properties = state
                    is IEnumerable<KeyValuePair<string, object?>> values
                    ? values.ToDictionary(pair => pair.Key, pair => pair.Value?.ToString())
                    : [];

                lock (records)
                {
                    records.Add(
                        new CategoryLogRecord(
                            category,
                            logLevel,
                            formatter(state, exception),
                            exception,
                            properties
                        )
                    );
                }
            }
        }
    }

    private protected sealed record CategoryLogRecord(
        string Category,
        LogLevel Level,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, string?> Properties
    )
    {
        public bool Mentions(string value) =>
            Message.Contains(value, StringComparison.OrdinalIgnoreCase)
            || Properties.Values.Any(property =>
                property is not null && property.Contains(value, StringComparison.OrdinalIgnoreCase)
            );
    }
}
