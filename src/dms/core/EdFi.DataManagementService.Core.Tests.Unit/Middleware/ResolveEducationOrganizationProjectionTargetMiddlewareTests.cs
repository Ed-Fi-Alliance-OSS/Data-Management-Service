// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.EducationOrganizationProjection;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// Shared arrangement: a request for data store 3788 in tenant <c>Tenant_255901</c> with route
/// qualifiers <c>255901/2025</c>, against a faked catalog and a registered compiler of the chosen
/// dialect.
/// </summary>
public abstract class ResolveEducationOrganizationProjectionTargetMiddlewareTests
{
    protected const string Tenant = "Tenant_255901";
    protected const int DataStoreId = 3788;
    protected const string ConnectionString = "host=db;database=edfi_255901_2025";

    /// <summary>A value that must never reach a response or a log.</summary>
    protected const string Hostile = "Server=hostile;Password=s3cret-hostile";

    protected IDataStoreProvider DataStoreProvider { get; private set; } = null!;

    private protected RecordingLogger<ResolveEducationOrganizationProjectionTargetMiddleware> Logger
    {
        get;
        private set;
    } = new();

    private protected RequestInfo RequestInfo { get; private set; } = null!;

    protected DataStoreSelection Selection { get; private set; } = null!;

    protected bool NextCalled { get; private set; }

    /// <summary>
    /// NUnit reuses one fixture instance for every test in it, so fakes and the log are rebuilt here.
    /// </summary>
    [SetUp]
    public void ResetFakes()
    {
        DataStoreProvider = A.Fake<IDataStoreProvider>();
        Logger = new RecordingLogger<ResolveEducationOrganizationProjectionTargetMiddleware>();
        Selection = new DataStoreSelection();
    }

    protected static Dictionary<RouteQualifierName, RouteQualifierValue> Qualifiers(
        string districtId,
        string schoolYear
    ) =>
        new()
        {
            [new RouteQualifierName("districtId")] = new RouteQualifierValue(districtId),
            [new RouteQualifierName("schoolYear")] = new RouteQualifierValue(schoolYear),
        };

    protected static DataStore Store(
        RelationalProviderMetadataStatus status = RelationalProviderMetadataStatus.Missing,
        RelationalProviderToken? token = null,
        string? connectionString = ConnectionString,
        Dictionary<RouteQualifierName, RouteQualifierValue>? routeContext = null
    ) =>
        new(
            Id: DataStoreId,
            DataStoreType: "Relational",
            Name: "District 255901 2025",
            ConnectionString: connectionString,
            RouteContext: routeContext ?? Qualifiers("255901", "2025"),
            RelationalProviderToken: token,
            RelationalProviderMetadataStatus: status
        );

    protected void StoreInCache(DataStore store) =>
        A.CallTo(() => DataStoreProvider.GetById(DataStoreId, Tenant)).Returns(store);

