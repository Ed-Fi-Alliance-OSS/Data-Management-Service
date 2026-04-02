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
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>(),
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
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
        public void It_does_not_call_next()
        {
            _nextCalled.Should().BeFalse();
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

        [Test]
        public void It_returns_503()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public void It_returns_a_mapping_set_unavailable_response()
        {
            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("Mapping Set Unavailable");
            _requestInfo
                .FrontendResponse.Body!.ToString()
                .Should()
                .Contain("Database fingerprint was not resolved before mapping set resolution");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Fingerprint_Present_And_Provider_Returns_MappingSet : ResolveMappingSetMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private bool _nextCalled;
        private readonly MappingSet _expectedMappingSet = CreateTestMappingSet();

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
    public class Given_Fingerprint_Present_Verifies_Exact_Key_Construction : ResolveMappingSetMiddlewareTests
    {
        private MappingSetKey _capturedKey;
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

            var fingerprint = CreateFingerprint(hash: _testHash);
            var requestInfo = CreateRequestInfo(fingerprint: fingerprint);

            A.CallTo(() =>
                    mappingSetProvider.GetOrCreateAsync(
                        A<MappingSetKey>.Ignored,
                        A<CancellationToken>.Ignored
                    )
                )
                .Invokes((MappingSetKey key, CancellationToken _) => _capturedKey = key)
                .Returns(CreateTestMappingSet());

            await middleware.Execute(requestInfo, () => Task.CompletedTask);
        }

        [Test]
        public void It_uses_fingerprint_effective_schema_hash()
        {
            _capturedKey.EffectiveSchemaHash.Should().Be(_testHash);
        }

        [Test]
        public void It_uses_compiler_dialect()
        {
            _capturedKey.Dialect.Should().Be(SqlDialect.Pgsql);
        }

        [Test]
        public void It_uses_effective_schema_relational_mapping_version()
        {
            _capturedKey.RelationalMappingVersion.Should().Be("v1");
        }

        [Test]
        public void It_calls_provider_exactly_once()
        {
            A.CallTo(() =>
                    _mappingSetProvider.GetOrCreateAsync(
                        A<MappingSetKey>.That.Matches(k =>
                            k.EffectiveSchemaHash == _testHash
                            && k.Dialect == SqlDialect.Pgsql
                            && k.RelationalMappingVersion == "v1"
                        ),
                        A<CancellationToken>.Ignored
                    )
                )
                .MustHaveHappenedOnceExactly();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Next_Throws_After_Mapping_Set_Resolution : ResolveMappingSetMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();
        private Func<Task> _act = null!;

        [SetUp]
        public void Setup()
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
                .Returns(CreateTestMappingSet());

            _act = () => middleware.Execute(_requestInfo, () => throw new InvalidOperationException("boom"));
        }

        [Test]
        public async Task It_propagates_the_downstream_exception()
        {
            await _act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        }

        [Test]
        public async Task It_still_attaches_the_mapping_set_before_next_runs()
        {
            await _act.Should().ThrowAsync<InvalidOperationException>();

            _requestInfo.MappingSet.Should().NotBeNull();
        }

        [Test]
        public async Task It_does_not_translate_the_downstream_exception_into_a_503()
        {
            await _act.Should().ThrowAsync<InvalidOperationException>();

            _requestInfo.FrontendResponse.Should().Be(No.FrontendResponse);
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

    [TestFixture]
    [Parallelizable]
    public class Given_Provider_Throws_With_Diagnostics : ResolveMappingSetMiddlewareTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

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
                .Throws(
                    new MappingSetUnavailableException(
                        "Mapping pack is required but not found.",
                        [
                            $"EffectiveSchemaHash: {_testHash}",
                            "Dialect: Pgsql",
                            "RelationalMappingVersion: v1",
                            "Pack status: required but not found",
                            "Suggested action: Provide a matching .mpack file or set Required=false.",
                        ]
                    )
                );

            await middleware.Execute(_requestInfo, () => Task.CompletedTask);
        }

        [Test]
        public void It_returns_503()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(503);
        }

        [Test]
        public void It_includes_exception_message_as_detail()
        {
            _requestInfo
                .FrontendResponse.Body!.ToString()
                .Should()
                .Contain("Mapping pack is required but not found.");
        }

        [Test]
        public void It_includes_effective_schema_hash_in_errors()
        {
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain(_testHash);
        }

        [Test]
        public void It_includes_dialect_in_errors()
        {
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("Dialect: Pgsql");
        }

        [Test]
        public void It_includes_mapping_version_in_errors()
        {
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("RelationalMappingVersion: v1");
        }

        [Test]
        public void It_includes_pack_status_in_errors()
        {
            _requestInfo.FrontendResponse.Body!.ToString().Should().Contain("required but not found");
        }
    }
}
