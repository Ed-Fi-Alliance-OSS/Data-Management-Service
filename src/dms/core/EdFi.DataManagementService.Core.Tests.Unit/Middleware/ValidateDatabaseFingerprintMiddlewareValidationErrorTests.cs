// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Linq;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[Parallelizable]
public class ValidateDatabaseFingerprintMiddlewareValidationErrorTests
{
    private const string DatabaseFingerprintValidationErrorType =
        "urn:ed-fi:api:database-fingerprint-validation-error";

    private const string MalformedFingerprintDetail =
        "The target database contains malformed dms.EffectiveSchema provisioning metadata. Repair the database by re-running 'ddl provision' against an empty database. If provisioning was partial or the database was modified after provisioning, drop and recreate the database before reprovisioning. Restart DMS after the database has been repaired to clear the cached fingerprint validation failure.";

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
                DmsInstanceIds: [new DmsInstanceId(1)]
            ),
        };
    }

    private static (
        ValidateDatabaseFingerprintMiddleware middleware,
        IDatabaseFingerprintReader fingerprintReader,
        IDmsInstanceSelection dmsInstanceSelection,
        CapturingLogger<ValidateDatabaseFingerprintMiddleware> middlewareLogger,
        IServiceProvider serviceProvider
    ) CreateMiddleware(bool validateProvisionedMappingsOnStartup = false)
    {
        var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();
        var dmsInstanceSelection = A.Fake<IDmsInstanceSelection>();
        var middlewareLogger = new CapturingLogger<ValidateDatabaseFingerprintMiddleware>();

        var appSettings = Options.Create(
            new AppSettings
            {
                AllowIdentityUpdateOverrides = "",
                UseRelationalBackend = true,
                ValidateProvisionedMappingsOnStartup = validateProvisionedMappingsOnStartup,
            }
        );

        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDmsInstanceSelection)))
            .Returns(dmsInstanceSelection);

        var fingerprintProvider = new DatabaseFingerprintProvider(fingerprintReader);
        var middleware = new ValidateDatabaseFingerprintMiddleware(
            appSettings,
            fingerprintProvider,
            middlewareLogger
        );

        return (middleware, fingerprintReader, dmsInstanceSelection, middlewareLogger, serviceProvider);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

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
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

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
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Database_Fingerprint_Has_Duplicate_Rows
        : ValidateDatabaseFingerprintMiddlewareValidationErrorTests
    {
        private const string DuplicateRowsMessage =
            "dms.EffectiveSchema must contain exactly one singleton row, but multiple rows were found.";

        private RequestInfo _requestInfo = No.RequestInfo();
        private JsonNode _body = default!;
        private bool _nextCalled;
        private IDatabaseFingerprintReader _fingerprintReader = null!;
        private CapturingLogger<ValidateDatabaseFingerprintMiddleware> _middlewareLogger = null!;
        private CapturingLogger _exceptionLogger = null!;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, fingerprintReader, dmsInstanceSelection, middlewareLogger, serviceProvider) =
                CreateMiddleware(validateProvisionedMappingsOnStartup: false);

            _fingerprintReader = fingerprintReader;
            _requestInfo = CreateRequestInfoWithAuthorizations(serviceProvider);
            _middlewareLogger = middlewareLogger;
            _exceptionLogger = new CapturingLogger();

            A.CallTo(() => dmsInstanceSelection.IsSet).Returns(true);
            A.CallTo(() => dmsInstanceSelection.GetSelectedDmsInstance())
                .Returns(
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "Test",
                        InstanceName: "Test Instance",
                        ConnectionString: "Server=test;Database=corrupt",
                        RouteContext: []
                    )
                );

            A.CallTo(() => fingerprintReader.ReadFingerprintAsync("Server=test;Database=corrupt"))
                .Returns(
                    Task.FromException<DatabaseFingerprint?>(
                        new DatabaseFingerprintValidationException(DuplicateRowsMessage)
                    )
                );

            var exceptionLoggingMiddleware = new CoreExceptionLoggingMiddleware(_exceptionLogger);

            await exceptionLoggingMiddleware.Execute(
                _requestInfo,
                () =>
                    middleware.Execute(
                        _requestInfo,
                        () =>
                        {
                            _nextCalled = true;
                            return Task.CompletedTask;
                        }
                    )
            );

            _body = ((FrontendResponse)_requestInfo.FrontendResponse).Body!;
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
        public void It_returns_json_content_type()
        {
            ((FrontendResponse)_requestInfo.FrontendResponse).ContentType.Should().Be("application/json");
        }

        [Test]
        public void It_returns_the_database_fingerprint_validation_problem_type()
        {
            _body["type"]?.GetValue<string>().Should().Be(DatabaseFingerprintValidationErrorType);
        }

        [Test]
        public void It_returns_the_database_provisioning_error_title()
        {
            _body["title"]?.GetValue<string>().Should().Be("Database Provisioning Error");
        }

        [Test]
        public void It_returns_the_expected_detail_message()
        {
            _body["detail"]?.GetValue<string>().Should().Be(MalformedFingerprintDetail);
        }

        [Test]
        public void It_returns_the_validation_message_and_remediation_in_errors()
        {
            var errors = _body["errors"]!.AsArray();

            errors.Count.Should().Be(2);
            errors[0]?.GetValue<string>().Should().Be(DuplicateRowsMessage);
            errors[1]?.GetValue<string>().Should().Be(MalformedFingerprintDetail);
        }

        [Test]
        public void It_returns_the_request_trace_id_as_correlation_id()
        {
            _body["correlationId"]?.GetValue<string>().Should().Be("test-trace-id");
        }

        [Test]
        public void It_leaves_validation_errors_empty()
        {
            _body["validationErrors"]!.AsObject().Count.Should().Be(0);
        }

        [Test]
        public void It_does_not_set_the_database_fingerprint_on_the_request()
        {
            _requestInfo.DatabaseFingerprint.Should().BeNull();
        }

        [Test]
        public void It_logs_the_selected_instance_and_trace_id()
        {
            _middlewareLogger
                .Entries.Should()
                .Contain(entry =>
                    entry.Level == LogLevel.Error
                    && entry.Message.Contains("Malformed dms.EffectiveSchema fingerprint")
                    && entry.Message.Contains("Restart DMS after repairing the database")
                    && entry.Message.Contains("Test Instance")
                    && entry.Message.Contains("test-trace-id")
                );
        }

        [Test]
        public void It_still_reads_the_database_fingerprint_when_startup_validation_bypass_is_enabled()
        {
            A.CallTo(() => _fingerprintReader.ReadFingerprintAsync("Server=test;Database=corrupt"))
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_does_not_fall_through_the_unknown_error_handler()
        {
            _exceptionLogger.Entries.Should().NotContain(entry => entry.Message.Contains("Unknown Error"));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Database_Fingerprint_Has_Invalid_ResourceKeySeedHash
        : ValidateDatabaseFingerprintMiddlewareValidationErrorTests
    {
        private const string InvalidSeedHashMessage =
            "dms.EffectiveSchema.ResourceKeySeedHash must be exactly 32 bytes, but found 31.";

        private RequestInfo _requestInfo = No.RequestInfo();
        private JsonNode _body = default!;
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, fingerprintReader, dmsInstanceSelection, _, serviceProvider) =
                CreateMiddleware();

            _requestInfo = CreateRequestInfoWithAuthorizations(serviceProvider);

            A.CallTo(() => dmsInstanceSelection.IsSet).Returns(true);
            A.CallTo(() => dmsInstanceSelection.GetSelectedDmsInstance())
                .Returns(
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "Test",
                        InstanceName: "Test Instance",
                        ConnectionString: "Server=test;Database=invalid-seed",
                        RouteContext: []
                    )
                );

            A.CallTo(() => fingerprintReader.ReadFingerprintAsync("Server=test;Database=invalid-seed"))
                .Returns(
                    Task.FromException<DatabaseFingerprint?>(
                        new DatabaseFingerprintValidationException(InvalidSeedHashMessage)
                    )
                );

            await middleware.Execute(
                _requestInfo,
                () =>
                {
                    _nextCalled = true;
                    return Task.CompletedTask;
                }
            );

            _body = ((FrontendResponse)_requestInfo.FrontendResponse).Body!;
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
        public void It_returns_the_database_fingerprint_validation_problem_type()
        {
            _body["type"]?.GetValue<string>().Should().Be(DatabaseFingerprintValidationErrorType);
        }

        [Test]
        public void It_returns_the_specific_seed_hash_validation_message()
        {
            _body["errors"]?[0]?.GetValue<string>().Should().Be(InvalidSeedHashMessage);
        }

        [Test]
        public void It_includes_ddl_provision_remediation()
        {
            _body["detail"]?.GetValue<string>().Should().Be(MalformedFingerprintDetail);
        }

        [Test]
        public void It_includes_restart_guidance_in_the_detail_message()
        {
            _body["detail"]?.GetValue<string>().Should().Contain("Restart DMS");
        }

        [Test]
        public void It_does_not_set_the_database_fingerprint_on_the_request()
        {
            _requestInfo.DatabaseFingerprint.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Cached_Database_Fingerprint_Validation_Error_Has_Multiple_Issues
        : ValidateDatabaseFingerprintMiddlewareValidationErrorTests
    {
        private static readonly string[] ValidationIssues =
        [
            "dms.EffectiveSchema.ApiSchemaFormatVersion must not be null or empty.",
            "dms.EffectiveSchema.ResourceKeySeedHash must be exactly 32 bytes, but found 31.",
        ];

        private RequestInfo _firstRequestInfo = No.RequestInfo();
        private RequestInfo _secondRequestInfo = No.RequestInfo();
        private JsonNode _firstBody = default!;
        private JsonNode _secondBody = default!;
        private int _nextCallCount;
        private IDatabaseFingerprintReader _fingerprintReader = null!;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, fingerprintReader, dmsInstanceSelection, _, serviceProvider) =
                CreateMiddleware();

            _fingerprintReader = fingerprintReader;
            _firstRequestInfo = CreateRequestInfoWithAuthorizations(serviceProvider);
            _secondRequestInfo = CreateRequestInfoWithAuthorizations(serviceProvider);

            A.CallTo(() => dmsInstanceSelection.IsSet).Returns(true);
            A.CallTo(() => dmsInstanceSelection.GetSelectedDmsInstance())
                .Returns(
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "Test",
                        InstanceName: "Test Instance",
                        ConnectionString: "Server=test;Database=multi-issue",
                        RouteContext: []
                    )
                );

            A.CallTo(() => fingerprintReader.ReadFingerprintAsync("Server=test;Database=multi-issue"))
                .Returns(
                    Task.FromException<DatabaseFingerprint?>(
                        new DatabaseFingerprintValidationException(ValidationIssues)
                    )
                );

            await middleware.Execute(
                _firstRequestInfo,
                () =>
                {
                    _nextCallCount++;
                    return Task.CompletedTask;
                }
            );

            await middleware.Execute(
                _secondRequestInfo,
                () =>
                {
                    _nextCallCount++;
                    return Task.CompletedTask;
                }
            );

            _firstBody = ((FrontendResponse)_firstRequestInfo.FrontendResponse).Body!;
            _secondBody = ((FrontendResponse)_secondRequestInfo.FrontendResponse).Body!;
        }

        [Test]
        public void It_does_not_call_next()
        {
            _nextCallCount.Should().Be(0);
        }

        [Test]
        public void It_returns_503_for_each_request()
        {
            _firstRequestInfo.FrontendResponse.StatusCode.Should().Be(503);
            _secondRequestInfo.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public void It_returns_all_validation_issues_in_order_plus_remediation_for_each_request()
        {
            string[] expectedErrors = [ValidationIssues[0], ValidationIssues[1], MalformedFingerprintDetail];

            _firstBody["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .Equal(expectedErrors);

            _secondBody["errors"]!
                .AsArray()
                .Select(error => error!.GetValue<string>())
                .Should()
                .Equal(expectedErrors);
        }

        [Test]
        public void It_keeps_the_shared_detail_message()
        {
            _secondBody["detail"]?.GetValue<string>().Should().Be(MalformedFingerprintDetail);
        }

        [Test]
        public void It_uses_the_cached_validation_failure_without_retrying_the_reader()
        {
            A.CallTo(() => _fingerprintReader.ReadFingerprintAsync("Server=test;Database=multi-issue"))
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Database_Fingerprint_Runtime_Projection_Is_Invalid
        : ValidateDatabaseFingerprintMiddlewareValidationErrorTests
    {
        private const string ProjectionFailureMessage =
            "dms.EffectiveSchema does not match the expected fingerprint projection. Required fingerprint columns may be missing, renamed, or incompatible with the runtime query.";

        private RequestInfo _requestInfo = No.RequestInfo();
        private JsonNode _body = default!;
        private bool _nextCalled;
        private CapturingLogger _exceptionLogger = null!;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, fingerprintReader, dmsInstanceSelection, _, serviceProvider) =
                CreateMiddleware();

            _requestInfo = CreateRequestInfoWithAuthorizations(serviceProvider);
            _exceptionLogger = new CapturingLogger();

            A.CallTo(() => dmsInstanceSelection.IsSet).Returns(true);
            A.CallTo(() => dmsInstanceSelection.GetSelectedDmsInstance())
                .Returns(
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "Test",
                        InstanceName: "Test Instance",
                        ConnectionString: "Server=test;Database=projection-failure",
                        RouteContext: []
                    )
                );

            A.CallTo(() => fingerprintReader.ReadFingerprintAsync("Server=test;Database=projection-failure"))
                .Returns(
                    Task.FromException<DatabaseFingerprint?>(
                        new DatabaseFingerprintValidationException(ProjectionFailureMessage)
                    )
                );

            var exceptionLoggingMiddleware = new CoreExceptionLoggingMiddleware(_exceptionLogger);

            await exceptionLoggingMiddleware.Execute(
                _requestInfo,
                () =>
                    middleware.Execute(
                        _requestInfo,
                        () =>
                        {
                            _nextCalled = true;
                            return Task.CompletedTask;
                        }
                    )
            );

            _body = ((FrontendResponse)_requestInfo.FrontendResponse).Body!;
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
        public void It_returns_the_database_fingerprint_validation_problem_type()
        {
            _body["type"]?.GetValue<string>().Should().Be(DatabaseFingerprintValidationErrorType);
        }

        [Test]
        public void It_returns_the_projection_failure_message_and_shared_remediation()
        {
            var errors = _body["errors"]!.AsArray();

            errors.Count.Should().Be(2);
            errors[0]?.GetValue<string>().Should().Be(ProjectionFailureMessage);
            errors[1]?.GetValue<string>().Should().Be(MalformedFingerprintDetail);
            _body["detail"]?.GetValue<string>().Should().Be(MalformedFingerprintDetail);
        }

        [Test]
        public void It_does_not_fall_through_the_unknown_error_handler()
        {
            _exceptionLogger.Entries.Should().NotContain(entry => entry.Message.Contains("Unknown Error"));
        }
    }
}
