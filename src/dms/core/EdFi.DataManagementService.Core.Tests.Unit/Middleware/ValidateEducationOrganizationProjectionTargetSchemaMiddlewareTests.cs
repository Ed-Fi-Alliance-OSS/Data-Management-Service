// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using EdFi.DataManagementService.Core.Startup;
using EdFi.DataManagementService.Core.Tests.Unit.TestSupport;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

/// <summary>
/// Shared arrangement: data store 3788 selected with its Primary target, a faked fingerprint reader
/// behind a real <see cref="DatabaseFingerprintProvider"/> cache, and a deployment whose effective
/// schema hash is <see cref="ExpectedHash"/>.
/// </summary>
public abstract class ValidateEducationOrganizationProjectionTargetSchemaMiddlewareTests
{
    protected const string ConnectionString = "host=db;database=edfi_255901_2025";

    /// <summary>A value that must never reach a response or a log.</summary>
    protected const string Hostile = "Server=hostile;Password=s3cret-hostile";

    protected static readonly string ExpectedHash = new('a', 64);

    protected static readonly EffectiveDataStoreTarget Target = EffectiveDataStoreTarget.Primary(
        ConnectionString
    );

    protected IDatabaseFingerprintReader FingerprintReader { get; private set; } = null!;

    private protected DatabaseFingerprintProvider FingerprintProvider { get; private set; } = null!;

    private protected RecordingLogger<ValidateEducationOrganizationProjectionTargetSchemaMiddleware> Logger
    {
        get;
        private set;
    } = new();

    private protected RequestInfo RequestInfo { get; private set; } = null!;

    protected bool NextCalled { get; private set; }

    /// <summary>
    /// NUnit reuses one fixture instance for every test in it, so the cache, fakes and log are
    /// rebuilt here.
    /// </summary>
    [SetUp]
    public void ResetFakes()
    {
        FingerprintReader = A.Fake<IDatabaseFingerprintReader>();
        FingerprintProvider = new DatabaseFingerprintProvider(
            FingerprintReader,
            TimeProvider.System,
            new CacheSettings()
        );
        Logger = new RecordingLogger<ValidateEducationOrganizationProjectionTargetSchemaMiddleware>();
    }

    protected static DatabaseFingerprint Fingerprint(string hash) =>
        new("1.0", hash, 42, new byte[32].ToImmutableArray());

