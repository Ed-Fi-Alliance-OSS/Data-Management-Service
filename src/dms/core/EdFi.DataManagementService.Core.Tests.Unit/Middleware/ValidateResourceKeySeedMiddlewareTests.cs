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
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ValidateResourceKeySeedMiddlewareTests
{
    internal static (
        ValidateResourceKeySeedMiddleware middleware,
        IResourceKeyValidator validator,
        ResourceKeyValidationCacheProvider cacheProvider,
        IEffectiveSchemaSetProvider schemaSetProvider,
        IDmsInstanceSelection dmsInstanceSelection,
        IServiceProvider serviceProvider
    ) CreateMiddleware(bool useRelationalBackend)
    {
        var validator = A.Fake<IResourceKeyValidator>();
        var cacheProvider = new ResourceKeyValidationCacheProvider();
        var schemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();
        var dmsInstanceSelection = A.Fake<IDmsInstanceSelection>();
        var logger = A.Fake<ILogger<ValidateResourceKeySeedMiddleware>>();

        var appSettings = Options.Create(
            new AppSettings { AllowIdentityUpdateOverrides = "", UseRelationalBackend = useRelationalBackend }
        );

        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IDmsInstanceSelection)))
            .Returns(dmsInstanceSelection);

        var middleware = new ValidateResourceKeySeedMiddleware(
            appSettings,
            validator,
            cacheProvider,
            schemaSetProvider,
            logger
        );

        return (
            middleware,
            validator,
            cacheProvider,
            schemaSetProvider,
            dmsInstanceSelection,
            serviceProvider
        );
    }

    private static RequestInfo CreateRequestInfoWithFingerprint(
        IServiceProvider serviceProvider,
        DatabaseFingerprint? fingerprint = null
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

        return new RequestInfo(frontendRequest, RequestMethod.GET, serviceProvider)
        {
            ClientAuthorizations = new ClientAuthorizations(
                TokenId: "token123",
                ClientId: "client123",
                ClaimSetName: "test",
                EducationOrganizationIds: [],
                NamespacePrefixes: [],
                DmsInstanceIds: [new DmsInstanceId(1)]
            ),
            DatabaseFingerprint = fingerprint,
        };
    }

    private static EffectiveSchemaInfo CreateMinimalEffectiveSchemaInfo()
    {
        return new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "1.0",
            EffectiveSchemaHash: "abc123",
            ResourceKeyCount: 2,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: []
        );
    }

    private static EffectiveSchemaSet CreateMinimalEffectiveSchemaSet()
    {
        return new EffectiveSchemaSet(CreateMinimalEffectiveSchemaInfo(), []);
    }

    private static void SetupDmsInstanceSelection(IDmsInstanceSelection dmsInstanceSelection)
    {
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
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Feature_Flag_Is_Disabled : ValidateResourceKeySeedMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;
        private IResourceKeyValidator _validator = null!;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, validator, _, _, _, serviceProvider) = CreateMiddleware(
                useRelationalBackend: false
            );
            _validator = validator;
            _requestInfo = CreateRequestInfoWithFingerprint(serviceProvider);

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
        public void It_does_not_interact_with_validator()
        {
            A.CallTo(_validator).MustNotHaveHappened();
        }

        [Test]
        public void It_does_not_set_error_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Feature_Flag_Is_Enabled_But_Fingerprint_Is_Null
        : ValidateResourceKeySeedMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;
        private IResourceKeyValidator _validator = null!;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, validator, _, _, _, serviceProvider) = CreateMiddleware(
                useRelationalBackend: true
            );
            _validator = validator;
            _requestInfo = CreateRequestInfoWithFingerprint(serviceProvider, fingerprint: null);

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
        public void It_does_not_interact_with_validator()
        {
            A.CallTo(_validator).MustNotHaveHappened();
        }

        [Test]
        public void It_does_not_set_error_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Validation_Succeeds : ValidateResourceKeySeedMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, validator, _, schemaSetProvider, dmsInstanceSelection, serviceProvider) =
                CreateMiddleware(useRelationalBackend: true);

            SetupDmsInstanceSelection(dmsInstanceSelection);
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(CreateMinimalEffectiveSchemaSet());

            A.CallTo(() =>
                    validator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<string>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ResourceKeyValidationResult.ValidationSuccess());

            _requestInfo = CreateRequestInfoWithFingerprint(
                serviceProvider,
                new DatabaseFingerprint("1.0", "abc123", 2, new byte[32].ToImmutableArray())
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
        public void It_calls_next()
        {
            _nextCalled.Should().BeTrue();
        }

        [Test]
        public void It_does_not_set_error_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Validation_Fails : ValidateResourceKeySeedMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, validator, _, schemaSetProvider, dmsInstanceSelection, serviceProvider) =
                CreateMiddleware(useRelationalBackend: true);

            SetupDmsInstanceSelection(dmsInstanceSelection);
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(CreateMinimalEffectiveSchemaSet());

            A.CallTo(() =>
                    validator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<string>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ResourceKeyValidationResult.ValidationFailure("test diff report"));

            _requestInfo = CreateRequestInfoWithFingerprint(
                serviceProvider,
                new DatabaseFingerprint("1.0", "abc123", 2, new byte[32].ToImmutableArray())
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
        public void It_returns_503_service_unavailable()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public void It_returns_error_body()
        {
            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("Resource Key Seed Mismatch");
        }

        [Test]
        public void It_includes_reprovisioning_guidance()
        {
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("reprovisioned");
        }

        [Test]
        public void It_does_not_include_diff_report_in_response_body()
        {
            _requestInfo.FrontendResponse.Body!.ToString().Should().NotContain("test diff report");
        }

        [Test]
        public void It_returns_resource_key_seed_validation_error_type()
        {
            _requestInfo
                .FrontendResponse.Body!.ToString()
                .Should()
                .Contain("urn:ed-fi:api:resource-key-seed-validation-error");
        }

        [Test]
        public void It_does_not_return_database_fingerprint_error_type()
        {
            _requestInfo
                .FrontendResponse.Body!.ToString()
                .Should()
                .NotContain("urn:ed-fi:api:database-fingerprint-validation-error");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Validation_Succeeds_And_A_Second_Request_Arrives
        : ValidateResourceKeySeedMiddlewareTests
    {
        private RequestInfo _requestInfo1 = No.RequestInfo();
        private RequestInfo _requestInfo2 = No.RequestInfo();
        private bool _nextCalled1;
        private bool _nextCalled2;
        private IResourceKeyValidator _validator = null!;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, validator, _, schemaSetProvider, dmsInstanceSelection, serviceProvider) =
                CreateMiddleware(useRelationalBackend: true);
            _validator = validator;

            SetupDmsInstanceSelection(dmsInstanceSelection);
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(CreateMinimalEffectiveSchemaSet());

            A.CallTo(() =>
                    validator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<string>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ResourceKeyValidationResult.ValidationSuccess());

            var fingerprint = new DatabaseFingerprint("1.0", "abc123", 2, new byte[32].ToImmutableArray());

            _requestInfo1 = CreateRequestInfoWithFingerprint(serviceProvider, fingerprint);
            _requestInfo2 = CreateRequestInfoWithFingerprint(serviceProvider, fingerprint);

            await middleware.Execute(
                _requestInfo1,
                () =>
                {
                    _nextCalled1 = true;
                    return Task.CompletedTask;
                }
            );

            await middleware.Execute(
                _requestInfo2,
                () =>
                {
                    _nextCalled2 = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_calls_next_on_both_requests()
        {
            _nextCalled1.Should().BeTrue();
            _nextCalled2.Should().BeTrue();
        }

        [Test]
        public void It_invokes_validator_only_once()
        {
            A.CallTo(() =>
                    _validator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<string>._,
                        A<CancellationToken>._
                    )
                )
                .MustHaveHappenedOnceExactly();
        }

        [Test]
        public void It_does_not_set_error_response_on_either_request()
        {
            _requestInfo1.FrontendResponse.Should().Be(No.FrontendResponse);
            _requestInfo2.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Validation_Fails_And_A_Second_Request_Arrives : ValidateResourceKeySeedMiddlewareTests
    {
        private RequestInfo _requestInfo1 = No.RequestInfo();
        private RequestInfo _requestInfo2 = No.RequestInfo();
        private bool _nextCalled1;
        private bool _nextCalled2;
        private IResourceKeyValidator _validator = null!;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, validator, _, schemaSetProvider, dmsInstanceSelection, serviceProvider) =
                CreateMiddleware(useRelationalBackend: true);
            _validator = validator;

            SetupDmsInstanceSelection(dmsInstanceSelection);
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(CreateMinimalEffectiveSchemaSet());

            A.CallTo(() =>
                    validator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<string>._,
                        A<CancellationToken>._
                    )
                )
                .Returns(new ResourceKeyValidationResult.ValidationFailure("cached diff"));

            var fingerprint = new DatabaseFingerprint("1.0", "abc123", 2, new byte[32].ToImmutableArray());

            _requestInfo1 = CreateRequestInfoWithFingerprint(serviceProvider, fingerprint);
            _requestInfo2 = CreateRequestInfoWithFingerprint(serviceProvider, fingerprint);

            await middleware.Execute(
                _requestInfo1,
                () =>
                {
                    _nextCalled1 = true;
                    return Task.CompletedTask;
                }
            );

            await middleware.Execute(
                _requestInfo2,
                () =>
                {
                    _nextCalled2 = true;
                    return Task.CompletedTask;
                }
            );
        }

        [Test]
        public void It_does_not_call_next_on_either_request()
        {
            _nextCalled1.Should().BeFalse();
            _nextCalled2.Should().BeFalse();
        }

        [Test]
        public void It_returns_503_on_both_requests()
        {
            _requestInfo1.FrontendResponse.StatusCode.Should().Be(503);
            _requestInfo2.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public void It_invokes_validator_only_once()
        {
            A.CallTo(() =>
                    _validator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<string>._,
                        A<CancellationToken>._
                    )
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Validator_Throws_Exception : ValidateResourceKeySeedMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, validator, _, schemaSetProvider, dmsInstanceSelection, serviceProvider) =
                CreateMiddleware(useRelationalBackend: true);

            SetupDmsInstanceSelection(dmsInstanceSelection);
            A.CallTo(() => schemaSetProvider.EffectiveSchemaSet).Returns(CreateMinimalEffectiveSchemaSet());

            A.CallTo(() =>
                    validator.ValidateAsync(
                        A<DatabaseFingerprint>._,
                        A<short>._,
                        A<ImmutableArray<byte>>._,
                        A<IReadOnlyList<ResourceKeyRow>>._,
                        A<string>._,
                        A<CancellationToken>._
                    )
                )
                .ThrowsAsync(new TimeoutException("connection timed out"));

            _requestInfo = CreateRequestInfoWithFingerprint(
                serviceProvider,
                new DatabaseFingerprint("1.0", "abc123", 2, new byte[32].ToImmutableArray())
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
        public void It_returns_503_service_unavailable()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public void It_returns_unexpected_error_message()
        {
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("unexpected error");
        }

        [Test]
        public void It_does_not_leak_exception_message_in_response_body()
        {
            _requestInfo.FrontendResponse.Body!.ToString().Should().NotContain("connection timed out");
        }

        [Test]
        public void It_returns_resource_key_seed_validation_error_type()
        {
            _requestInfo
                .FrontendResponse.Body!.ToString()
                .Should()
                .Contain("urn:ed-fi:api:resource-key-seed-validation-error");
        }
    }
}
