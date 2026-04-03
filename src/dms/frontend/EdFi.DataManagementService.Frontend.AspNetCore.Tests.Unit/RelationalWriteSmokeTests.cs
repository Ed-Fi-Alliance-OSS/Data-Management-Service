// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Interface;
using EdFi.DataManagementService.Core.External.Model;
using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using EdFi.DataManagementService.Core.Startup;
using FakeItEasy;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Frontend.AspNetCore.Tests.Unit;

[TestFixture]
[NonParallelizable]
public class Given_A_Host_Using_The_Relational_Backend
{
    private const string MinimalApiSchemaJson = """
        {
          "apiSchemaVersion": "1.0.0",
          "projectSchema": {
            "projectName": "TestProject",
            "projectVersion": "1.0.0",
            "projectEndpointName": "testproject",
            "isExtensionProject": false,
            "description": "Minimal relational smoke schema",
            "abstractResources": {},
            "resourceNameMapping": {
              "Widget": "widgets"
            },
            "caseInsensitiveEndpointNameMapping": {
              "widgets": "widgets"
            },
            "educationOrganizationHierarchy": {},
            "educationOrganizationTypes": [],
            "domains": [],
            "resourceSchemas": {
              "widgets": {
                "resourceName": "Widget",
                "allowIdentityUpdates": false,
                "isDescriptor": false,
                "isSchoolYearEnumeration": false,
                "isSubclass": false,
                "isResourceExtension": false,
                "booleanJsonPaths": [],
                "numericJsonPaths": [],
                "dateJsonPaths": [],
                "dateTimeJsonPaths": [
                  "$.submittedAt"
                ],
                "identityJsonPaths": [
                  "$.widgetId"
                ],
                "documentPathsMapping": {
                  "WidgetId": {
                    "isPartOfIdentity": true,
                    "isReference": false,
                    "isRequired": true,
                    "path": "$.widgetId",
                    "type": "integer"
                  },
                  "WidgetName": {
                    "isPartOfIdentity": false,
                    "isReference": false,
                    "isRequired": true,
                    "path": "$.widgetName",
                    "type": "string"
                  },
                  "SubmittedAt": {
                    "isPartOfIdentity": false,
                    "isReference": false,
                    "isRequired": false,
                    "path": "$.submittedAt",
                    "type": "string"
                  },
                  "WidgetCount": {
                    "isPartOfIdentity": false,
                    "isReference": false,
                    "isRequired": false,
                    "path": "$.widgetCount",
                    "type": "string"
                  }
                },
                "securableElements": {
                  "Namespace": [],
                  "EducationOrganization": [],
                  "Student": [],
                  "Contact": [],
                  "Staff": []
                },
                "authorizationPathways": [],
                "decimalPropertyValidationInfos": [],
                "equalityConstraints": [],
                "arrayUniquenessConstraints": [],
                "jsonSchemaForInsert": {
                  "$schema": "https://json-schema.org/draft/2020-12/schema",
                  "type": "object",
                  "properties": {
                    "widgetId": {
                      "type": "integer"
                    },
                    "widgetName": {
                      "type": "string",
                      "maxLength": 75
                    },
                    "submittedAt": {
                      "type": "string",
                      "format": "date-time"
                    },
                    "widgetCount": {
                      "type": "string"
                    }
                  },
                  "required": [
                    "widgetId",
                    "widgetName"
                  ],
                  "additionalProperties": false
                }
              }
            }
          }
        }
        """;

    private CapturingRelationalWriteTerminalStage _terminalStage = null!;
    private CapturingRelationalWriteFlattener _flattener = null!;
    private WebApplicationFactory<Program> _factory = null!;
    private string _schemaDirectory = null!;
    private string _startupStatusFilePath = null!;

    [SetUp]
    public void Setup()
    {
        _schemaDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_schemaDirectory);
        File.WriteAllText(Path.Combine(_schemaDirectory, "ApiSchema.json"), MinimalApiSchemaJson);
        _startupStatusFilePath = Path.Combine(Path.GetTempPath(), "relational-write-smoke-status.json");
        if (File.Exists(_startupStatusFilePath))
        {
            File.Delete(_startupStatusFilePath);
        }

