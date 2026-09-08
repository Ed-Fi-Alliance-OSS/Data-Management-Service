// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Model;
using EdFi.DataManagementService.Core.Pipeline;
using FakeItEasy;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Polly;
using static EdFi.DataManagementService.Core.Tests.Unit.TestHelper;

namespace EdFi.DataManagementService.Core.Tests.Unit.Handler;

[TestFixture]
[Parallelizable]
public class TrackedChangeQueryRequestHandlerTests
{
    private static readonly PaginationParameters _paginationParameters = new(
        Limit: 25,
        Offset: 10,
        TotalCount: true,
        MaximumPageSize: 500
    );
    private static readonly ChangeVersionRange _changeVersionRange = new(5L, 15L);

    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _descriptorResource = new("Ed-Fi", "GradeLevelDescriptor");

    private sealed class Repository(JsonArray items, long? totalCount) : IChangeQueryRepository
    {
        public ITrackedChangeQueryRequest? CapturedRequest { get; private set; }

        public Task<long> GetNewestChangeVersion(CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<TrackedChangeQueryResult> QueryTrackedChanges(
            ITrackedChangeQueryRequest request,
            CancellationToken cancellationToken = default
        )
        {
            CapturedRequest = request;
            return Task.FromResult(new TrackedChangeQueryResult(items, totalCount));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Change_Query_Repository_Is_Registered : TrackedChangeQueryRequestHandlerTests
    {
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            _requestInfo = CreateRequestInfo(
                serviceProvider: No.ServiceProvider,
                mappingSet: null,
                resourceInfo: CreateResourceInfo(_schoolResource, isDescriptor: false)
            );

            await Execute(_requestInfo);
        }

        [Test]
        public void It_returns_the_not_found_problem_response()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(404);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
            _requestInfo.FrontendResponse.Headers.Should().BeEmpty();
            _requestInfo.FrontendResponse.Body.Should().NotBeNull();
            _requestInfo.FrontendResponse.Body!["detail"]!
                .GetValue<string>()
                .Should()
                .Be("The specified data could not be found.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Relational_Tracked_Change_Query : TrackedChangeQueryRequestHandlerTests
    {
        private readonly JsonArray _items =
        [
            new JsonObject { ["id"] = "11111111-1111-1111-1111-111111111111", ["changeVersion"] = 7L },
        ];

        private Repository _repository = null!;
        private RequestInfo _requestInfo = No.RequestInfo();
        private MappingSet _mappingSet = null!;
        private ConcreteResourceModel _resourceModel = null!;
        private TrackedChangeTableInfo _trackedChangeTable = null!;

        [SetUp]
        public async Task Setup()
        {
            _mappingSet = CreateMappingSet(out _resourceModel, out _trackedChangeTable, out _, out _, out _);
            _repository = new Repository(_items, totalCount: 42L);
            _requestInfo = CreateRequestInfo(
                serviceProvider: CreateServiceProvider(_repository),
                mappingSet: _mappingSet,
                resourceInfo: CreateResourceInfo(_schoolResource, isDescriptor: false)
            );

            await Execute(_requestInfo);
        }

        [Test]
        public void It_returns_success_with_the_repository_items_and_total_count_header()
        {
            _requestInfo.FrontendResponse.StatusCode.Should().Be(200);
            _requestInfo.FrontendResponse.ContentType.Should().Be("application/json");
            _requestInfo.FrontendResponse.Body.Should().BeSameAs(_items);
            _requestInfo.FrontendResponse.Headers.Should().Contain("Total-Count", "42");
        }

        [Test]
        public void It_passes_a_relational_request_with_the_resolved_resource_model_and_tracked_table()
        {
            _repository.CapturedRequest.Should().BeAssignableTo<IRelationalTrackedChangeQueryRequest>();

            var relationalRequest = (IRelationalTrackedChangeQueryRequest)_repository.CapturedRequest!;
            relationalRequest.ResourceInfo.Should().BeSameAs(_requestInfo.ResourceInfo);
            relationalRequest.Operation.Should().Be(ChangeQueryEndpointOperation.Deletes);
            relationalRequest.PaginationParameters.Should().BeSameAs(_paginationParameters);
            relationalRequest.ChangeVersionRange.Should().BeSameAs(_changeVersionRange);
            relationalRequest.TraceId.Should().Be(_requestInfo.FrontendRequest.TraceId);
            relationalRequest.MappingSet.Should().BeSameAs(_mappingSet);
            relationalRequest.ResourceModel.Should().BeSameAs(_resourceModel);
            relationalRequest.TrackedChangeTable.Should().BeSameAs(_trackedChangeTable);
            relationalRequest
                .AuthorizationContext.ClaimEducationOrganizationIds.Should()
                .Equal(255901L, 255902L);
            relationalRequest
                .AuthorizationContext.NamespacePrefixes.Should()
                .Equal("uri://ed-fi.org", "uri://sample.org");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Descriptor_Tracked_Change_Query : TrackedChangeQueryRequestHandlerTests
    {
        private Repository _repository = null!;
        private RequestInfo _requestInfo = No.RequestInfo();
        private ConcreteResourceModel _descriptorResourceModel = null!;
        private TrackedChangeTableInfo _sourceMatchedDescriptorTable = null!;
        private TrackedChangeTableInfo _sharedDescriptorTrackedTable = null!;

        [SetUp]
        public async Task Setup()
        {
            MappingSet mappingSet = CreateMappingSet(
                out _,
                out _,
                out _descriptorResourceModel,
                out _sourceMatchedDescriptorTable,
                out _sharedDescriptorTrackedTable
            );
            _repository = new Repository([], totalCount: null);
            _requestInfo = CreateRequestInfo(
                serviceProvider: CreateServiceProvider(_repository),
                mappingSet: mappingSet,
                resourceInfo: CreateResourceInfo(_descriptorResource, isDescriptor: true)
            );

            await Execute(_requestInfo);
        }

        [Test]
        public void It_resolves_the_shared_descriptor_tracked_change_table()
        {
            _repository.CapturedRequest.Should().BeAssignableTo<IRelationalTrackedChangeQueryRequest>();

            var relationalRequest = (IRelationalTrackedChangeQueryRequest)_repository.CapturedRequest!;
            relationalRequest.ResourceModel.Should().BeSameAs(_descriptorResourceModel);
            relationalRequest.TrackedChangeTable.Should().BeSameAs(_sharedDescriptorTrackedTable);
            relationalRequest.TrackedChangeTable.Should().NotBeSameAs(_sourceMatchedDescriptorTable);
            relationalRequest.TrackedChangeTable.Kind.Should().Be(TrackedChangeTableKind.SharedDescriptor);
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Request_Level_Authorization_Failure : TrackedChangeQueryRequestHandlerTests
    {
        [Test]
        public async Task It_maps_security_configuration_failure_to_500()
        {
            var repository = A.Fake<IChangeQueryRepository>();
            A.CallTo(() =>
                    repository.QueryTrackedChanges(A<ITrackedChangeQueryRequest>._, A<CancellationToken>._)
                )
                .Returns(
                    new TrackedChangeQueryResult(
                        [],
                        null,
                        new ChangeQueryAuthorizationFailure.SecurityConfiguration(["OwnershipBased"])
                    )
                );

            RequestInfo requestInfo = CreateTrackedChangeRequestInfo(repository);

            await Execute(requestInfo);

            requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            requestInfo
                .FrontendResponse.Body!.ToJsonString()
                .Should()
                .Contain("Could not find authorization strategy implementations");
        }

        [Test]
        public async Task It_maps_security_configuration_failure_errors_to_500()
        {
            var repository = A.Fake<IChangeQueryRepository>();
            A.CallTo(() =>
                    repository.QueryTrackedChanges(A<ITrackedChangeQueryRequest>._, A<CancellationToken>._)
                )
                .Returns(
                    new TrackedChangeQueryResult(
                        [],
                        null,
                        new ChangeQueryAuthorizationFailure.SecurityConfiguration(
                            [],
                            ["The planned ReadChanges command exceeds the SQL Server parameter limit."]
                        )
                    )
                );

            RequestInfo requestInfo = CreateTrackedChangeRequestInfo(repository);

            await Execute(requestInfo);

            requestInfo.FrontendResponse.StatusCode.Should().Be(500);
            string responseBody = requestInfo.FrontendResponse.Body!.ToJsonString();
            responseBody
                .Should()
                .Contain("The planned ReadChanges command exceeds the SQL Server parameter limit.");
            responseBody.Should().NotContain("Could not find authorization strategy implementations");
        }

        [Test]
        public async Task It_maps_no_prefixes_failure_to_403()
        {
            var repository = A.Fake<IChangeQueryRepository>();
            A.CallTo(() =>
                    repository.QueryTrackedChanges(A<ITrackedChangeQueryRequest>._, A<CancellationToken>._)
                )
                .Returns(
                    new TrackedChangeQueryResult(
                        [],
                        null,
                        new ChangeQueryAuthorizationFailure.NamespaceNoPrefixesConfigured("NamespaceBased")
                    )
                );

            RequestInfo requestInfo = CreateTrackedChangeRequestInfo(repository);

            await Execute(requestInfo);

            requestInfo.FrontendResponse.StatusCode.Should().Be(403);
        }

        private static RequestInfo CreateTrackedChangeRequestInfo(IChangeQueryRepository repository) =>
            CreateRequestInfo(
                serviceProvider: CreateServiceProvider(repository),
                mappingSet: CreateMappingSet(out _, out _, out _, out _, out _),
                resourceInfo: CreateResourceInfo(_schoolResource, isDescriptor: false)
            );
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Resource_That_Is_Not_In_The_Mapping_Set : TrackedChangeQueryRequestHandlerTests
    {
        private Repository _repository = null!;
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            MappingSet mappingSet = CreateMappingSet(
                out _,
                out _,
                out _,
                out _,
                out _,
                includeSchoolResourceModel: false
            );
            _repository = new Repository([], totalCount: null);
            _requestInfo = CreateRequestInfo(
                serviceProvider: CreateServiceProvider(_repository),
                mappingSet: mappingSet,
                resourceInfo: CreateResourceInfo(_schoolResource, isDescriptor: false)
            );

            await Execute(_requestInfo);
        }

        [Test]
        public void It_returns_the_not_found_problem_response()
        {
            AssertNotFound(_requestInfo);
            _repository.CapturedRequest.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Resource_Without_A_Tracked_Change_Table : TrackedChangeQueryRequestHandlerTests
    {
        private Repository _repository = null!;
        private RequestInfo _requestInfo = No.RequestInfo();

        [SetUp]
        public async Task Setup()
        {
            MappingSet mappingSet = CreateMappingSet(
                out _,
                out _,
                out _,
                out _,
                out _,
                includeSchoolTrackedChangeTable: false
            );
            _repository = new Repository([], totalCount: null);
            _requestInfo = CreateRequestInfo(
                serviceProvider: CreateServiceProvider(_repository),
                mappingSet: mappingSet,
                resourceInfo: CreateResourceInfo(_schoolResource, isDescriptor: false)
            );

            await Execute(_requestInfo);
        }

        [Test]
        public void It_returns_the_not_found_problem_response()
        {
            AssertNotFound(_requestInfo);
            _repository.CapturedRequest.Should().BeNull();
        }
    }

    private static Task Execute(RequestInfo requestInfo)
    {
        var handler = new EdFi.DataManagementService.Core.Handler.TrackedChangeQueryRequestHandler(
            A.Fake<ILogger>(),
            ResiliencePipeline.Empty
        );
        return handler.Execute(requestInfo, NullNext);
    }

    private static IServiceProvider CreateServiceProvider(IChangeQueryRepository repository)
    {
        var serviceProvider = A.Fake<IServiceProvider>();
        A.CallTo(() => serviceProvider.GetService(typeof(IChangeQueryRepository))).Returns(repository);
        return serviceProvider;
    }

    private static void AssertNotFound(RequestInfo requestInfo)
    {
        requestInfo.FrontendResponse.StatusCode.Should().Be(404);
        requestInfo.FrontendResponse.ContentType.Should().Be("application/problem+json");
        requestInfo.FrontendResponse.Headers.Should().BeEmpty();
        requestInfo.FrontendResponse.Body.Should().NotBeNull();
        requestInfo.FrontendResponse.Body!["detail"]!
            .GetValue<string>()
            .Should()
            .Be("The specified data could not be found.");
    }

    private static RequestInfo CreateRequestInfo(
        IServiceProvider serviceProvider,
        MappingSet? mappingSet,
        ResourceInfo resourceInfo
    )
    {
        RequestInfo requestInfo = No.RequestInfo("tracked-change-handler", serviceProvider);
        requestInfo.ChangeQueryOperation = ChangeQueryEndpointOperation.Deletes;
        requestInfo.MappingSet = mappingSet;
        requestInfo.ResourceInfo = resourceInfo;
        requestInfo.PaginationParameters = _paginationParameters;
        requestInfo.ChangeVersionRange = _changeVersionRange;
        requestInfo.ClientAuthorizations = new ClientAuthorizations(
            TokenId: "token-id",
            ClientId: "client-id",
            ClaimSetName: "claim-set",
            EducationOrganizationIds:
            [
                new EducationOrganizationId(255902L),
                new EducationOrganizationId(255901L),
                new EducationOrganizationId(255902L),
            ],
            NamespacePrefixes:
            [
                new NamespacePrefix("uri://sample.org"),
                new NamespacePrefix("uri://ed-fi.org"),
                new NamespacePrefix("uri://sample.org"),
            ],
            DataStoreIds: []
        );
        return requestInfo;
    }

    private static ResourceInfo CreateResourceInfo(QualifiedResourceName resource, bool isDescriptor) =>
        new(
            ProjectName: new ProjectName(resource.ProjectName),
            ResourceName: new ResourceName(resource.ResourceName),
            IsDescriptor: isDescriptor,
            ResourceVersion: new SemVer("1.0.0"),
            AllowIdentityUpdates: false
        );

    private static MappingSet CreateMappingSet(
        out ConcreteResourceModel schoolResourceModel,
        out TrackedChangeTableInfo schoolTrackedChangeTable,
        out ConcreteResourceModel descriptorResourceModel,
        out TrackedChangeTableInfo sourceMatchedDescriptorTable,
        out TrackedChangeTableInfo sharedDescriptorTrackedTable,
        bool includeSchoolResourceModel = true,
        bool includeSchoolTrackedChangeTable = true
    )
    {
        ResourceKeyEntry schoolResourceKey = new(
            ResourceKeyId: 1,
            Resource: _schoolResource,
            ResourceVersion: "1.0.0",
            IsAbstractResource: false
        );
        ResourceKeyEntry descriptorResourceKey = new(
            ResourceKeyId: 2,
            Resource: _descriptorResource,
            ResourceVersion: "1.0.0",
            IsAbstractResource: false
        );

        schoolResourceModel = CreateResourceModel(
            schoolResourceKey,
            ResourceStorageKind.RelationalTables,
            new DbTableName(new DbSchemaName("edfi"), "School")
        );
        descriptorResourceModel = CreateResourceModel(
            descriptorResourceKey,
            ResourceStorageKind.SharedDescriptorTable,
            new DbTableName(new DbSchemaName("dms"), "Descriptor")
        );

        schoolTrackedChangeTable = CreateTrackedChangeTable(
            new DbTableName(new DbSchemaName("tracked_changes_edfi"), "School"),
            TrackedChangeTableKind.Resource,
            schoolResourceModel.RelationalModel.Root.Table
        );
        sourceMatchedDescriptorTable = CreateTrackedChangeTable(
            new DbTableName(new DbSchemaName("tracked_changes_edfi"), "DescriptorBySourceOnly"),
            TrackedChangeTableKind.Resource,
            descriptorResourceModel.RelationalModel.Root.Table
        );
        sharedDescriptorTrackedTable = CreateTrackedChangeTable(
            new DbTableName(new DbSchemaName("tracked_changes_edfi"), "Descriptor"),
            TrackedChangeTableKind.SharedDescriptor,
            descriptorResourceModel.RelationalModel.Root.Table
        );

        List<ConcreteResourceModel> concreteResources = [descriptorResourceModel];
        if (includeSchoolResourceModel)
        {
            concreteResources.Insert(0, schoolResourceModel);
        }

        List<TrackedChangeTableInfo> trackedChangeTables =
        [
            sourceMatchedDescriptorTable,
            sharedDescriptorTrackedTable,
        ];
        if (includeSchoolTrackedChangeTable)
        {
            trackedChangeTables.Insert(0, schoolTrackedChangeTable);
        }

        DerivedRelationalModelSet modelSet = new(
            EffectiveSchema: new EffectiveSchemaInfo(
                ApiSchemaFormatVersion: "1.0",
                RelationalMappingVersion: "v1",
                EffectiveSchemaHash: "tracked-change-handler",
                ResourceKeyCount: 2,
                ResourceKeySeedHash: [1, 2, 3],
                SchemaComponentsInEndpointOrder:
                [
                    new SchemaComponentInfo("ed-fi", "Ed-Fi", "1.0.0", false, "component-hash"),
                ],
                ResourceKeysInIdOrder: [schoolResourceKey, descriptorResourceKey]
            ),
            Dialect: SqlDialect.Pgsql,
            ProjectSchemasInEndpointOrder:
            [
                new ProjectSchemaInfo("ed-fi", "Ed-Fi", "1.0.0", false, new DbSchemaName("edfi")),
            ],
            ConcreteResourcesInNameOrder: concreteResources,
            AbstractIdentityTablesInNameOrder: [],
            AbstractUnionViewsInNameOrder: [],
            IndexesInCreateOrder: [],
            TriggersInCreateOrder: [],
            TrackedChangeTablesInNameOrder: trackedChangeTables
        );

        return new MappingSet(
            Key: new MappingSetKey("tracked-change-handler", SqlDialect.Pgsql, "v1"),
            Model: modelSet,
            WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>(),
            ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
            ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short>
            {
                [_schoolResource] = schoolResourceKey.ResourceKeyId,
                [_descriptorResource] = descriptorResourceKey.ResourceKeyId,
            },
            ResourceKeyById: new Dictionary<short, ResourceKeyEntry>
            {
                [schoolResourceKey.ResourceKeyId] = schoolResourceKey,
                [descriptorResourceKey.ResourceKeyId] = descriptorResourceKey,
            },
            SecurableElementColumnPathsByResource: new Dictionary<
                QualifiedResourceName,
                IReadOnlyList<ResolvedSecurableElementPath>
            >()
        );
    }

    private static ConcreteResourceModel CreateResourceModel(
        ResourceKeyEntry resourceKey,
        ResourceStorageKind storageKind,
        DbTableName rootTable
    )
    {
        DbTableModel tableModel = new(
            Table: rootTable,
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: $"PK_{rootTable.Name}",
                Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            Columns:
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: new RelationalScalarType(ScalarKind.Int64),
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null
                ),
            ],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [new DbColumnName("DocumentId")],
                RootScopeLocatorColumns: [new DbColumnName("DocumentId")],
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };

        RelationalResourceModel relationalModel = new(
            Resource: resourceKey.Resource,
            PhysicalSchema: rootTable.Schema,
            StorageKind: storageKind,
            Root: tableModel,
            TablesInDependencyOrder: [tableModel],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );

        DescriptorMetadata? descriptorMetadata =
            storageKind is ResourceStorageKind.SharedDescriptorTable
                ? new DescriptorMetadata(
                    new DescriptorColumnContract(
                        Namespace: new DbColumnName("Namespace"),
                        CodeValue: new DbColumnName("CodeValue"),
                        ShortDescription: null,
                        Description: null,
                        EffectiveBeginDate: null,
                        EffectiveEndDate: null,
                        Discriminator: new DbColumnName("Discriminator")
                    ),
                    DiscriminatorStrategy.DescriptorColumn
                )
                : null;

        return new ConcreteResourceModel(resourceKey, storageKind, relationalModel, descriptorMetadata);
    }

    private static TrackedChangeTableInfo CreateTrackedChangeTable(
        DbTableName trackedTable,
        TrackedChangeTableKind kind,
        DbTableName sourceTable
    )
    {
        List<TrackedChangeSystemColumnInfo> systemColumns =
        [
            new(
                Role: TrackedChangeSystemColumnRole.Id,
                ColumnName: new DbColumnName("Id"),
                ScalarType: null,
                IsNullable: false,
                IsPrimaryKey: false
            ),
            new(
                Role: TrackedChangeSystemColumnRole.ChangeVersion,
                ColumnName: new DbColumnName("ChangeVersion"),
                ScalarType: new RelationalScalarType(ScalarKind.Int64),
                IsNullable: false,
                IsPrimaryKey: true
            ),
            new(
                Role: TrackedChangeSystemColumnRole.CreatedAt,
                ColumnName: new DbColumnName("CreatedAt"),
                ScalarType: null,
                IsNullable: false,
                IsPrimaryKey: false
            ),
        ];

        if (kind is TrackedChangeTableKind.SharedDescriptor)
        {
            systemColumns.Add(
                new TrackedChangeSystemColumnInfo(
                    Role: TrackedChangeSystemColumnRole.Discriminator,
                    ColumnName: new DbColumnName("Discriminator"),
                    ScalarType: new RelationalScalarType(ScalarKind.String, MaxLength: 128),
                    IsNullable: false,
                    IsPrimaryKey: false
                )
            );
        }

        return new TrackedChangeTableInfo(
            Table: trackedTable,
            Kind: kind,
            SourceTable: sourceTable,
            ValueColumnsInTableOrder: [],
            SystemColumns: systemColumns,
            PrimaryKeyColumns: [new DbColumnName("ChangeVersion")],
            DescriptorJoins: [],
            PersonJoins: []
        );
    }
}
