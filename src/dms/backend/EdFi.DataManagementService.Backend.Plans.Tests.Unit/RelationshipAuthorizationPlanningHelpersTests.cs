// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_RelationshipAuthorizationPlanningHelpers
{
    private static readonly DbSchemaName _schema = new("edfi");

    [TestCase(SecurableElementKind.Student, RelationshipAuthorizationPersonKind.Student)]
    [TestCase(SecurableElementKind.Contact, RelationshipAuthorizationPersonKind.Contact)]
    [TestCase(SecurableElementKind.Staff, RelationshipAuthorizationPersonKind.Staff)]
    public void It_should_map_supported_person_securable_element_kinds(
        SecurableElementKind kind,
        RelationshipAuthorizationPersonKind expected
    )
    {
        var mapped = RelationshipAuthorizationPlanningHelpers.MapPersonKind(kind);

        mapped.Should().Be(expected);
    }

    [Test]
    public void It_should_reject_non_person_securable_element_kinds()
    {
        var act = () =>
            RelationshipAuthorizationPlanningHelpers.MapPersonKind(
                SecurableElementKind.EducationOrganization
            );

        act.Should()
            .Throw<ArgumentOutOfRangeException>()
            .WithParameterName("kind")
            .WithMessage("Unsupported relationship authorization person securable element kind.*");
    }

    [Test]
    public void It_should_return_the_single_root_scope_locator_column()
    {
        var table = CreateRootTable([new DbColumnName("DocumentId")]);

        var column = RelationshipAuthorizationPlanningHelpers.GetRootDocumentIdColumn(
            table,
            "relationship authorization"
        );

        column.Should().Be(new DbColumnName("DocumentId"));
    }

    [Test]
    public void It_should_reject_blank_root_locator_planning_contexts()
    {
        var table = CreateRootTable([new DbColumnName("DocumentId")]);

        var act = () => RelationshipAuthorizationPlanningHelpers.GetRootDocumentIdColumn(table, " ");

        act.Should().Throw<ArgumentException>().WithParameterName("planningContext");
    }

    [Test]
    public void It_should_report_missing_root_scope_locator_columns()
    {
        var table = CreateRootTable([]);

        var act = () =>
            RelationshipAuthorizationPlanningHelpers.GetRootDocumentIdColumn(
                table,
                "relationship authorization"
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Root table 'edfi.School' does not expose a root-scope locator column for relationship authorization."
            );
    }

    [Test]
    public void It_should_report_multiple_root_scope_locator_columns()
    {
        var table = CreateRootTable([new DbColumnName("DocumentId"), new DbColumnName("SchoolId")]);

        var act = () =>
            RelationshipAuthorizationPlanningHelpers.GetRootDocumentIdColumn(
                table,
                "relationship authorization"
            );

        act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Root table 'edfi.School' exposes multiple root-scope locator columns, which is not supported for relationship authorization."
            );
    }

    [Test]
    public void It_should_order_failures_by_strategy_local_order_location_and_hint()
    {
        var strategyHigh = CreateFailure("strategy-high", configuredIndex: 1);
        var strategyLow = CreateFailure("strategy-low", configuredIndex: 0);
        var localHigh = CreateFailure("local-high", configuredIndex: 10, relationshipLocalOrder: 1);
        var localLow = CreateFailure("local-low", configuredIndex: 10, relationshipLocalOrder: 0);
        var contributionHigh = CreateUnresolvedFailure(
            "contribution-high",
            configuredIndex: 20,
            contributionOrders: [1]
        );
        var contributionLow = CreateUnresolvedFailure(
            "contribution-low",
            configuredIndex: 20,
            contributionOrders: [0, 2]
        );
        var pathHigh = CreateFailure("path-high", configuredIndex: 30, jsonPath: "$.z");
        var pathLow = CreateFailure("path-low", configuredIndex: 30, jsonPath: "$.a");
        var readableHigh = CreateFailure("readable-high", configuredIndex: 40, readableName: "Z");
        var readableLow = CreateFailure("readable-low", configuredIndex: 40, readableName: "A");
        var tableHigh = CreateFailure("table-high", configuredIndex: 50, tableName: "ZTable");
        var tableLow = CreateFailure("table-low", configuredIndex: 50, tableName: "ATable");
        var columnHigh = CreateFailure("column-high", configuredIndex: 60, columnName: "ZColumn");
        var columnLow = CreateFailure("column-low", configuredIndex: 60, columnName: "AColumn");
        var authHigh = CreateFailure("auth-high", configuredIndex: 70, authObjectName: "ZAuth");
        var authLow = CreateFailure("auth-low", configuredIndex: 70, authObjectName: "AAuth");
        var hintHigh = CreateFailure("hint-high", configuredIndex: 80, hint: "Z hint");
        var hintLow = CreateFailure("hint-low", configuredIndex: 80, hint: "A hint");

        var ordered = RelationshipAuthorizationPlanningHelpers
            .OrderFailures([
                hintHigh,
                hintLow,
                authHigh,
                authLow,
                columnHigh,
                columnLow,
                tableHigh,
                tableLow,
                readableHigh,
                readableLow,
                pathHigh,
                pathLow,
                contributionHigh,
                contributionLow,
                localHigh,
                localLow,
                strategyHigh,
                strategyLow,
            ])
            .Select(static failure => failure.Resource.ResourceName);

        ordered
            .Should()
            .Equal(
                "strategy-low",
                "strategy-high",
                "local-low",
                "local-high",
                "contribution-low",
                "contribution-high",
                "path-low",
                "path-high",
                "readable-low",
                "readable-high",
                "table-low",
                "table-high",
                "column-low",
                "column-high",
                "auth-low",
                "auth-high",
                "hint-low",
                "hint-high"
            );
    }

    [Test]
    public void It_should_order_failures_with_configured_strategy_and_local_order_before_unspecified_values()
    {
        var unspecifiedStrategy = CreateFailure("unspecified-strategy", configuredIndex: null);
        var specifiedStrategy = CreateFailure("specified-strategy", configuredIndex: 0);
        var unspecifiedLocalOrder = CreateFailure(
            "unspecified-local-order",
            configuredIndex: 10,
            relationshipLocalOrder: null
        );
        var specifiedLocalOrder = CreateFailure(
            "specified-local-order",
            configuredIndex: 10,
            relationshipLocalOrder: 0
        );

        var ordered = RelationshipAuthorizationPlanningHelpers
            .OrderFailures([
                unspecifiedStrategy,
                specifiedStrategy,
                unspecifiedLocalOrder,
                specifiedLocalOrder,
            ])
            .Select(static failure => failure.Resource.ResourceName);

        ordered
            .Should()
            .Equal(
                "specified-strategy",
                "specified-local-order",
                "unspecified-local-order",
                "unspecified-strategy"
            );
    }

    private static RelationshipAuthorizationFailureMetadata CreateFailure(
        string resourceName,
        int? configuredIndex,
        int? relationshipLocalOrder = 0,
        string jsonPath = "$.same",
        string readableName = "Same",
        string tableName = "SameTable",
        string columnName = "SameColumn",
        string authObjectName = "SameAuth",
        string hint = "same hint"
    ) =>
        new(
            RelationshipAuthorizationFailureKind.InvalidAuthorizationStrategy,
            new QualifiedResourceName("Ed-Fi", resourceName),
            configuredIndex is null
                ? null
                : new ConfiguredAuthorizationStrategy(
                    $"Strategy{configuredIndex.Value}",
                    configuredIndex.Value
                ),
            relationshipLocalOrder,
            Location: new RelationshipAuthorizationFailureLocation(
                JsonPath: jsonPath,
                ReadableName: readableName,
                Table: new DbTableName(_schema, tableName),
                Column: new DbColumnName(columnName),
                AuthorizationObjectName: authObjectName
            ),
            Hint: hint
        );

    private static RelationshipAuthorizationFailureMetadata CreateUnresolvedFailure(
        string resourceName,
        int configuredIndex,
        IReadOnlyList<int> contributionOrders
    ) =>
        new(
            RelationshipAuthorizationFailureKind.UnresolvedSecurableElement,
            new QualifiedResourceName("Ed-Fi", resourceName),
            new ConfiguredAuthorizationStrategy($"Strategy{configuredIndex}", configuredIndex),
            RelationshipLocalOrder: 0,
            Location: new RelationshipAuthorizationFailureLocation(
                JsonPath: "$.same",
                ReadableName: "Same",
                Table: new DbTableName(_schema, "SameTable"),
                Column: new DbColumnName("SameColumn"),
                AuthorizationObjectName: "SameAuth"
            ),
            Hint: "same hint"
        )
        {
            Contributors = contributionOrders
                .Select(static order => new RelationshipAuthorizationSubjectContributor(
                    SecurableElementKind.EducationOrganization,
                    "$.schoolReference.schoolId",
                    "SchoolId",
                    order
                ))
                .ToArray(),
        };

    private static DbTableModel CreateRootTable(IReadOnlyList<DbColumnName> rootScopeLocatorColumns)
    {
        var documentIdColumn = new DbColumnName("DocumentId");

        return new DbTableModel(
            Table: new DbTableName(_schema, "School"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey("PK_School", [new DbKeyColumn(documentIdColumn, ColumnKind.ParentKeyPart)]),
            Columns: [CreateColumn(documentIdColumn)],
            Constraints: []
        )
        {
            IdentityMetadata = new DbTableIdentityMetadata(
                TableKind: DbTableKind.Root,
                PhysicalRowIdentityColumns: [documentIdColumn],
                RootScopeLocatorColumns: rootScopeLocatorColumns,
                ImmediateParentScopeLocatorColumns: [],
                SemanticIdentityBindings: []
            ),
        };
    }

    private static DbColumnModel CreateColumn(DbColumnName columnName) =>
        new(
            ColumnName: columnName,
            Kind: ColumnKind.ParentKeyPart,
            ScalarType: new RelationalScalarType(ScalarKind.Int64),
            IsNullable: false,
            SourceJsonPath: null,
            TargetResource: null
        );
}
