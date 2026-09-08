// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ValidateDatabaseFingerprintMiddlewareTests
{
    internal static (
        ValidateDatabaseFingerprintMiddleware middleware,
        IDatabaseFingerprintReader fingerprintReader,
        IDataStoreSelection dataStoreSelection,
        IServiceProvider serviceProvider
    ) CreateMiddleware()
    {
        var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();
        var dataStoreSelection = A.Fake<IDataStoreSelection>();
        var logger = A.Fake<ILogger<ValidateDatabaseFingerprintMiddleware>>();
        var effectiveSchemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();

        // Default schema set with hash "abc123" to match test fingerprints
        A.CallTo(() => effectiveSchemaSetProvider.EffectiveSchemaSet)
            .Returns(
                new EffectiveSchemaSet(
                    new EffectiveSchemaInfo("1.0", "1.0", "abc123", 0, new byte[32], [], []),
                    []
                )
            );

        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDataStoreSelection))).Returns(dataStoreSelection);

        var fingerprintProvider = new DatabaseFingerprintProvider(
            fingerprintReader,
            TimeProvider.System,
            new CacheSettings()
        );
        var middleware = new ValidateDatabaseFingerprintMiddleware(
            fingerprintProvider,
            effectiveSchemaSetProvider,
            logger
        );

        return (middleware, fingerprintReader, dataStoreSelection, serviceProvider);
    }

    private static RequestInfo CreateRequestInfoWithAuthorizations(
        IServiceProvider? scopedServiceProvider = null
    )
    {
        var frontendRequest = new FrontendRequest(
            Path: "/ed-fi/students",
            Body: null,
            Form: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("test-trace-id"),
            RouteQualifiers: []
        );

        return new RequestInfo(
            frontendRequest,
            RequestMethod.GET,
            scopedServiceProvider ?? No.ServiceProvider
        )
        {
            ClientAuthorizations = new ClientAuthorizations(
                TokenId: "token123",
                ClientId: "client123",
                ClaimSetName: "test",
                EducationOrganizationIds: [],
                NamespacePrefixes: [],
                DataStoreIds: [new DataStoreId(1)]
            ),
        };
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Fingerprint_Is_Returned : ValidateDatabaseFingerprintMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, fingerprintReader, dataStoreSelection, serviceProvider) = CreateMiddleware();
            _requestInfo = CreateRequestInfoWithAuthorizations(serviceProvider);

            A.CallTo(() => dataStoreSelection.IsSet).Returns(true);
            A.CallTo(() => dataStoreSelection.GetSelectedDataStore())
                .Returns(
                    new DataStore(
                        Id: 1,
                        DataStoreType: "Test",
                        Name: "Test Instance",
                        ConnectionString: "Server=test;Database=testdb",
                        RouteContext: []
                    )
                );
            A.CallTo(() => dataStoreSelection.GetEffectiveTarget())
                .Returns(EffectiveDataStoreTarget.Primary("Server=test;Database=testdb"));

            A.CallTo(() =>
                    fingerprintReader.ReadFingerprintAsync(
                        EffectiveDataStoreTarget.Primary("Server=test;Database=testdb")
                    )
                )
                .Returns(new DatabaseFingerprint("1.0", "abc123", 42, new byte[32].ToImmutableArray()));

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_sets_database_fingerprint_on_request_info()
        {
            _requestInfo.DatabaseFingerprint.Should().NotBeNull();
            _requestInfo.DatabaseFingerprint!.EffectiveSchemaHash.Should().Be("abc123");
        }

        [Test]
        public void It_does_not_set_error_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Fingerprint_Is_Null : ValidateDatabaseFingerprintMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, fingerprintReader, dataStoreSelection, serviceProvider) = CreateMiddleware();
            _requestInfo = CreateRequestInfoWithAuthorizations(serviceProvider);

            A.CallTo(() => dataStoreSelection.IsSet).Returns(true);
            A.CallTo(() => dataStoreSelection.GetSelectedDataStore())
                .Returns(
                    new DataStore(
                        Id: 1,
                        DataStoreType: "Test",
                        Name: "Test Instance",
                        ConnectionString: "Server=test;Database=unprovisioned",
                        RouteContext: []
                    )
                );
            A.CallTo(() => dataStoreSelection.GetEffectiveTarget())
                .Returns(EffectiveDataStoreTarget.Primary("Server=test;Database=unprovisioned"));

            A.CallTo(() =>
                    fingerprintReader.ReadFingerprintAsync(
                        EffectiveDataStoreTarget.Primary("Server=test;Database=unprovisioned")
                    )
                )
                .Returns((DatabaseFingerprint?)null);

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_does_not_call_next()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_returns_503_service_unavailable()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public void It_does_not_set_database_fingerprint()
        {
            _requestInfo.DatabaseFingerprint.Should().BeNull();
        }

        [Test]
        public void It_returns_error_body_with_provisioning_guidance()
        {
            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("ddl provision");
        }

        [Test]
        public void It_returns_error_body_with_restart_guidance()
        {
            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("restart the Ed-Fi API service");
        }
    }

    /// <summary>
    /// A request routed to a derivative must be validated against the database it will read, not the
    /// parent it resolved from. The parent carries a different connection string here, so a middleware
    /// that still read the parent would be visible rather than accidentally correct.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Request_Was_Routed_To_A_Derivative : ValidateDatabaseFingerprintMiddlewareTests
    {
        private const string ParentConnectionString = "Server=parent;Database=edfi";
        private const string ReplicaConnectionString = "Server=replica;Database=edfi";

        private IDatabaseFingerprintReader _fingerprintReader = null!;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, fingerprintReader, dataStoreSelection, serviceProvider) = CreateMiddleware();
            _fingerprintReader = fingerprintReader;
            var requestInfo = CreateRequestInfoWithAuthorizations(serviceProvider);

            EffectiveDataStoreTarget replicaTarget = new(
                EffectiveTargetKind.ReadReplica,
                ReplicaConnectionString
            );

            A.CallTo(() => dataStoreSelection.IsSet).Returns(true);
            A.CallTo(() => dataStoreSelection.GetSelectedDataStore())
                .Returns(
                    new DataStore(
                        Id: 1,
                        DataStoreType: "Test",
                        Name: "Test Instance",
                        ConnectionString: ParentConnectionString,
                        RouteContext: []
                    )
                );
            A.CallTo(() => dataStoreSelection.GetEffectiveTarget()).Returns(replicaTarget);

            A.CallTo(() => fingerprintReader.ReadFingerprintAsync(replicaTarget))
                .Returns(new DatabaseFingerprint("1.0", "abc123", 42, new byte[32].ToImmutableArray()));

            await middleware.Execute(requestInfo, () => Task.CompletedTask);
        }

        [Test]
        public void It_reads_the_fingerprint_of_the_effective_target()
        {
            A.CallTo(() =>
                    _fingerprintReader.ReadFingerprintAsync(
                        new EffectiveDataStoreTarget(EffectiveTargetKind.ReadReplica, ReplicaConnectionString)
                    )
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_never_reads_the_parent_database()
        {
            A.CallTo(() =>
                    _fingerprintReader.ReadFingerprintAsync(
                        A<EffectiveDataStoreTarget>.That.Matches(target =>
                            target.ConnectionString == ParentConnectionString
                        )
                    )
                )
                .MustNotHaveHappened();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Fingerprint_Reader_Fails : ValidateDatabaseFingerprintMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private Func<Task> _execute = null!;

        [SetUp]
        public void Setup()
        {
            var dataStoreSelection = A.Fake<IDataStoreSelection>();
            A.CallTo(() => dataStoreSelection.IsSet).Returns(true);
            A.CallTo(() => dataStoreSelection.GetSelectedDataStore())
                .Returns(
                    new DataStore(
                        Id: 1,
                        DataStoreType: "Test",
                        Name: "Test Instance",
                        ConnectionString: "Server=test;Database=testdb",
                        RouteContext: []
                    )
                );
            A.CallTo(() => dataStoreSelection.GetEffectiveTarget())
                .Returns(EffectiveDataStoreTarget.Primary("Server=test;Database=testdb"));

            var serviceProvider = A.Fake<IServiceProvider>();
            A.CallTo(() => serviceProvider.GetService(typeof(IDataStoreSelection)))
                .Returns(dataStoreSelection);

            var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet)
                .Returns(
                    new EffectiveSchemaSet(
                        new EffectiveSchemaInfo("1.0", "1.0", "abc123", 0, new byte[32], [], []),
                        []
                    )
                );

            var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();
            A.CallTo(() =>
                    fingerprintReader.ReadFingerprintAsync(
                        EffectiveDataStoreTarget.Primary("Server=test;Database=testdb")
                    )
                )
                .Throws(
                    new InvalidOperationException("No dialect-specific fingerprint reader is registered.")
                );

            var middleware = new ValidateDatabaseFingerprintMiddleware(
                new DatabaseFingerprintProvider(fingerprintReader, TimeProvider.System, new CacheSettings()),
                schemaSetProvider,
                A.Fake<ILogger<ValidateDatabaseFingerprintMiddleware>>()
            );

            _requestInfo = CreateRequestInfoWithAuthorizations(serviceProvider);
            _execute = () => middleware.Execute(_requestInfo, () => Task.CompletedTask);
        }

        [Test]
        public async Task It_returns_503_service_unavailable()
        {
            await _execute();

            _requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public async Task It_does_not_set_database_fingerprint()
        {
            await _execute();

            _requestInfo.DatabaseFingerprint.Should().BeNull();
        }
    }

    /// <summary>
    /// Seam 1: the fingerprint read could not acquire a connection. The response depends on which kind
    /// of target it was reading, which is the whole reason the exception carries the kind.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Fingerprint_Read_Could_Not_Acquire_A_Connection
        : ValidateDatabaseFingerprintMiddlewareTests
    {
        private const string ConnectionString = "Server=snapshot;Database=edfi;Password=hunter2";

        private sealed class CapturingLogger : ILogger<ValidateDatabaseFingerprintMiddleware>
        {
            public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

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
                Entries.Add((logLevel, formatter(state, exception), exception));
            }
        }

        /// <summary>
        /// A provider exception of the shape the seam guard classifies. Its message quotes the
        /// connection string back, which is exactly what must never reach a log.
        /// </summary>
        private static TimeoutException ProviderFailure() =>
            new($"connection timed out for {ConnectionString}");

        /// <summary>
        /// Counts its own reads, so the fixture can tell a cached failed verdict from an evicted one
        /// without depending on when a fake's exception factory is invoked.
        /// </summary>
        private sealed class ThrowingFingerprintReader(Exception thrown) : IDatabaseFingerprintReader
        {
            public int Reads { get; private set; }

            public Task<DatabaseFingerprint?> ReadFingerprintAsync(EffectiveDataStoreTarget target)
            {
                Reads++;
                throw thrown;
            }
        }

        private sealed record Outcome(
            RequestInfo RequestInfo,
            bool NextCalled,
            int Reads,
            CapturingLogger Logger
        );

        /// <summary>
        /// Executes twice against the same middleware and provider, so the second run reveals whether
        /// the failed verdict was cached. A read count of two means it was evicted.
        /// </summary>
        private static async Task<Outcome> ExecuteWith(EffectiveTargetKind kind, Exception thrown)
        {
            EffectiveDataStoreTarget target = new(kind, ConnectionString);

            var dataStoreSelection = A.Fake<IDataStoreSelection>();
            A.CallTo(() => dataStoreSelection.IsSet).Returns(true);
            A.CallTo(() => dataStoreSelection.GetSelectedDataStore())
                .Returns(
                    new DataStore(
                        Id: 1,
                        DataStoreType: "Test",
                        Name: "Test Instance",
                        ConnectionString: ConnectionString,
                        RouteContext: []
                    )
                );
            A.CallTo(() => dataStoreSelection.GetEffectiveTarget()).Returns(target);

            var serviceProvider = A.Fake<IServiceProvider>();
            A.CallTo(() => serviceProvider.GetService(typeof(IDataStoreSelection)))
                .Returns(dataStoreSelection);

            var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet)
                .Returns(
                    new EffectiveSchemaSet(
                        new EffectiveSchemaInfo("1.0", "1.0", "abc123", 0, new byte[32], [], []),
                        []
                    )
                );

            ThrowingFingerprintReader fingerprintReader = new(thrown);

            CapturingLogger logger = new();
            var middleware = new ValidateDatabaseFingerprintMiddleware(
                new DatabaseFingerprintProvider(fingerprintReader, TimeProvider.System, new CacheSettings()),
                schemaSetProvider,
                logger
            );

            RequestInfo requestInfo = CreateRequestInfoWithAuthorizations(serviceProvider);
            bool nextCalled = false;

            await middleware.Execute(
                requestInfo,
                () =>
                {
                    nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            await middleware.Execute(
                CreateRequestInfoWithAuthorizations(serviceProvider),
                () => Task.CompletedTask
            );

            return new Outcome(requestInfo, nextCalled, fingerprintReader.Reads, logger);
        }

        private static Task<Outcome> ExecuteWith(EffectiveTargetKind kind) =>
            ExecuteWith(kind, new DatabaseConnectionUnavailableException(kind, ProviderFailure()));

        [Test]
        public async Task It_answers_snapshot_not_found_for_a_snapshot()
        {
            Outcome outcome = await ExecuteWith(EffectiveTargetKind.Snapshot);

            outcome.RequestInfo.FrontendResponse.ShouldBeSnapshotNotFound("test-trace-id");
            outcome.NextCalled.Should().BeFalse();
            outcome.RequestInfo.DatabaseFingerprint.Should().BeNull();
        }

        /// <summary>
        /// A failed verdict must not be cached, or a snapshot rebuilt underneath a running service
        /// would keep answering 404 until restart. The cache evicts a derivative fault on its own, so
        /// the second read is the proof rather than an explicit invalidation.
        /// </summary>
        [Test]
        public async Task It_does_not_cache_the_failed_snapshot_verdict()
        {
            Outcome outcome = await ExecuteWith(EffectiveTargetKind.Snapshot);

            outcome.Reads.Should().Be(2, "the immediately following request must revalidate");
        }

        /// <summary>
        /// Only a snapshot's response depends on the failure being a connection failure. A primary or
        /// a read replica keeps the transient-error 503 it produces today, which is what stops this
        /// translation from changing two contracts it was not meant to touch.
        /// </summary>
        [TestCase(EffectiveTargetKind.Primary)]
        [TestCase(EffectiveTargetKind.ReadReplica)]
        public async Task It_keeps_the_existing_503_for_a_non_snapshot(EffectiveTargetKind kind)
        {
            Outcome outcome = await ExecuteWith(kind);

            outcome.RequestInfo.FrontendResponse.StatusCode.Should().Be(503);
            outcome
                .RequestInfo.FrontendResponse.Body!.ToString()
                .Should()
                .Contain("urn:ed-fi:api:service-configuration-error");
            outcome.NextCalled.Should().BeFalse();
        }

        /// <summary>
        /// A malformed fingerprint on a snapshot is a statement about the database's contents, not
        /// about reaching it, so the new arm must not swallow it: it keeps the 503 provisioning body.
        /// </summary>
        [Test]
        public async Task It_keeps_the_existing_503_for_a_malformed_snapshot_fingerprint()
        {
            Outcome outcome = await ExecuteWith(
                EffectiveTargetKind.Snapshot,
                new DatabaseFingerprintValidationException(["malformed"])
            );

            outcome.RequestInfo.FrontendResponse.StatusCode.Should().Be(503);
            outcome
                .RequestInfo.FrontendResponse.Body!.ToString()
                .Should()
                .Contain("urn:ed-fi:api:database-fingerprint-validation-error");
        }

        [Test]
        public async Task It_logs_the_inner_exception_type_and_the_target_kind()
        {
            Outcome outcome = await ExecuteWith(EffectiveTargetKind.Snapshot);

            outcome
                .Logger.Entries.Should()
                .Contain(entry =>
                    entry.Level == LogLevel.Warning
                    && entry.Message.Contains("Snapshot connection unavailable")
                    && entry.Message.Contains(nameof(TimeoutException))
                    && entry.Message.Contains("for Snapshot target")
                );
        }

        [Test]
        public async Task It_never_logs_connection_material()
        {
            Outcome outcome = await ExecuteWith(EffectiveTargetKind.Snapshot);

            outcome.Logger.Entries.Should().NotContain(entry => entry.Message.Contains("Password=hunter2"));
            outcome
                .Logger.Entries.Should()
                .OnlyContain(
                    entry => entry.Exception == null,
                    "the wrapper's inner provider exception quotes the connection string in its message"
                );
        }
    }
}