    protected async Task Execute(
        SqlDialect? registeredDialect = SqlDialect.Pgsql,
        CancellationToken cancellationToken = default
    )
    {
        FrontendRequest frontendRequest = new(
            Path: "/management/education-organizations",
            Body: null,
            Form: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("projection-target"),
            RouteQualifiers: Qualifiers("255901", "2025"),
            Tenant: Tenant
        );

        IServiceProvider serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDataStoreSelection))).Returns(Selection);

        RequestInfo = new RequestInfo(frontendRequest, RequestMethod.GET, serviceProvider)
        {
            EducationOrganizationProjectionRequest = new EducationOrganizationProjectionRequest(
                DataStoreId,
                Limit: 2000,
                ContractVersion: ProjectionContractVersions.Default,
                BindingHash: "binding",
                Cursor: null
            ),
            RequestCancellationToken = cancellationToken,
        };
        NextCalled = false;

        List<IRuntimeMappingSetCompiler> compilers = [];

        if (registeredDialect is SqlDialect dialect)
        {
            IRuntimeMappingSetCompiler compiler = A.Fake<IRuntimeMappingSetCompiler>();
            A.CallTo(() => compiler.Dialect).Returns(dialect);
            compilers.Add(compiler);
        }

        ResolveEducationOrganizationProjectionTargetMiddleware middleware = new(
            DataStoreProvider,
            compilers,
            Logger
        );

        await middleware.Execute(
            RequestInfo,
            () =>
            {
                NextCalled = true;
                return Task.CompletedTask;
            }
        );
    }

    protected JsonNode Body => RequestInfo.FrontendResponse.Body!;

    protected void AssertAccepted(DataStore store)
    {
        NextCalled.Should().BeTrue();
        RequestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        Selection.IsSet.Should().BeTrue();
        Selection.GetSelectedDataStore().Should().BeSameAs(store);
    }

    /// <summary>
    /// The full fixed body: status, type, title and detail exactly, empty <c>errors</c>, served as
    /// <c>application/problem+json</c>, and the request stopped before any data store was selected.
    /// </summary>
    protected void AssertRejectedWith(int status, string type, string title, string detail)
    {
        NextCalled.Should().BeFalse();
        Selection.IsSet.Should().BeFalse();
        RequestInfo.FrontendResponse.StatusCode.Should().Be(status);
        RequestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        Body["status"]!.GetValue<int>().Should().Be(status);
        Body["type"]!.GetValue<string>().Should().Be(type);
        Body["title"]!.GetValue<string>().Should().Be(title);
        Body["detail"]!.GetValue<string>().Should().Be(detail);
        Body["errors"]!.AsArray().Should().BeEmpty();
    }

    protected void AssertTargetNotFound() =>
        AssertRejectedWith(
            404,
            "urn:ed-fi:api:education-organization-projection:target-not-found",
            "Target Not Found",
            "The data store could not be found."
        );

    protected void AssertProviderUnsupported() =>
        AssertRejectedWith(
            409,
            "urn:ed-fi:api:education-organization-projection:target-provider-unsupported",
            "Target Provider Unsupported",
            "The data store uses a database provider that this Ed-Fi API deployment does not serve."
        );

    protected void AssertNothingHostileLogged()
    {
        Logger.Records.Should().NotBeEmpty();
        Logger.Records.Should().OnlyContain(record => record.Exception == null);
        Logger
            .Records.Should()
            .NotContain(record =>
                record.Message.Contains("hostile", StringComparison.OrdinalIgnoreCase)
                || record.Properties.Values.Any(value =>
                    value != null && value.ToString()!.Contains("hostile", StringComparison.OrdinalIgnoreCase)
                )
            );
    }

    [TestFixture]
    public class Given_A_Supported_PostgreSql_Store_On_A_PostgreSql_Deployment
        : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        private DataStore _store = null!;

        [SetUp]
        public async Task Setup()
        {
            _store = Store(RelationalProviderMetadataStatus.Supported, RelationalProviderToken.Postgresql);
            StoreInCache(_store);
            await Execute(SqlDialect.Pgsql);
        }

        [Test]
        public void It_selects_the_store_and_continues() => AssertAccepted(_store);

        [Test]
        public void It_does_not_reload_the_catalog() =>
            A.CallTo(() => DataStoreProvider.LoadDataStores(A<string?>._, A<CancellationToken>._))
                .MustNotHaveHappened();

        [Test]
        public void It_refreshes_the_tenant_catalog_first() =>
            A.CallTo(() => DataStoreProvider.RefreshInstancesIfExpiredAsync(Tenant, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
    }

    [TestFixture]
    public class Given_A_Supported_SqlServer_Store_On_A_SqlServer_Deployment
        : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        private DataStore _store = null!;

        [SetUp]
        public async Task Setup()
        {
            _store = Store(RelationalProviderMetadataStatus.Supported, RelationalProviderToken.SqlServer);
            StoreInCache(_store);
            await Execute(SqlDialect.Mssql);
        }

        [Test]
        public void It_selects_the_store_and_continues() => AssertAccepted(_store);
    }

    [TestFixture]
    public class Given_A_PostgreSql_Store_On_A_SqlServer_Deployment
        : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            StoreInCache(
                Store(RelationalProviderMetadataStatus.Supported, RelationalProviderToken.Postgresql)
            );
            await Execute(SqlDialect.Mssql);
        }

        [Test]
        public void It_answers_target_provider_unsupported() => AssertProviderUnsupported();
    }

    [TestFixture]
    public class Given_A_SqlServer_Store_On_A_PostgreSql_Deployment
        : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            StoreInCache(
                Store(RelationalProviderMetadataStatus.Supported, RelationalProviderToken.SqlServer)
            );
            await Execute(SqlDialect.Pgsql);
        }

        [Test]
        public void It_answers_target_provider_unsupported() => AssertProviderUnsupported();
    }

    [TestFixture(SqlDialect.Pgsql)]
    [TestFixture(SqlDialect.Mssql)]
    public class Given_A_Legacy_Store_With_No_Provider_Token(SqlDialect registeredDialect)
        : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        private DataStore _store = null!;

        [SetUp]
        public async Task Setup()
        {
            _store = Store(RelationalProviderMetadataStatus.Missing, token: null);
            StoreInCache(_store);
            await Execute(registeredDialect);
        }

        [Test]
        public void It_is_served_by_the_registered_dialect() => AssertAccepted(_store);
    }

    [TestFixture]
    public class Given_A_Store_With_An_Unrecognized_Provider
        : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            StoreInCache(Store(RelationalProviderMetadataStatus.Unknown, token: null));
            await Execute(SqlDialect.Pgsql);
        }

        [Test]
        public void It_answers_target_provider_unsupported() => AssertProviderUnsupported();
    }

    [TestFixture]
    public class Given_A_Cache_Miss_That_The_Reload_Resolves
        : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        private DataStore _store = null!;

        [SetUp]
        public async Task Setup()
        {
            _store = Store();
            A.CallTo(() => DataStoreProvider.GetById(DataStoreId, Tenant))
                .ReturnsNextFromSequence(null, _store);
            await Execute();
        }

        [Test]
        public void It_selects_the_reloaded_store() => AssertAccepted(_store);

        [Test]
        public void It_reloads_the_tenant_catalog_exactly_once() =>
            A.CallTo(() => DataStoreProvider.LoadDataStores(A<string?>._, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly()
                .Then(A.CallTo(() => DataStoreProvider.GetById(DataStoreId, Tenant)).MustHaveHappened());

        [Test]
        public void It_reloads_the_requests_tenant() =>
            A.CallTo(() => DataStoreProvider.LoadDataStores(Tenant, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
    }

    [TestFixture]
    public class Given_An_Unknown_Store : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => DataStoreProvider.GetById(A<long>._, A<string?>._)).Returns(null);
            await Execute();
        }

        [Test]
        public void It_answers_target_not_found() => AssertTargetNotFound();

        [Test]
        public void It_reloads_the_catalog_exactly_once() =>
            A.CallTo(() => DataStoreProvider.LoadDataStores(A<string?>._, A<CancellationToken>._))
                .MustHaveHappenedOnceExactly();
    }

    [TestFixture]
    public class Given_A_Store_Of_Another_Tenant : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            // The id exists, but only in another tenant's catalog.
            A.CallTo(() => DataStoreProvider.GetById(DataStoreId, "Tenant_255902")).Returns(Store());
            A.CallTo(() => DataStoreProvider.GetById(DataStoreId, Tenant)).Returns(null);
            await Execute();
        }

        [Test]
        public void It_answers_target_not_found() => AssertTargetNotFound();

        [Test]
        public void It_never_looks_outside_the_requests_tenant() =>
            A.CallTo(() => DataStoreProvider.GetById(A<long>._, A<string?>.That.Not.IsEqualTo(Tenant)))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_A_Store_Whose_Route_Context_Does_Not_Match
        : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        private JsonNode _notFoundBody = null!;

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => DataStoreProvider.GetById(A<long>._, A<string?>._)).Returns(null);
            await Execute();
            _notFoundBody = Body.DeepClone();

            ResetFakes();
            StoreInCache(Store(routeContext: Qualifiers("255901", "2024")));
            await Execute();
        }

        [Test]
        public void It_answers_target_not_found() => AssertTargetNotFound();

        [Test]
        public void It_answers_with_the_same_body_as_an_unknown_store() =>
            JsonNode.DeepEquals(Body, _notFoundBody).Should().BeTrue();

        [Test]
        public void It_does_not_reload_the_catalog_for_a_store_it_found() =>
            A.CallTo(() => DataStoreProvider.LoadDataStores(A<string?>._, A<CancellationToken>._))
                .MustNotHaveHappened();
    }

    [TestFixture]
    public class Given_The_Configuration_Service_Is_Unreachable_During_The_Miss_Reload
        : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => DataStoreProvider.GetById(A<long>._, A<string?>._)).Returns(null);
            A.CallTo(() => DataStoreProvider.LoadDataStores(A<string?>._, A<CancellationToken>._))
                .ThrowsAsync(new InvalidOperationException(Hostile));
            await Execute();
        }

        [Test]
        public void It_answers_service_unavailable_not_target_not_found() =>
            AssertRejectedWith(
                503,
                "urn:ed-fi:api:service-unavailable",
                "Service Unavailable",
                "The service is temporarily unable to handle the request. Retry the request later."
            );

        [Test]
        public void It_logs_the_exception_type_only()
        {
            AssertNothingHostileLogged();
            Logger
                .Records.Should()
                .Contain(record =>
                    Equals(record.Properties["ExceptionType"], nameof(InvalidOperationException))
                );
        }
    }

    /// <summary>
    /// An undecryptable primary connection string fails the whole tenant catalog load inside the data
    /// store provider, with the same exception type as an outage, so it never reaches this step as a
    /// store: it is answered as a failed reload.
    /// </summary>
    [TestFixture]
    public class Given_The_Miss_Reload_Fails_On_An_Undecryptable_Connection_String
        : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => DataStoreProvider.GetById(A<long>._, A<string?>._)).Returns(null);
            A.CallTo(() => DataStoreProvider.LoadDataStores(A<string?>._, A<CancellationToken>._))
                .ThrowsAsync(
                    new InvalidOperationException(
                        "Failed to decrypt the connection string. hostile",
                        new System.Security.Cryptography.CryptographicException(Hostile)
                    )
                );
            await Execute();
        }

        [Test]
        public void It_answers_the_transient_service_unavailable() =>
            AssertRejectedWith(
                503,
                "urn:ed-fi:api:service-unavailable",
                "Service Unavailable",
                "The service is temporarily unable to handle the request. Retry the request later."
            );

        [Test]
        public void It_logs_nothing_the_exception_carries() => AssertNothingHostileLogged();
    }

    [TestFixture("")]
    [TestFixture("   ")]
    [TestFixture(null)]
    public class Given_A_Store_With_No_Connection_String(string? connectionString)
        : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            StoreInCache(Store(connectionString: connectionString));
            await Execute();
        }

        [Test]
        public void It_answers_service_configuration_error() =>
            AssertRejectedWith(
                503,
                "urn:ed-fi:api:service-configuration-error",
                "Service Configuration Error",
                "The data store's database connection is not configured."
            );
    }

    [TestFixture]
    public class Given_An_Unrecognized_Provider_And_No_Connection_String
        : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            StoreInCache(Store(RelationalProviderMetadataStatus.Unknown, connectionString: null));
            await Execute();
        }

        [Test]
        public void It_answers_the_permanent_provider_failure_before_the_transient_one() =>
            AssertProviderUnsupported();
    }

    [TestFixture]
    public class Given_A_Route_Context_Mismatch_And_An_Unrecognized_Provider
        : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            StoreInCache(
                Store(RelationalProviderMetadataStatus.Unknown, routeContext: Qualifiers("255901", "2024"))
            );
            await Execute();
        }

        [Test]
        public void It_does_not_disclose_the_store_of_another_route() => AssertTargetNotFound();
    }

    [TestFixture]
    public class Given_The_Catalog_Refresh_Fails : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        private DataStore _store = null!;

        [SetUp]
        public async Task Setup()
        {
            _store = Store();
            StoreInCache(_store);
            A.CallTo(() =>
                    DataStoreProvider.RefreshInstancesIfExpiredAsync(A<string?>._, A<CancellationToken>._)
                )
                .ThrowsAsync(new HttpRequestException(Hostile));
            await Execute();
        }

        [Test]
        public void It_serves_the_cached_store() => AssertAccepted(_store);

        [Test]
        public void It_logs_the_exception_type_only() => AssertNothingHostileLogged();
    }

    [TestFixture]
    public class Given_The_Request_Is_Cancelled_During_The_Miss_Reload
        : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        [Test]
        public async Task It_propagates_the_cancellation_without_a_response()
        {
            using CancellationTokenSource cancellation = new();
            A.CallTo(() => DataStoreProvider.GetById(A<long>._, A<string?>._)).Returns(null);
            A.CallTo(() => DataStoreProvider.LoadDataStores(A<string?>._, A<CancellationToken>._))
                .ReturnsLazily(
                    (string? _, CancellationToken token) =>
                    {
                        cancellation.Cancel();
                        token.ThrowIfCancellationRequested();
                        return Task.FromResult<IList<DataStore>>([]);
                    }
                );

            Func<Task> act = () => Execute(cancellationToken: cancellation.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            RequestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
            NextCalled.Should().BeFalse();
        }
    }

    [TestFixture]
    public class Given_No_Runtime_Compiler_Is_Registered
        : ResolveEducationOrganizationProjectionTargetMiddlewareTests
    {
        [Test]
        public async Task It_fails_as_a_composition_defect_not_a_client_state()
        {
            StoreInCache(Store());
            Func<Task> act = () => Execute(registeredDialect: null);
            await act.Should().ThrowAsync<InvalidOperationException>();
            NextCalled.Should().BeFalse();
        }
    }
}
