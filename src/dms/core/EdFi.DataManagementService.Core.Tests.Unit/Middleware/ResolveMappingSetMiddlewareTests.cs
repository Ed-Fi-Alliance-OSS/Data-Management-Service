// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
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
public class ResolveMappingSetMiddlewareTests
{
    private static readonly string _testHash = new('a', 64);

    private static (
        ResolveMappingSetMiddleware middleware,
        IMappingSetProvider mappingSetProvider
    ) CreateMiddleware(
        bool useRelationalBackend,
        IRuntimeMappingSetCompiler? compiler = null,
        IMappingSetProvider? mappingSetProvider = null
    )
    {
        var provider = mappingSetProvider ?? A.Fake<IMappingSetProvider>();
        var logger = A.Fake<ILogger<ResolveMappingSetMiddleware>>();

        var effectiveSchemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();
        A.CallTo(() => effectiveSchemaSetProvider.EffectiveSchemaSet)
            .Returns(
                new EffectiveSchemaSet(
                    new EffectiveSchemaInfo("1.0", "v1", _testHash, 0, new byte[32], [], []),
                    []
                )
            );

        var appSettings = Options.Create(
            new AppSettings { AllowIdentityUpdateOverrides = "", UseRelationalBackend = useRelationalBackend }
        );

        IEnumerable<IRuntimeMappingSetCompiler> compilers = compiler is not null ? [compiler] : [];

        var middleware = new ResolveMappingSetMiddleware(
            appSettings,
            provider,
            effectiveSchemaSetProvider,
            compilers,
            logger
        );

        return (middleware, provider);
    }

    private static RequestInfo CreateRequestInfo(DatabaseFingerprint? fingerprint = null)
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

