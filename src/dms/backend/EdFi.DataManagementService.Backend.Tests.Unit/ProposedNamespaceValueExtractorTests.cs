// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
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

    private static readonly JsonNode EmptyBody = new JsonObject();
    private static readonly IReadOnlySet<string> NoDeferred = new HashSet<string>(StringComparer.Ordinal);

    private static NamespaceAuthorizationCheckSpec ProposedCheck(DbColumnName? column = null) =>
        new(0, NamespaceAuthorizationCheckValueSource.Proposed, _rootTable, column ?? _namespaceColumn);

    [Test]
    public void It_extracts_the_proposed_namespace_from_the_finalized_root_row_binding()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal("uri://ed-fi.org/Survey"));

        var result = ProposedNamespaceValueExtractor.Extract(
            [ProposedCheck()],
            rootRow,
            EmptyBody,
            NoDeferred
        );

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

        var result = ProposedNamespaceValueExtractor.Extract(
            [ProposedCheck()],
            rootRow,
            EmptyBody,
            NoDeferred
        );

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

        var result = ProposedNamespaceValueExtractor.Extract(
            [ProposedCheck()],
            rootRow,
            EmptyBody,
            NoDeferred
        );

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
            rootRow,
            EmptyBody,
            NoDeferred
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

        var result = ProposedNamespaceValueExtractor.Extract([storedCheck], rootRow, EmptyBody, NoDeferred);

        result.Should().BeOfType<ProposedNamespaceValueExtractionResult.InvalidAuthorizationPlan>();
    }

    [Test]
    public void It_returns_invalid_when_no_checks_are_supplied()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal("uri://ed-fi.org/Survey"));

        var result = ProposedNamespaceValueExtractor.Extract([], rootRow, EmptyBody, NoDeferred);

        result.Should().BeOfType<ProposedNamespaceValueExtractionResult.InvalidAuthorizationPlan>();
    }

    [Test]
    public void It_reads_the_request_body_namespace_for_a_deferred_missing_reference_derived_column()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(null), ReferenceDerivedNamespaceSource());

        var result = Extract(
            rootRow,
            """{ "surveyReference": { "namespace": "uri://ed-fi.org/Survey" } }""",
            "$.surveyReference"
        );

        ReadyNamespace(result).Should().Be("uri://ed-fi.org/Survey");
    }

    [Test]
    public void It_keeps_the_row_value_for_a_resolved_reference_derived_column()
    {
        var rootRow = CreateRootRow(
            new FlattenedWriteValue.Literal("uri://ed-fi.org/Resolved"),
            ReferenceDerivedNamespaceSource()
        );

        var result = Extract(rootRow, """{ "surveyReference": { "namespace": "uri://ed-fi.org/Body" } }""");

        ReadyNamespace(result).Should().Be("uri://ed-fi.org/Resolved");
    }

    [Test]
    public void It_keeps_the_row_value_for_a_scalar_namespace_column_when_references_are_deferred()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal("uri://ed-fi.org/Row"));

        var result = Extract(
            rootRow,
            """{ "namespace": "uri://ed-fi.org/Body", "surveyReference": { "namespace": "uri://ed-fi.org/Ref" } }""",
            "$.surveyReference"
        );

        ReadyNamespace(result).Should().Be("uri://ed-fi.org/Row");
    }

    [Test]
    public void It_keeps_the_row_value_when_the_deferred_reference_is_a_different_reference()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(null), ReferenceDerivedNamespaceSource());

        var result = Extract(
            rootRow,
            """{ "surveyReference": { "namespace": "uri://ed-fi.org/Body" } }""",
            "$.otherReference"
        );

        ReadyNamespace(result).Should().BeNull();
    }

    [Test]
    public void It_extracts_a_null_namespace_when_the_deferred_reference_body_value_is_absent()
    {
        var rootRow = CreateRootRow(
            new FlattenedWriteValue.Literal("uri://ed-fi.org/Stale"),
            ReferenceDerivedNamespaceSource()
        );

        var result = Extract(rootRow, """{ "surveyReference": { } }""", "$.surveyReference");

        ReadyNamespace(result).Should().BeNull();
    }

    [Test]
    public void It_extracts_a_null_namespace_when_the_deferred_reference_body_value_is_json_null()
    {
        var rootRow = CreateRootRow(
            new FlattenedWriteValue.Literal("uri://ed-fi.org/Stale"),
            ReferenceDerivedNamespaceSource()
        );

        var result = Extract(
            rootRow,
            """{ "surveyReference": { "namespace": null } }""",
            "$.surveyReference"
        );

        ReadyNamespace(result).Should().BeNull();
    }

    [Test]
    public void It_normalizes_an_empty_deferred_reference_body_value_to_a_null_namespace()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(null), ReferenceDerivedNamespaceSource());

        var result = Extract(rootRow, """{ "surveyReference": { "namespace": "" } }""", "$.surveyReference");

        ReadyNamespace(result).Should().BeNull();
    }

    [Test]
    public void It_returns_invalid_when_the_deferred_reference_body_value_is_not_a_string()
    {
        var rootRow = CreateRootRow(new FlattenedWriteValue.Literal(null), ReferenceDerivedNamespaceSource());

        var result = Extract(rootRow, """{ "surveyReference": { "namespace": 42 } }""", "$.surveyReference");

        result.Should().BeOfType<ProposedNamespaceValueExtractionResult.InvalidAuthorizationPlan>();
    }

    [Test]
    public void It_reads_the_first_deferred_member_body_value_for_a_null_key_unified_canonical()
    {
        var rootRow = CreateRootRow(
            new FlattenedWriteValue.Literal(null),
            new WriteValueSource.Precomputed(),
            [KeyUnifiedNamespacePlan()]
        );

        var result = Extract(
            rootRow,
            """
            {
              "firstReference": { "namespace": "uri://ed-fi.org/First" },
              "secondReference": { "namespace": "uri://ed-fi.org/Second" }
            }
            """,
            "$.firstReference",
            "$.secondReference"
        );

        ReadyNamespace(result).Should().Be("uri://ed-fi.org/First");
    }

    [Test]
    public void It_skips_a_non_deferred_member_when_reading_a_null_key_unified_canonical()
    {
        var rootRow = CreateRootRow(
            new FlattenedWriteValue.Literal(null),
            new WriteValueSource.Precomputed(),
            [KeyUnifiedNamespacePlan()]
        );

        var result = Extract(
            rootRow,
            """
            {
              "firstReference": { "namespace": "uri://ed-fi.org/First" },
              "secondReference": { "namespace": "uri://ed-fi.org/Second" }
            }
            """,
            "$.secondReference"
        );

        ReadyNamespace(result).Should().Be("uri://ed-fi.org/Second");
    }

    [Test]
    public void It_keeps_a_non_null_key_unified_canonical_even_when_another_member_is_deferred()
    {
        var rootRow = CreateRootRow(
            new FlattenedWriteValue.Literal("uri://ed-fi.org/Resolved"),
            new WriteValueSource.Precomputed(),
            [KeyUnifiedNamespacePlan()]
        );

        var result = Extract(
            rootRow,
            """
            {
              "firstReference": { "namespace": "uri://ed-fi.org/First" },
              "secondReference": { "namespace": "uri://ed-fi.org/Second" }
            }
            """,
            "$.secondReference"
        );

        ReadyNamespace(result).Should().Be("uri://ed-fi.org/Resolved");
    }

    private static ProposedNamespaceValueExtractionResult Extract(
        RootWriteRowBuffer rootRow,
        string bodyJson,
        params string[] deferredPaths
    ) =>
        ProposedNamespaceValueExtractor.Extract(
            [ProposedCheck()],
            rootRow,
            JsonNode.Parse(bodyJson)!,
            new HashSet<string>(deferredPaths, StringComparer.Ordinal)
        );

    private static string? ReadyNamespace(ProposedNamespaceValueExtractionResult result) =>
        result.Should().BeOfType<ProposedNamespaceValueExtractionResult.Ready>().Subject.ProposedNamespace;

    private static ReferenceDerivedValueSourceMetadata ReferenceMetadata(string referenceName) =>
        new(
            BindingIndex: 0,
            ReferenceObjectPath: new JsonPathExpression(
                $"$.{referenceName}",
                [new JsonPathSegment.Property(referenceName)]
            ),
            IdentityJsonPath: new JsonPathExpression(
                "$.namespace",
                [new JsonPathSegment.Property("namespace")]
            ),
            ReferenceJsonPath: new JsonPathExpression(
                $"$.{referenceName}.namespace",
                [new JsonPathSegment.Property(referenceName), new JsonPathSegment.Property("namespace")]
            )
        );

    private static WriteValueSource ReferenceDerivedNamespaceSource() =>
        new WriteValueSource.ReferenceDerived(ReferenceMetadata("surveyReference"));

    private static KeyUnificationWritePlan KeyUnifiedNamespacePlan() =>
        new(
            _namespaceColumn,
            CanonicalBindingIndex: 1,
            [KeyUnifiedMember("firstReference"), KeyUnifiedMember("secondReference")]
        );

    private static KeyUnificationMemberWritePlan KeyUnifiedMember(string referenceName) =>
        new KeyUnificationMemberWritePlan.ReferenceDerivedMember(
            new DbColumnName($"{referenceName}_Namespace"),
            new JsonPathExpression(
                $"$.{referenceName}.namespace",
                [new JsonPathSegment.Property(referenceName), new JsonPathSegment.Property("namespace")]
            ),
            ReferenceMetadata(referenceName),
            PresenceColumn: null,
            PresenceBindingIndex: null,
            PresenceIsSynthetic: false
        );

    private static RootWriteRowBuffer CreateRootRow(
        FlattenedWriteValue namespaceValue,
        WriteValueSource? namespaceSource = null,
        KeyUnificationWritePlan[]? keyUnificationPlans = null
    )
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
                    namespaceSource is WriteValueSource.ReferenceDerived referenceDerived
                        ? referenceDerived.ReferenceSource.ReferenceJsonPath
                        : new JsonPathExpression("$.namespace", []),
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
                    namespaceSource
                        ?? new WriteValueSource.Scalar(
                            new JsonPathExpression("$.namespace", []),
                            new RelationalScalarType(ScalarKind.String, MaxLength: 255)
                        ),
                    "Namespace"
                ),
            ],
            KeyUnificationPlans: keyUnificationPlans ?? []
        );

        return new RootWriteRowBuffer(
            writePlan,
            [FlattenedWriteValue.UnresolvedRootDocumentId.Instance, namespaceValue]
        );
    }
}
