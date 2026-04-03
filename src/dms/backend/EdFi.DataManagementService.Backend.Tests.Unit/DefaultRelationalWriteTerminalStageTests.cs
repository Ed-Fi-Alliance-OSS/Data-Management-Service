// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Core.External.Backend;
using EdFi.DataManagementService.Core.External.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_Default_Relational_Write_Terminal_Stage
{
    private DefaultRelationalWriteTerminalStage _sut = null!;

    [SetUp]
    public void Setup()
    {
        _sut = new DefaultRelationalWriteTerminalStage();
    }

    [Test]
    public async Task It_returns_a_precise_unknown_failure_for_post_requests()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Post);

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteTerminalStageResult.Upsert(
                    new UpsertResult.UnknownFailure(
                        "Relational POST terminal write stage is not implemented for resource 'Ed-Fi.School'. "
                            + "Write-plan selection, target-context resolution, reference resolution, and flattening succeeded, but relational command execution is still pending."
                    )
                )
            );
    }

    [Test]
    public async Task It_returns_a_precise_unknown_failure_for_put_requests()
    {
        var request = CreateRequest(RelationalWriteOperationKind.Put);

        var result = await _sut.ExecuteAsync(request);

        result
            .Should()
            .BeEquivalentTo(
                new RelationalWriteTerminalStageResult.Update(
                    new UpdateResult.UnknownFailure(
                        "Relational PUT terminal write stage is not implemented for resource 'Ed-Fi.School'. "
                            + "Write-plan selection, target-context resolution, reference resolution, and flattening succeeded, but relational command execution is still pending."
                    )
                )
            );
    }

    private static RelationalWriteTerminalStageRequest CreateRequest(
        RelationalWriteOperationKind operationKind
    )
    {
        var writePlan = CreateRootPlan();
        var flatteningInput = new FlatteningInput(
            operationKind,
            new RelationalWriteTargetContext.CreateNew(new DocumentUuid(Guid.NewGuid())),
            new ResourceWritePlan(CreateRelationalResourceModel(writePlan.TableModel), [writePlan]),
            JsonNode.Parse("""{"name":"Lincoln High"}""")!,
            new ResolvedReferenceSet(
                SuccessfulDocumentReferencesByPath: new Dictionary<JsonPath, ResolvedDocumentReference>(),
                SuccessfulDescriptorReferencesByPath: new Dictionary<JsonPath, ResolvedDescriptorReference>(),
                LookupsByReferentialId: new Dictionary<ReferentialId, ReferenceLookupSnapshot>(),
                InvalidDocumentReferences: [],
                InvalidDescriptorReferences: [],
                DocumentReferenceOccurrences: [],
                DescriptorReferenceOccurrences: []
            )
        );

        return new RelationalWriteTerminalStageRequest(
            flatteningInput,
            new FlattenedWriteSet(
                new RootWriteRowBuffer(
                    writePlan,
                    [
                        FlattenedWriteValue.UnresolvedRootDocumentId.Instance,
                        new FlattenedWriteValue.Literal("Lincoln High"),
                    ]
                )
            ),
            new TraceId("terminal-stage-test")
        );
    }

    private static RelationalResourceModel CreateRelationalResourceModel(DbTableModel rootTable)
    {
        var resource = new QualifiedResourceName("Ed-Fi", "School");

        return new RelationalResourceModel(
            Resource: resource,
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: [],
            DescriptorEdgeSources: []
        );
    }

    private static TableWritePlan CreateRootPlan()
    {
        var tableModel = new DbTableModel(
            new DbTableName(new DbSchemaName("edfi"), "School"),
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_School",
                [new DbKeyColumn(new DbColumnName("DocumentId"), ColumnKind.ParentKeyPart)]
            ),
            [
                new DbColumnModel(
                    new DbColumnName("DocumentId"),
                    ColumnKind.ParentKeyPart,
                    null,
                    false,
                    null,
                    null,
                    new ColumnStorage.Stored()
                ),
                new DbColumnModel(
                    new DbColumnName("Name"),
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 75),
                    false,
                    new JsonPathExpression("$.name", []),
                    null,
                    new ColumnStorage.Stored()
                ),
            ],
            []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                DbTableKind.Root,
                [new DbColumnName("DocumentId")],
                [new DbColumnName("DocumentId")],
                [],
                []
            ),
        };

        return new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"School\" values (@DocumentId, @Name)",
            UpdateSql: "update edfi.\"School\" set \"Name\" = @Name where \"DocumentId\" = @DocumentId",
            DeleteByParentSql: null,
            BulkInsertBatching: new BulkInsertBatchingInfo(100, 2, 1000),
            ColumnBindings:
            [
                new WriteColumnBinding(
                    tableModel.Columns[0],
                    new WriteValueSource.DocumentId(),
                    "DocumentId"
                ),
                new WriteColumnBinding(
                    tableModel.Columns[1],
                    new WriteValueSource.Scalar(
                        new JsonPathExpression("$.name", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 75)
                    ),
                    "Name"
                ),
            ],
            KeyUnificationPlans: []
        );
    }
}