    protected async Task Execute(CancellationToken cancellationToken = default)
    {
        FrontendRequest frontendRequest = new(
            Path: "/management/education-organizations",
            Body: null,
            Form: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("projection-schema"),
            RouteQualifiers: [],
            Tenant: "Tenant_255901"
        );

        DataStoreSelection selection = new();
        selection.SetSelectedDataStore(
            new DataStore(
                Id: 3788,
                DataStoreType: "Relational",
                Name: "District 255901 2025",
                ConnectionString: ConnectionString,
                RouteContext: []
            )
        );
        selection.SetEffectiveTarget(Target);

        IServiceProvider serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDataStoreSelection))).Returns(selection);

        RequestInfo = new RequestInfo(frontendRequest, RequestMethod.GET, serviceProvider)
        {
            RequestCancellationToken = cancellationToken,
        };
        NextCalled = false;

        IEffectiveSchemaSetProvider effectiveSchemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();
        A.CallTo(() => effectiveSchemaSetProvider.EffectiveSchemaSet)
            .Returns(
                new EffectiveSchemaSet(
                    new EffectiveSchemaInfo("1.0", "v3", ExpectedHash, 0, new byte[32], [], []),
                    []
                )
            );

        ValidateEducationOrganizationProjectionTargetSchemaMiddleware middleware = new(
            FingerprintProvider,
            effectiveSchemaSetProvider,
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

    protected void AssertRejectedWith(int status, string type, string title, string detail)
    {
        NextCalled.Should().BeFalse();
        RequestInfo.DatabaseFingerprint.Should().BeNull();
        RequestInfo.FrontendResponse.StatusCode.Should().Be(status);
        RequestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        Body["status"]!.GetValue<int>().Should().Be(status);
        Body["type"]!.GetValue<string>().Should().Be(type);
        Body["title"]!.GetValue<string>().Should().Be(title);
        Body["detail"]!.GetValue<string>().Should().Be(detail);
        Body["errors"]!.AsArray().Should().BeEmpty();
    }

    protected void AssertSchemaIncompatible() =>
        AssertRejectedWith(
            409,
            "urn:ed-fi:api:education-organization-projection:target-schema-incompatible",
            "Target Schema Incompatible",
            "The data store's database schema is not compatible with this Ed-Fi API deployment."
        );

    protected void AssertTargetUnavailable() =>
        AssertRejectedWith(
            503,
            "urn:ed-fi:api:education-organization-projection:target-unavailable",
            "Target Unavailable",
            "The data store's database is temporarily unavailable. Retry the request later."
        );

    /// <summary>
    /// No record carries an exception, and no message or property contains any of the given values.
    /// </summary>
    protected void AssertNotLogged(params string[] values)
    {
        Logger.Records.Should().NotBeEmpty();
        Logger.Records.Should().OnlyContain(record => record.Exception == null);

        foreach (string value in values)
        {
            Logger
                .Records.Should()
                .NotContain(record =>
                    record.Message.Contains(value, StringComparison.OrdinalIgnoreCase)
                    || record.Properties.Values.Any(property =>
                        property != null
                        && property.ToString()!.Contains(value, StringComparison.OrdinalIgnoreCase)
                    )
                );
        }
    }

    protected void AssertBodyDoesNotContain(params string[] values)
    {
        string body = Body.ToJsonString();

        foreach (string value in values)
        {
            body.Should().NotContain(value);
        }
    }

    [TestFixture]
    public class Given_The_Fingerprint_Matches
        : ValidateEducationOrganizationProjectionTargetSchemaMiddlewareTests
    {
        private DatabaseFingerprint _fingerprint = null!;

        [SetUp]
        public async Task Setup()
        {
            _fingerprint = Fingerprint(ExpectedHash);
            A.CallTo(() => FingerprintReader.ReadFingerprintAsync(Target)).Returns(_fingerprint);
            await Execute();
        }

        [Test]
        public void It_continues() => NextCalled.Should().BeTrue();

        [Test]
        public void It_records_the_fingerprint() =>
            RequestInfo.DatabaseFingerprint.Should().BeSameAs(_fingerprint);

        [Test]
        public void It_writes_no_response() =>
            RequestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
    }

    [TestFixture]
    public class Given_No_Fingerprint_Row : ValidateEducationOrganizationProjectionTargetSchemaMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => FingerprintReader.ReadFingerprintAsync(Target))
                .Returns(Task.FromResult<DatabaseFingerprint?>(null));
            await Execute();
        }

        [Test]
        public void It_answers_database_not_provisioned() =>
            AssertRejectedWith(
                503,
                "urn:ed-fi:api:database-not-provisioned",
                "Database Not Provisioned",
                "The data store's database has not been provisioned."
            );

        /// <summary>
        /// The verdict comes from the cache every endpoint shares, which retains a Primary verdict
        /// until restart, so the projection answers the same as every other endpoint would.
        /// </summary>
        [Test]
        public async Task It_answers_the_next_request_from_the_shared_primary_verdict()
        {
            await Execute();
            RequestInfo.FrontendResponse.StatusCode.Should().Be(503);
            A.CallTo(() => FingerprintReader.ReadFingerprintAsync(Target)).MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    public class Given_A_Fingerprint_For_Another_Effective_Schema
        : ValidateEducationOrganizationProjectionTargetSchemaMiddlewareTests
    {
        private static readonly string _databaseHash = "hostile" + new string('b', 57);

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => FingerprintReader.ReadFingerprintAsync(Target))
                .Returns(Fingerprint(_databaseHash));
            await Execute();
        }

        [Test]
        public void It_answers_target_schema_incompatible() => AssertSchemaIncompatible();

        [Test]
        public void It_logs_neither_hash() => AssertNotLogged(_databaseHash, ExpectedHash);

        [Test]
        public void It_returns_neither_hash() => AssertBodyDoesNotContain(_databaseHash, ExpectedHash);
    }

    [TestFixture]
    public class Given_A_Malformed_Fingerprint
        : ValidateEducationOrganizationProjectionTargetSchemaMiddlewareTests
    {
        private const string Issue = "dms.EffectiveSchema row hostile-issue is malformed";

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => FingerprintReader.ReadFingerprintAsync(Target))
                .ThrowsAsync(new DatabaseFingerprintValidationException([Issue, Hostile]));
            await Execute();
        }

        [Test]
        public void It_answers_target_schema_incompatible_not_a_transient_failure() =>
            AssertSchemaIncompatible();

        [Test]
        public void It_logs_no_validation_issue() => AssertNotLogged("hostile");

        [Test]
        public void It_returns_no_validation_issue() => AssertBodyDoesNotContain("hostile", "malformed");
    }

    [TestFixture]
    public class Given_The_Fingerprint_Read_Fails_Transiently
        : ValidateEducationOrganizationProjectionTargetSchemaMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => FingerprintReader.ReadFingerprintAsync(Target))
                .ThrowsAsync(new TimeoutException(Hostile));
            await Execute();
        }

        [Test]
        public void It_answers_target_unavailable_never_service_configuration_error() =>
            AssertTargetUnavailable();

        [Test]
        public void It_logs_the_exception_type_only()
        {
            AssertNotLogged("hostile", "s3cret");
            Logger
                .Records.Should()
                .Contain(record => Equals(record.Properties["Failure"], nameof(TimeoutException)));
        }

        [Test]
        public void It_returns_nothing_the_exception_carries() => AssertBodyDoesNotContain("hostile");
    }

    [TestFixture]
    public class Given_The_Fingerprint_Read_Cannot_Acquire_A_Connection
        : ValidateEducationOrganizationProjectionTargetSchemaMiddlewareTests
    {
        private const string Description = "NpgsqlException(08001)";

        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => FingerprintReader.ReadFingerprintAsync(Target))
                .ThrowsAsync(
                    new DatabaseConnectionUnavailableException(
                        EffectiveTargetKind.Primary,
                        Description,
                        new InvalidOperationException(Hostile)
                    )
                );
            await Execute();
        }

        [Test]
        public void It_answers_target_unavailable() => AssertTargetUnavailable();

        [Test]
        public void It_logs_the_providers_log_safe_description_only()
        {
            AssertNotLogged("hostile", "s3cret");
            Logger.Records.Should().Contain(record => Equals(record.Properties["Failure"], Description));
        }
    }

    [TestFixture]
    public class Given_The_Request_Is_Cancelled_While_The_Fingerprint_Is_Read
        : ValidateEducationOrganizationProjectionTargetSchemaMiddlewareTests
    {
        [Test]
        public async Task It_stops_waiting_and_propagates_the_cancellation()
        {
            // The read does not complete while the request runs, so only the request's cancellation
            // can end the wait. With the token already cancelled that happens before Execute returns,
            // so the check needs no timing; a step that ignored the token would leave the task
            // pending, and releasing the read afterwards keeps that failure from hanging the run.
            TaskCompletionSource<DatabaseFingerprint?> pendingRead = new();
            A.CallTo(() => FingerprintReader.ReadFingerprintAsync(Target)).Returns(pendingRead.Task);
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync();

            Task execution = Execute(cancellation.Token);
            bool stoppedWaiting = execution.IsCompleted;
            pendingRead.SetResult(Fingerprint(ExpectedHash));

            stoppedWaiting.Should().BeTrue();
            Func<Task> act = () => execution;
            await act.Should().ThrowAsync<OperationCanceledException>();
            NextCalled.Should().BeFalse();
            RequestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        }
    }
}