        return new RequestInfo(frontendRequest, RequestMethod.GET, No.ServiceProvider)
        {
            DatabaseFingerprint = fingerprint,
        };
    }

    private static DatabaseFingerprint CreateFingerprint(string? hash = null)
    {
        return new DatabaseFingerprint("1.0", hash ?? _testHash, 1, new byte[32].ToImmutableArray());
    }

    private static IRuntimeMappingSetCompiler CreateFakeCompiler(SqlDialect dialect = SqlDialect.Pgsql)
    {
        var compiler = A.Fake<IRuntimeMappingSetCompiler>();
        A.CallTo(() => compiler.Dialect).Returns(dialect);
        return compiler;
    }

    [TestFixture]
    [Parallelizable]
    public class Given_UseRelationalBackend_Is_False : ResolveMappingSetMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;
        private IMappingSetProvider _mappingSetProvider = null!;

        [SetUp]
        public async Task Setup()
        {
            var (middleware, mappingSetProvider) = CreateMiddleware(useRelationalBackend: false);
            _mappingSetProvider = mappingSetProvider;
            _requestInfo = CreateRequestInfo();

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
        public void It_does_not_call_mapping_set_provider()
        {
            A.CallTo(_mappingSetProvider).MustNotHaveHappened();
        }

        [Test]
        public void It_does_not_set_mapping_set_on_request_info()
        {
            _requestInfo.MappingSet.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Null_DatabaseFingerprint : ResolveMappingSetMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;
        private IMappingSetProvider _mappingSetProvider = null!;

        [SetUp]
        public async Task Setup()
        {
            var compiler = CreateFakeCompiler();
            var (middleware, mappingSetProvider) = CreateMiddleware(
                useRelationalBackend: true,
                compiler: compiler
            );
            _mappingSetProvider = mappingSetProvider;
            _requestInfo = CreateRequestInfo(fingerprint: null);

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
        public void It_does_not_call_mapping_set_provider()
        {
            A.CallTo(_mappingSetProvider).MustNotHaveHappened();
        }

        [Test]
        public void It_does_not_set_mapping_set_on_request_info()
        {
            _requestInfo.MappingSet.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Fingerprint_Present_And_Provider_Returns_MappingSet : ResolveMappingSetMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;
        private readonly MappingSet _expectedMappingSet = CreateTestMappingSet();

        private static MappingSet CreateTestMappingSet()
        {
            var key = new MappingSetKey(_testHash, SqlDialect.Pgsql, "v1");
            var effectiveSchema = new EffectiveSchemaInfo(
                ApiSchemaFormatVersion: "1.0",
                RelationalMappingVersion: "v1",
                EffectiveSchemaHash: _testHash,
                ResourceKeyCount: 0,
                ResourceKeySeedHash: new byte[32],
                SchemaComponentsInEndpointOrder: [],
                ResourceKeysInIdOrder: []
            );

            var modelSet = new DerivedRelationalModelSet(
                EffectiveSchema: effectiveSchema,
                Dialect: SqlDialect.Pgsql,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            );

            return new MappingSet(
                Key: key,
                Model: modelSet,
                WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
                ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
                ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>(),
                ResourceKeyById: new Dictionary<short, ResourceKeyEntry>()
            );
        }

        [SetUp]
        public async Task Setup()
        {
            var compiler = CreateFakeCompiler();
            var (middleware, mappingSetProvider) = CreateMiddleware(
                useRelationalBackend: true,
                compiler: compiler
            );
            _requestInfo = CreateRequestInfo(fingerprint: CreateFingerprint());

            A.CallTo(() =>
                    mappingSetProvider.GetOrCreateAsync(
                        A<MappingSetKey>.Ignored,
                        A<CancellationToken>.Ignored
                    )
                )
                .Returns(_expectedMappingSet);

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
        public void It_attaches_mapping_set_to_request_info()
        {
            _requestInfo.MappingSet.Should().BeSameAs(_expectedMappingSet);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Provider_Throws_MappingSetUnavailableException : ResolveMappingSetMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var compiler = CreateFakeCompiler();
            var (middleware, mappingSetProvider) = CreateMiddleware(
                useRelationalBackend: true,
                compiler: compiler
            );
            _requestInfo = CreateRequestInfo(fingerprint: CreateFingerprint());

            A.CallTo(() =>
                    mappingSetProvider.GetOrCreateAsync(
                        A<MappingSetKey>.Ignored,
                        A<CancellationToken>.Ignored
                    )
                )
                .Throws(new MappingSetUnavailableException("Pack required but not found"));

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
        public void It_returns_503()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public void It_does_not_set_mapping_set()
        {
            _requestInfo.MappingSet.Should().BeNull();
        }

        [Test]
        public void It_returns_error_body_with_guidance()
        {
            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("Mapping Set Unavailable");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Provider_Throws_Unexpected_Exception : ResolveMappingSetMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            var compiler = CreateFakeCompiler();
            var (middleware, mappingSetProvider) = CreateMiddleware(
                useRelationalBackend: true,
                compiler: compiler
            );
            _requestInfo = CreateRequestInfo(fingerprint: CreateFingerprint());

            A.CallTo(() =>
                    mappingSetProvider.GetOrCreateAsync(
                        A<MappingSetKey>.Ignored,
                        A<CancellationToken>.Ignored
                    )
                )
                .Throws(new InvalidOperationException("Unexpected internal error"));

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
        public void It_returns_503()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public void It_returns_error_body_with_unexpected_error_guidance()
        {
            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("unexpected error");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Compiler_Registered : ResolveMappingSetMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;

        [SetUp]
        public async Task Setup()
        {
            // No compiler passed — _dialect will be null
            var (middleware, _) = CreateMiddleware(useRelationalBackend: true, compiler: null);
            _requestInfo = CreateRequestInfo(fingerprint: CreateFingerprint());

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
        public void It_returns_503()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public void It_returns_error_body_with_configuration_guidance()
        {
            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            _requestInfo
                .FrontendResponse.Body!.ToString()
                .Should()
                .Contain("No relational backend compiler is registered");
        }
    }
}
