// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
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
/// Shared arrangement: a request whose target fingerprint carries <see cref="DatabaseHash"/>, a
/// deployment at relational mapping version <c>v3</c>, and a faked mapping-set provider.
/// </summary>
public abstract class ResolveEducationOrganizationProjectionMappingSetMiddlewareTests
{
    protected static readonly string DatabaseHash = new('d', 64);

    /// <summary>The message and diagnostic the spec names: neither may reach a body or a log.</summary>
    protected const string HostileMessage = "hostile";
    protected const string HostileDiagnostic = "Server=db;Password=s3cret-hostile";

    protected IMappingSetProvider MappingSetProvider { get; private set; } = null!;

    private protected RecordingLogger<ResolveEducationOrganizationProjectionMappingSetMiddleware> Logger
    {
        get;
        private set;
    } = new();

    private protected RequestInfo RequestInfo { get; private set; } = null!;

    protected bool NextCalled { get; private set; }

    [SetUp]
    public void ResetFakes()
    {
        MappingSetProvider = A.Fake<IMappingSetProvider>();
        Logger = new RecordingLogger<ResolveEducationOrganizationProjectionMappingSetMiddleware>();
    }

    protected async Task Execute(
        SqlDialect dialect = SqlDialect.Pgsql,
        bool withFingerprint = true,
        CancellationToken cancellationToken = default
    )
    {
        FrontendRequest frontendRequest = new(
            Path: "/management/education-organizations",
            Body: null,
            Form: null,
            Headers: [],
            QueryParameters: [],
            TraceId: new TraceId("projection-mapping"),
            RouteQualifiers: [],
            Tenant: "Tenant_255901"
        );

        RequestInfo = new RequestInfo(frontendRequest, RequestMethod.GET, No.ServiceProvider)
        {
            DatabaseFingerprint = withFingerprint
                ? new DatabaseFingerprint("1.0", DatabaseHash, 42, new byte[32].ToImmutableArray())
                : null,
            RequestCancellationToken = cancellationToken,
        };
        NextCalled = false;

        IEffectiveSchemaSetProvider effectiveSchemaSetProvider = A.Fake<IEffectiveSchemaSetProvider>();
        A.CallTo(() => effectiveSchemaSetProvider.EffectiveSchemaSet)
            .Returns(
                new EffectiveSchemaSet(
                    new EffectiveSchemaInfo("1.0", "v3", DatabaseHash, 0, new byte[32], [], []),
                    []
                )
            );

        IRuntimeMappingSetCompiler compiler = A.Fake<IRuntimeMappingSetCompiler>();
        A.CallTo(() => compiler.Dialect).Returns(dialect);

        ResolveEducationOrganizationProjectionMappingSetMiddleware middleware = new(
            MappingSetProvider,
            effectiveSchemaSetProvider,
            [compiler],
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

    internal static MappingSet CreateMappingSet(SqlDialect dialect)
    {
        EffectiveSchemaInfo effectiveSchema = new(
            ApiSchemaFormatVersion: "1.0",
            RelationalMappingVersion: "v3",
            EffectiveSchemaHash: DatabaseHash,
            ResourceKeyCount: 0,
            ResourceKeySeedHash: new byte[32],
            SchemaComponentsInEndpointOrder: [],
            ResourceKeysInIdOrder: []
        );

        return new MappingSet(
            Key: new MappingSetKey(DatabaseHash, dialect, "v3"),
            Model: new DerivedRelationalModelSet(
                EffectiveSchema: effectiveSchema,
                Dialect: dialect,
                ProjectSchemasInEndpointOrder: [],
                ConcreteResourcesInNameOrder: [],
                AbstractIdentityTablesInNameOrder: [],
                AbstractUnionViewsInNameOrder: [],
                IndexesInCreateOrder: [],
                TriggersInCreateOrder: []
            ),
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

    [TestFixture(SqlDialect.Pgsql)]
    [TestFixture(SqlDialect.Mssql)]
    public class Given_The_Mapping_Set_Resolves(SqlDialect dialect)
        : ResolveEducationOrganizationProjectionMappingSetMiddlewareTests
    {
        private MappingSet _mappingSet = null!;

        [SetUp]
        public async Task Setup()
        {
            _mappingSet = CreateMappingSet(dialect);
            A.CallTo(() => MappingSetProvider.GetOrCreateAsync(A<MappingSetKey>._, A<CancellationToken>._))
                .Returns(_mappingSet);
            await Execute(dialect);
        }

        [Test]
        public void It_attaches_the_mapping_set_and_continues()
        {
            NextCalled.Should().BeTrue();
            RequestInfo.MappingSet.Should().BeSameAs(_mappingSet);
            RequestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        }

        [Test]
        public void It_resolves_the_targets_hash_with_the_registered_dialect_and_mapping_version() =>
            A.CallTo(() =>
                    MappingSetProvider.GetOrCreateAsync(
                        new MappingSetKey(DatabaseHash, dialect, "v3"),
                        A<CancellationToken>._
                    )
                )
                .MustHaveHappenedOnceExactly();
    }

    [TestFixture]
    public class Given_The_Mapping_Set_Is_Unavailable_With_Hostile_Diagnostics
        : ResolveEducationOrganizationProjectionMappingSetMiddlewareTests
    {
        [SetUp]
        public async Task Setup()
        {
            A.CallTo(() => MappingSetProvider.GetOrCreateAsync(A<MappingSetKey>._, A<CancellationToken>._))
                .ThrowsAsync(new MappingSetUnavailableException(HostileMessage, [HostileDiagnostic]));
            await Execute();
        }

        [Test]
        public void It_answers_projection_unsupported_with_the_fixed_body()
        {
            NextCalled.Should().BeFalse();
            RequestInfo.MappingSet.Should().BeNull();
            RequestInfo.FrontendResponse.StatusCode.Should().Be(409);
            RequestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");

            JsonNode body = RequestInfo.FrontendResponse.Body!;
            body["type"]!
                .GetValue<string>()
                .Should()
                .Be("urn:ed-fi:api:education-organization-projection:projection-unsupported");
            body["title"]!.GetValue<string>().Should().Be("Projection Unsupported");
            body["detail"]!
                .GetValue<string>()
                .Should()
                .Be("The education organization projection is not supported by the data model in use.");
            body["errors"]!.AsArray().Should().BeEmpty();
        }

        [Test]
        public void It_returns_neither_the_message_nor_the_diagnostic()
        {
            string body = RequestInfo.FrontendResponse.Body!.ToJsonString();
            body.Should().NotContain(HostileMessage);
            body.Should().NotContain("s3cret");
            body.Should().NotContain("Server=");
        }

        [Test]
        public void It_logs_neither_the_message_nor_the_diagnostic_at_any_level()
        {
            // The recording logger is enabled at every level, Trace included.
            Logger.Records.Should().NotBeEmpty();
            Logger.Records.Should().OnlyContain(record => record.Exception == null);
            Logger
                .Records.Should()
                .NotContain(record =>
                    record.Message.Contains(HostileMessage)
                    || record.Message.Contains("s3cret")
                    || record.Properties.Values.Any(value =>
                        value != null
                        && (
                            value.ToString()!.Contains(HostileMessage) || value.ToString()!.Contains("s3cret")
                        )
                    )
                );
        }

        [Test]
        public void It_logs_the_reason() =>
            Logger
                .Records.Should()
                .Contain(record => Equals(record.Properties["Reason"], "MappingSetUnavailable"));
    }

    [TestFixture]
    public class Given_The_Request_Is_Cancelled_While_The_Mapping_Set_Resolves
        : ResolveEducationOrganizationProjectionMappingSetMiddlewareTests
    {
        [Test]
        public async Task It_propagates_the_cancellation_without_a_response()
        {
            using CancellationTokenSource cancellation = new();
            await cancellation.CancelAsync();
            A.CallTo(() => MappingSetProvider.GetOrCreateAsync(A<MappingSetKey>._, A<CancellationToken>._))
                .ThrowsAsync(new OperationCanceledException(cancellation.Token));

            Func<Task> act = () => Execute(cancellationToken: cancellation.Token);

            await act.Should().ThrowAsync<OperationCanceledException>();
            NextCalled.Should().BeFalse();
            RequestInfo.FrontendResponse.Should().BeSameAs(No.FrontendResponse);
        }
    }

    [TestFixture]
    public class Given_No_Fingerprint_Was_Resolved
        : ResolveEducationOrganizationProjectionMappingSetMiddlewareTests
    {
        [Test]
        public async Task It_fails_as_a_composition_defect_without_resolving()
        {
            Func<Task> act = () => Execute(withFingerprint: false);

            await act.Should().ThrowAsync<InvalidOperationException>();
            A.CallTo(() => MappingSetProvider.GetOrCreateAsync(A<MappingSetKey>._, A<CancellationToken>._))
                .MustNotHaveHappened();
        }
    }
}
