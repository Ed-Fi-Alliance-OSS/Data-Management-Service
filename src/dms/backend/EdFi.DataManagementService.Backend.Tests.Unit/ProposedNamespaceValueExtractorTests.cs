// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_a_proposed_namespace_value_extractor
{
    private static readonly DbTableName _rootTable = new(new DbSchemaName("edfi"), "Survey");
    private static readonly DbColumnName _namespaceColumn = new("Namespace");

    private static NamespaceAuthorizationCheckSpec ProposedCheck(DbColumnName? column = null) =>
        new(0, NamespaceAuthorizationCheckValueSource.Proposed, _rootTable, column ?? _namespaceColumn);

    [Test]
    public void It_extracts_the_proposed_namespace_from_the_finalized_root_row_binding()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal("uri://ed-fi.org/Survey"));

        var result = ProposedNamespaceValueExtractor.Extract([ProposedCheck()], rootRow);

        result
            .Should()
            .BeOfType<ProposedNamespaceValueExtractionResult.Ready>()
            .Which.ProposedNamespace.Should()
            .Be("uri://ed-fi.org/Survey");
    }

    [Test]
    public void It_extracts_a_null_proposed_namespace_when_the_finalized_value_is_null()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(null));

        var result = ProposedNamespaceValueExtractor.Extract([ProposedCheck()], rootRow);

        result
            .Should()
            .BeOfType<ProposedNamespaceValueExtractionResult.Ready>()
            .Which.ProposedNamespace.Should()
            .BeNull();
    }

    [Test]
    public void It_normalizes_an_empty_string_finalized_value_to_a_null_proposed_namespace()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(string.Empty));

        var result = ProposedNamespaceValueExtractor.Extract([ProposedCheck()], rootRow);

        result
            .Should()
            .BeOfType<ProposedNamespaceValueExtractionResult.Ready>()
            .Which.ProposedNamespace.Should()
            .BeNull();
    }

    [Test]
    public void It_returns_invalid_when_no_root_binding_matches_the_namespace_column()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal("uri://ed-fi.org/Survey"));

        var result = ProposedNamespaceValueExtractor.Extract(
            [ProposedCheck(new DbColumnName("NotAColumn"))],
            rootRow
        );

        result.Should().BeOfType<ProposedNamespaceValueExtractionResult.InvalidAuthorizationPlan>();
    }

    [Test]
    public void It_returns_invalid_when_a_check_is_not_a_proposed_value_source()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal("uri://ed-fi.org/Survey"));
        var storedCheck = new NamespaceAuthorizationCheckSpec(
            0,
            NamespaceAuthorizationCheckValueSource.Stored,
            _rootTable,
            _namespaceColumn
        );

        var result = ProposedNamespaceValueExtractor.Extract([storedCheck], rootRow);

        result.Should().BeOfType<ProposedNamespaceValueExtractionResult.InvalidAuthorizationPlan>();
    }

    [Test]
    public void It_returns_invalid_when_no_checks_are_supplied()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal("uri://ed-fi.org/Survey"));

        var result = ProposedNamespaceValueExtractor.Extract([], rootRow);

        result.Should().BeOfType<ProposedNamespaceValueExtractionResult.InvalidAuthorizationPlan>();
    }

    private static RootWriteRowBuffer CreateRootRow(FlattenedWriteValue namespaceValue)
    {
        var tableModel = new DbTableModel(
            _rootTable,
            new JsonPathExpression("$", []),
            new TableKey(
                "PK_Survey",
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
                    _namespaceColumn,
                    ColumnKind.Scalar,
                    new RelationalScalarType(ScalarKind.String, MaxLength: 255),
                    false,
                    new JsonPathExpression("$.namespace", []),
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

        var writePlan = new TableWritePlan(
            tableModel,
            InsertSql: "insert into edfi.\"Survey\" values (@DocumentId, @Namespace)",
            UpdateSql: "update edfi.\"Survey\" set \"Namespace\" = @Namespace where \"DocumentId\" = @DocumentId",
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
                        new JsonPathExpression("$.namespace", []),
                        new RelationalScalarType(ScalarKind.String, MaxLength: 255)
                    ),
                    "Namespace"
                ),
            ],
            KeyUnificationPlans: []
        );

        return new RootWriteRowBuffer(
            writePlan,
            [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, namespaceValue]
        );
    }
}
