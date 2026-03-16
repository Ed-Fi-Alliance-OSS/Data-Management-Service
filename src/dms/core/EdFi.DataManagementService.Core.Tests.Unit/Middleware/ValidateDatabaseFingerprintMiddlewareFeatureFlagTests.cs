// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core;
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
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ValidateDatabaseFingerprintMiddlewareFeatureFlagTests
{
    internal static (
        ValidateDatabaseFingerprintMiddleware middleware,
        IDatabaseFingerprintReader fingerprintReader,
        IDmsInstanceSelection dmsInstanceSelection,
        IServiceProvider serviceProvider
    ) CreateMiddleware(bool enableFingerprintValidation, bool validateProvisionedMappingsOnStartup = false)
    {
        var fingerprintReader = A.Fake<IDatabaseFingerprintReader>();
        var dmsInstanceSelection = A.Fake<IDmsInstanceSelection>();
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

        var appSettings = Options.Create(
            new AppSettings
            {
                AllowIdentityUpdateOverrides = "",
                UseRelationalBackend = enableFingerprintValidation,
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
            effectiveSchemaSetProvider,
            logger
        );

        return (middleware, fingerprintReader, dmsInstanceSelection, serviceProvider);
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
                DmsInstanceIds: [new DmsInstanceId(1)]
            ),
        };
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Feature_Flag_Is_Disabled : ValidateDatabaseFingerprintMiddlewareFeatureFlagTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;
        private IDatabaseFingerprintReader _fingerprintReader = null!;
        private IDmsInstanceSelection _dmsInstanceSelection = null!;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, fingerprintReader, dmsInstanceSelection, serviceProvider) = CreateMiddleware(
                enableFingerprintValidation: false
            );
            _fingerprintReader = fingerprintReader;
            _dmsInstanceSelection = dmsInstanceSelection;
            _requestInfo = CreateRequestInfoWithAuthorizations(serviceProvider);

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
        public void It_does_not_interact_with_fingerprint_reader()
        {
            A.CallTo(() => _fingerprintReader.ReadFingerprintAsync(A<string>.Ignored)).MustNotHaveHappened();
        }

        [Test]
        public void It_does_not_interact_with_dms_instance_selection()
        {
            A.CallTo(_dmsInstanceSelection).MustNotHaveHappened();
        }

        [Test]
        public void It_does_not_set_database_fingerprint_on_request_info()
        {
            _requestInfo.DatabaseFingerprint.Should().BeNull();
        }

        [Test]
        public void It_does_not_set_error_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Feature_Flag_Is_Enabled_And_Fingerprint_Is_Returned
        : ValidateDatabaseFingerprintMiddlewareFeatureFlagTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, fingerprintReader, dmsInstanceSelection, serviceProvider) = CreateMiddleware(
                enableFingerprintValidation: true
            );
            _requestInfo = CreateRequestInfoWithAuthorizations(serviceProvider);

            A.CallTo(() => dmsInstanceSelection.IsSet).Returns(true);
            A.CallTo(() => dmsInstanceSelection.GetSelectedDmsInstance())
                .Returns(
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "Test",
                        InstanceName: "Test Instance",
                        ConnectionString: "Server=test;Database=testdb",
                        RouteContext: []
                    )
                );

            A.CallTo(() => fingerprintReader.ReadFingerprintAsync("Server=test;Database=testdb"))
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
    public class Given_Feature_Flag_Is_Enabled_And_Fingerprint_Is_Null
        : ValidateDatabaseFingerprintMiddlewareFeatureFlagTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, fingerprintReader, dmsInstanceSelection, serviceProvider) = CreateMiddleware(
                enableFingerprintValidation: true
            );
            _requestInfo = CreateRequestInfoWithAuthorizations(serviceProvider);

            A.CallTo(() => dmsInstanceSelection.IsSet).Returns(true);
            A.CallTo(() => dmsInstanceSelection.GetSelectedDmsInstance())
                .Returns(
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "Test",
                        InstanceName: "Test Instance",
                        ConnectionString: "Server=test;Database=unprovisioned",
                        RouteContext: []
                    )
                );

            A.CallTo(() => fingerprintReader.ReadFingerprintAsync("Server=test;Database=unprovisioned"))
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
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("restart DMS");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Feature_Flag_Is_Enabled_And_Selected_Instance_Has_No_Connection_String
        : ValidateDatabaseFingerprintMiddlewareFeatureFlagTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;
        private IDatabaseFingerprintReader _fingerprintReader = null!;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, fingerprintReader, dmsInstanceSelection, serviceProvider) = CreateMiddleware(
                enableFingerprintValidation: true
            );
            _fingerprintReader = fingerprintReader;
            _requestInfo = CreateRequestInfoWithAuthorizations(serviceProvider);

            A.CallTo(() => dmsInstanceSelection.IsSet).Returns(true);
            A.CallTo(() => dmsInstanceSelection.GetSelectedDmsInstance())
                .Returns(
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "Test",
                        InstanceName: "Test Instance",
                        ConnectionString: null,
                        RouteContext: []
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
        }

        [Test]
        public void It_does_not_call_next()
        {
            _nextCalled.Should().BeFalse();
        }

        [Test]
        public void It_does_not_interact_with_the_fingerprint_reader()
        {
            A.CallTo(() => _fingerprintReader.ReadFingerprintAsync(A<string>.Ignored)).MustNotHaveHappened();
        }

        [Test]
        public void It_returns_503_service_unavailable()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public void It_returns_a_service_configuration_error()
        {
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("Service Configuration Error");
            _requestInfo
                .FrontendResponse.Body!.ToString()
                .Should()
                .Contain("Database connection not configured");
        }

        [Test]
        public void It_does_not_set_database_fingerprint()
        {
            _requestInfo.DatabaseFingerprint.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Feature_Flag_Is_Enabled_And_No_Dialect_Fingerprint_Reader_Is_Registered
        : ValidateDatabaseFingerprintMiddlewareFeatureFlagTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private Func<Task> _execute = null!;

        [SetUp]
        public void Setup()
        {
            var dmsInstanceSelection = A.Fake<IDmsInstanceSelection>();
            A.CallTo(() => dmsInstanceSelection.IsSet).Returns(true);
            A.CallTo(() => dmsInstanceSelection.GetSelectedDmsInstance())
                .Returns(
                    new DmsInstance(
                        Id: 1,
                        InstanceType: "Test",
                        InstanceName: "Test Instance",
                        ConnectionString: "Server=test;Database=testdb",
                        RouteContext: []
                    )
                );

            var serviceProvider = A.Fake<IServiceProvider>();
            A.CallTo(() => serviceProvider.GetService(typeof(IDmsInstanceSelection)))
                .Returns(dmsInstanceSelection);

            var appSettings = Options.Create(
                new AppSettings { AllowIdentityUpdateOverrides = "", UseRelationalBackend = true }
            );

            var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet)
                .Returns(
                    new EffectiveSchemaSet(
                        new EffectiveSchemaInfo("1.0", "1.0", "abc123", 0, new byte[32], [], []),
                        []
                    )
                );

            var middleware = new ValidateDatabaseFingerprintMiddleware(
                appSettings,
                new DatabaseFingerprintProvider(new MissingDatabaseFingerprintReader(appSettings)),
                schemaSetProvider,
                A.Fake<ILogger<ValidateDatabaseFingerprintMiddleware>>()
            );

            _requestInfo = CreateRequestInfoWithAuthorizations(serviceProvider);
            _execute = () => middleware.Execute(_requestInfo, () => Task.CompletedTask);
        }

        [Test]
        public async Task It_throws_a_configuration_error()
        {
            var exception = await _execute.Should().ThrowAsync<InvalidOperationException>();

            exception.Which.Message.Should().Be(MissingDatabaseFingerprintReader.ConfigurationErrorMessage);
        }

        [Test]
        public async Task It_does_not_return_the_database_not_provisioned_response()
        {
            await _execute.Should().ThrowAsync<InvalidOperationException>();

            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }
}
