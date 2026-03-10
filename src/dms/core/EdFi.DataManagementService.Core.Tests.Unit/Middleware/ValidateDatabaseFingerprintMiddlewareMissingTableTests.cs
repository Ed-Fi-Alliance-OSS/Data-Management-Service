// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Frontend;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Middleware;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FakeItEasy;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Core.Tests.Unit.Middleware.ValidateDatabaseFingerprintMiddlewareFeatureFlagTests;

namespace EdFi.DataManagementService.Core.Tests.Unit.Middleware;

[TestFixture]
[Parallelizable]
public class ValidateDatabaseFingerprintMiddlewareMissingTableTests
{
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
    public class Given_Database_Is_Provisioned : ValidateDatabaseFingerprintMiddlewareMissingTableTests
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
        }

        [Test]
        public void It_does_not_set_error_response()
        {
            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Database_Is_Not_Provisioned : ValidateDatabaseFingerprintMiddlewareMissingTableTests
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
        public void It_returns_problem_json_content_type()
        {
            var response = (FrontendResponse)_requestInfo.FrontendResponse;
            response.ContentType.Should().Be("application/problem+json");
        }

        [Test]
        public void It_includes_database_not_provisioned_title()
        {
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("Database Not Provisioned");
        }

        [Test]
        public void It_includes_ddl_provision_guidance()
        {
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("ddl provision");
        }

        [Test]
        public void It_includes_status_503_in_body()
        {
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("503");
        }

        [Test]
        public void It_includes_urn_type()
        {
            _requestInfo
                .FrontendResponse.Body!.ToString()
                .Should()
                .Contain("urn:ed-fi:api:database-not-provisioned");
        }

        [Test]
        public void It_includes_empty_validation_errors()
        {
            _requestInfo.FrontendResponse.Body!["validationErrors"]!.AsObject().Count.Should().Be(0);
        }

        [Test]
        public void It_does_not_set_database_fingerprint()
        {
            _requestInfo.DatabaseFingerprint.Should().BeNull();
        }
    }
}