        _terminalStage = new CapturingRelationalWriteTerminalStage();
        _flattener = new CapturingRelationalWriteFlattener();
        _factory = CreateFactory(
            _flattener,
            new WidgetMappingSetProvider(RelationalWriteSmokeSupport.CreateWidgetMappingSet),
            _terminalStage
        );
    }

    [TearDown]
    public void TearDown()
    {
        _factory.Dispose();

        if (Directory.Exists(_schemaDirectory))
        {
            Directory.Delete(_schemaDirectory, recursive: true);
        }
    }

    [Test]
    public async Task It_routes_http_post_requests_into_the_relational_write_seam()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "smoke-token");

        using var response = await client.PostAsync(
            "/data/testproject/widgets",
            new StringContent(
                """{"widgetId":101,"widgetName":"Smoke Widget"}""",
                Encoding.UTF8,
                "application/json"
            )
        );
        var responseBody = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, responseBody);
        response.Headers.Location.Should().NotBeNull();
        response.Headers.Location!.AbsolutePath.Should().StartWith("/data/testproject/widgets/");
        _flattener.Inputs.Should().ContainSingle();
        _terminalStage.Requests.Should().ContainSingle();
        _terminalStage
            .Requests[0]
            .FlatteningInput.WritePlan.Model.Resource.Should()
            .Be(new QualifiedResourceName("TestProject", "Widget"));
        _terminalStage.Requests[0].FlatteningInput.SelectedBody["widgetName"]!
            .GetValue<string>()
            .Should()
            .Be("Smoke Widget");
    }

    // The public HTTP path still relies on Core normalization; backend-local strict parsing only applies
    // when a caller bypasses that middleware and supplies an unnormalized selected body directly.
    [Test]
    public async Task It_normalizes_permissive_datetime_input_on_the_public_http_path_before_the_relational_flattener_sees_the_selected_body()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "smoke-token");

        using var response = await client.PostAsync(
            "/data/testproject/widgets",
            new StringContent(
                """{"widgetId":102,"widgetName":"Normalized Widget","submittedAt":"5/7/2009 2:15:30 PM"}""",
                Encoding.UTF8,
                "application/json"
            )
        );
        var responseBody = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.Created, responseBody);
        _flattener.Inputs.Should().ContainSingle();
        _terminalStage.Requests.Should().ContainSingle();
        _flattener.Inputs[0].SelectedBody["submittedAt"]!
            .GetValue<string>()
            .Should()
            .Be("2009-05-07T14:15:30Z");
        _terminalStage.Requests[0].FlatteningInput.SelectedBody["submittedAt"]!
            .GetValue<string>()
            .Should()
            .Be("2009-05-07T14:15:30Z");
    }

    [Test]
    public async Task It_returns_bad_request_when_the_real_relational_flattener_rejects_an_invalid_scalar_value()
    {
        var terminalStage = new CapturingRelationalWriteTerminalStage();

        using var factory = CreateFactory(
            new RelationalWriteFlattener(),
            new WidgetMappingSetProvider(RelationalWriteSmokeSupport.CreateWidgetCountValidationMappingSet),
            terminalStage
        );
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "smoke-token");

        using var response = await client.PostAsync(
            "/data/testproject/widgets",
            new StringContent(
                """{"widgetId":101,"widgetName":"Smoke Widget","widgetCount":"not-an-integer"}""",
                Encoding.UTF8,
                "application/json"
            )
        );
        var responseBody = await response.Content.ReadAsStringAsync();
        var body = JsonNode.Parse(responseBody)!.AsObject();

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, responseBody);
        body["detail"]!
            .GetValue<string>()
            .Should()
            .Be("Data validation failed. See 'validationErrors' for details.");
        body["validationErrors"]!["$.widgetCount"]![0]!
            .GetValue<string>()
            .Should()
            .Contain("Column 'WidgetCount' on table 'testproject.Widget' expected scalar kind 'Int32'");
        terminalStage.Requests.Should().BeEmpty();
    }

    private WebApplicationFactory<Program> CreateFactory(
        IRelationalWriteFlattener flattener,
        IMappingSetProvider mappingSetProvider,
        CapturingRelationalWriteTerminalStage terminalStage
    )
    {
        var claimSetProvider = new AllowAllWidgetClaimSetProvider();

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration(
                (_, configuration) =>
                {
                    configuration.AddInMemoryCollection(
                        new Dictionary<string, string?>
                        {
                            ["AppSettings:UseRelationalBackend"] = "true",
                            ["AppSettings:UseApiSchemaPath"] = "true",
                            ["AppSettings:ApiSchemaPath"] = _schemaDirectory,
                            ["AppSettings:StartupStatusFilePath"] = _startupStatusFilePath,
                        }
                    );
                }
            );
            builder.ConfigureServices(services =>
            {
                TestMockHelper.AddEssentialMocks(services);

                var jwtValidationService = A.Fake<IJwtValidationService>();
                A.CallTo(() =>
                        jwtValidationService.ValidateAndExtractClientAuthorizationsAsync(
                            A<string>._,
                            A<CancellationToken>._
                        )
                    )
                    .Returns(
                        Task.FromResult(
                            (
                                (ClaimsPrincipal?)
                                    new ClaimsPrincipal(
                                        new ClaimsIdentity([new Claim("client_id", "smoke-client")], "test")
                                    ),
                                (ClientAuthorizations?)
                                    new ClientAuthorizations(
                                        TokenId: "smoke-token",
                                        ClientId: "smoke-client",
                                        ClaimSetName: "SIS-Vendor",
                                        EducationOrganizationIds: [],
                                        NamespacePrefixes: [],
                                        DmsInstanceIds: [new DmsInstanceId(1)]
                                    )
                            )
                        )
                    );

                var applicationContextProvider = A.Fake<IApplicationContextProvider>();
                A.CallTo(() => applicationContextProvider.GetApplicationByClientIdAsync(A<string>._))
                    .Returns(Task.FromResult<ApplicationContext?>(null));
                A.CallTo(() => applicationContextProvider.ReloadApplicationByClientIdAsync(A<string>._))
                    .Returns(Task.FromResult<ApplicationContext?>(null));

                var resourceKeyValidator = A.Fake<IResourceKeyValidator>();
                A.CallTo(() =>
                        resourceKeyValidator.ValidateAsync(
                            A<DatabaseFingerprint>._,
                            A<short>._,
                            A<ImmutableArray<byte>>._,
                            A<IReadOnlyList<ResourceKeyRow>>._,
                            A<string>._,
                            A<CancellationToken>._
                        )
                    )
                    .Returns(new ResourceKeyValidationResult.ValidationSuccess());

                var targetContextResolver = A.Fake<IRelationalWriteTargetContextResolver>();
                A.CallTo(() =>
                        targetContextResolver.ResolveForPostAsync(
                            A<MappingSet>._,
                            A<QualifiedResourceName>._,
                            A<ReferentialId>._,
                            A<DocumentUuid>._,
                            A<CancellationToken>._
                        )
                    )
                    .ReturnsLazily(call =>
                        Task.FromResult<RelationalWriteTargetContext>(
                            new RelationalWriteTargetContext.CreateNew(call.GetArgument<DocumentUuid>(3))
                        )
                    );
                A.CallTo(() =>
                        targetContextResolver.ResolveForPutAsync(
                            A<MappingSet>._,
                            A<QualifiedResourceName>._,
                            A<DocumentUuid>._,
                            A<CancellationToken>._
                        )
                    )
                    .ReturnsLazily(call =>
                        Task.FromResult<RelationalWriteTargetContext>(
                            new RelationalWriteTargetContext.ExistingDocument(
                                345L,
                                call.GetArgument<DocumentUuid>(2)
                            )
                        )
                    );

                var referenceResolver = A.Fake<IReferenceResolver>();
                A.CallTo(() =>
                        referenceResolver.ResolveAsync(A<ReferenceResolverRequest>._, A<CancellationToken>._)
                    )
                    .Returns(RelationalWriteSmokeSupport.CreateEmptyResolvedReferences());

                services.RemoveAll<IJwtValidationService>();
                services.RemoveAll<IClaimSetProvider>();
                services.RemoveAll<IApplicationContextProvider>();
                services.RemoveAll<IDocumentStoreRepository>();
                services.RemoveAll<IDatabaseFingerprintReader>();
                services.RemoveAll<IResourceKeyValidator>();
                services.RemoveAll<IMappingSetProvider>();
                services.RemoveAll<IRelationalWriteTargetContextResolver>();
                services.RemoveAll<IReferenceResolver>();
                services.RemoveAll<IRelationalWriteFlattener>();
                services.RemoveAll<IRelationalWriteTerminalStage>();

                services.AddSingleton(jwtValidationService);
                services.AddSingleton<IClaimSetProvider>(claimSetProvider);
                services.AddSingleton(applicationContextProvider);
                services.AddScoped<IDocumentStoreRepository, RelationalDocumentStoreRepository>();
                services.AddSingleton<IDatabaseFingerprintReader, EffectiveSchemaFingerprintReader>();
                services.AddSingleton(resourceKeyValidator);
                services.AddSingleton(mappingSetProvider);
                services.AddSingleton(targetContextResolver);
                services.AddSingleton(referenceResolver);
                services.AddSingleton(flattener);
                services.AddSingleton<IRelationalWriteTerminalStage>(terminalStage);
                services.AddSingleton<IDescriptorWriteHandler>(new DefaultDescriptorWriteHandler());
            });
        });
    }

    private sealed class EffectiveSchemaFingerprintReader(
        IEffectiveSchemaSetProvider effectiveSchemaSetProvider
    ) : IDatabaseFingerprintReader
    {
        public Task<DatabaseFingerprint?> ReadFingerprintAsync(string connectionString)
        {
            var effectiveSchema = effectiveSchemaSetProvider.EffectiveSchemaSet.EffectiveSchema;

            return Task.FromResult<DatabaseFingerprint?>(
                new DatabaseFingerprint(
                    effectiveSchema.ApiSchemaFormatVersion,
                    effectiveSchema.EffectiveSchemaHash,
                    effectiveSchema.ResourceKeyCount,
                    [.. effectiveSchema.ResourceKeySeedHash]
                )
            );
        }
    }

    private sealed class WidgetMappingSetProvider : IMappingSetProvider
    {
        private readonly Func<MappingSetKey, MappingSet> _mappingSetFactory;

        public WidgetMappingSetProvider(Func<MappingSetKey, MappingSet> mappingSetFactory)
        {
            _mappingSetFactory =
                mappingSetFactory ?? throw new ArgumentNullException(nameof(mappingSetFactory));
        }

        public Task<MappingSet> GetOrCreateAsync(MappingSetKey key, CancellationToken cancellationToken)
        {
            return Task.FromResult(_mappingSetFactory(key));
        }
    }

    private sealed class CapturingRelationalWriteFlattener : IRelationalWriteFlattener
    {
        public List<FlatteningInput> Inputs { get; } = [];

        public FlattenedWriteSet Flatten(FlatteningInput flatteningInput)
        {
            Inputs.Add(flatteningInput);

            var rootPlan = flatteningInput.WritePlan.TablePlansInDependencyOrder.Single(plan =>
                plan.TableModel.IdentityMetadata.TableKind == DbTableKind.Root
            );

            return new FlattenedWriteSet(
                new RootWriteRowBuffer(rootPlan, [FlattenedWriteValue.UnresolvedRootDocumentId.Instance])
            );
        }
    }

    private sealed class CapturingRelationalWriteTerminalStage : IRelationalWriteTerminalStage
    {
        public List<RelationalWriteTerminalStageRequest> Requests { get; } = [];

        public Task<RelationalWriteTerminalStageResult> ExecuteAsync(
            RelationalWriteTerminalStageRequest request,
            CancellationToken cancellationToken = default
        )
        {
            Requests.Add(request);

            return Task.FromResult<RelationalWriteTerminalStageResult>(
                new RelationalWriteTerminalStageResult.Upsert(
                    new UpsertResult.InsertSuccess(
                        (
                            (RelationalWriteTargetContext.CreateNew)request.FlatteningInput.TargetContext
                        ).DocumentUuid
                    )
                )
            );
        }
    }

    private sealed class AllowAllWidgetClaimSetProvider : IClaimSetProvider
    {
        public Task<IList<ClaimSet>> GetAllClaimSets(string? tenant = null)
        {
            return Task.FromResult<IList<ClaimSet>>([
                new ClaimSet(
                    Name: "SIS-Vendor",
                    ResourceClaims:
                    [
                        new ResourceClaim(
                            Name: $"{Conventions.EdFiOdsResourceClaimBaseUri}/testproject/widget",
                            Action: "Create",
                            AuthorizationStrategies:
                            [
                                new AuthorizationStrategy(
                                    AuthorizationStrategyNameConstants.NoFurtherAuthorizationRequired
                                ),
                            ]
                        ),
                    ]
                ),
            ]);
        }
    }

    private static class RelationalWriteSmokeSupport
    {
        public static ResolvedReferenceSet CreateEmptyResolvedReferences()
        {
            return new ResolvedReferenceSet(
                SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
                SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
                LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
                InvalidDocumentReferences: [],
                InvalidDescriptorReferences: [],
                DocumentReferenceOccurrences: [],
                DescriptorReferenceOccurrences: []
            );
        }

        public static MappingSet CreateWidgetMappingSet(MappingSetKey key)
        {
            var resource = new QualifiedResourceName("TestProject", "Widget");
            var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);
            var rootPlan = CreateRootPlan();
            var resourceModel = CreateWidgetResourceModel(resource, rootPlan);

            return CreateMappingSet(key, resource, resourceKey, resourceModel, rootPlan);
        }

        public static MappingSet CreateWidgetCountValidationMappingSet(MappingSetKey key)
        {
            var resource = new QualifiedResourceName("TestProject", "Widget");
            var resourceKey = new ResourceKeyEntry(1, resource, "1.0.0", false);
            var rootPlan = CreateRootPlan(
                additionalColumn: new DbColumnModel(
                    ColumnName: new DbColumnName("WidgetCount"),
                    Kind: ColumnKind.Scalar,
                    ScalarType: new RelationalScalarType(ScalarKind.Int32),
                    IsNullable: true,
                    SourceJsonPath: CreatePath("$.widgetCount", new JsonPathSegment.Property("widgetCount")),
                    TargetResource: null
                ),
                additionalValueSource: new WriteValueSource.Scalar(
                    CreatePath("$.widgetCount", new JsonPathSegment.Property("widgetCount")),
                    new RelationalScalarType(ScalarKind.Int32)
                ),
                additionalParameterName: "WidgetCount"
            );
            var resourceModel = CreateWidgetResourceModel(resource, rootPlan);

            return CreateMappingSet(key, resource, resourceKey, resourceModel, rootPlan);
        }

        private static RelationalResourceModel CreateWidgetResourceModel(
            QualifiedResourceName resource,
            TableWritePlan rootPlan
        )
        {
            return new RelationalResourceModel(
                Resource: resource,
                PhysicalSchema: new DbSchemaName("testproject"),
                StorageKind: ResourceStorageKind.RelationalTables,
                Root: rootPlan.TableModel,
                TablesInDependencyOrder: [rootPlan.TableModel],
                DocumentReferenceBindings: [],
                DescriptorEdgeSources: []
            );
        }

        private static MappingSet CreateMappingSet(
            MappingSetKey key,
            QualifiedResourceName resource,
            ResourceKeyEntry resourceKey,
            RelationalResourceModel resourceModel,
            TableWritePlan rootPlan
        )
        {
            return new MappingSet(
                Key: key,
                Model: new DerivedRelationalModelSet(
                    EffectiveSchema: new EffectiveSchemaInfo(
                        ApiSchemaFormatVersion: "1.0.0",
                        RelationalMappingVersion: key.RelationalMappingVersion,
                        EffectiveSchemaHash: key.EffectiveSchemaHash,
                        ResourceKeyCount: 1,
                        ResourceKeySeedHash: [1, 2, 3],
                        SchemaComponentsInEndpointOrder:
                        [
                            new SchemaComponentInfo(
                                "testproject",
                                "TestProject",
                                "1.0.0",
                                false,
                                "component-hash"
                            ),
                        ],
                        ResourceKeysInIdOrder: [resourceKey]
                    ),
                    Dialect: key.Dialect,
                    ProjectSchemasInEndpointOrder:
                    [
                        new ProjectSchemaInfo(
                            "testproject",
                            "TestProject",
                            "1.0.0",
                            false,
                            new DbSchemaName("testproject")
                        ),
                    ],
                    ConcreteResourcesInNameOrder:
                    [
                        new ConcreteResourceModel(
                            resourceKey,
                            ResourceStorageKind.RelationalTables,
                            resourceModel
                        ),
                    ],
                    AbstractIdentityTablesInNameOrder: [],
                    AbstractUnionViewsInNameOrder: [],
                    IndexesInCreateOrder: [],
                    TriggersInCreateOrder: []
                ),
                WritePlansByResource: new Dictionary<QualifiedResourceName, ResourceWritePlan>
                {
                    [resource] = new ResourceWritePlan(resourceModel, [rootPlan]),
                },
                ReadPlansByResource: new Dictionary<QualifiedResourceName, ResourceReadPlan>(),
                ResourceKeyIdByResource: new Dictionary<QualifiedResourceName, short> { [resource] = 1 },
                ResourceKeyById: new Dictionary<short, ResourceKeyEntry> { [1] = resourceKey },
                SecurableElementColumnPathsByResource: new Dictionary<
                    QualifiedResourceName,
                    IReadOnlyList<ResolvedSecurableElementPath>
                >()
            );
        }

        private static TableWritePlan CreateRootPlan(
            DbColumnModel? additionalColumn = null,
            WriteValueSource? additionalValueSource = null,
            string? additionalParameterName = null
        )
        {
            List<DbColumnModel> columns =
            [
                new DbColumnModel(
                    ColumnName: new DbColumnName("DocumentId"),
                    Kind: ColumnKind.ParentKeyPart,
                    ScalarType: null,
                    IsNullable: false,
                    SourceJsonPath: null,
                    TargetResource: null,
                    Storage: new ColumnStorage.Stored()
                ),
            ];

            if (additionalColumn is not null)
            {
                columns.Add(additionalColumn);
            }

            var tableModel = new DbTableModel(
                Table: new DbTableName(new DbSchemaName("testproject"), "Widget"),
                JsonScope: new JsonPathExpression("$", []),
                Key: new TableKey(
                    ConstraintName: "PK_Widget",
                    Columns: [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
                ),
                Columns: columns,
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

            List<WriteColumnBinding> columnBindings =
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
            ];

            if (
                additionalValueSource is not null
                && additionalParameterName is not null
                && tableModel.Columns.Count > 1
            )
            {
                columnBindings.Add(
                    new WriteColumnBinding(
                        tableModel.Columns[1],
                        additionalValueSource,
                        additionalParameterName
                    )
                );
            }

            return new TableWritePlan(
                TableModel: tableModel,
                InsertSql: "insert into testproject.\"Widget\" values (...)",
                UpdateSql: "update testproject.\"Widget\" set ...",
                DeleteByParentSql: null,
                BulkInsertBatching: new BulkInsertBatchingInfo(100, tableModel.Columns.Count, 1000),
                ColumnBindings: columnBindings,
                KeyUnificationPlans: []
            );
        }

        private static JsonPathExpression CreatePath(string canonical, params JsonPathSegment[] segments)
        {
            return new JsonPathExpression(canonical, segments);
        }
    }
}
