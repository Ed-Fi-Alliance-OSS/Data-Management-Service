// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.External.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_WritePlanDocumentReferenceConventions
{
    [Test]
    public void It_should_resolve_document_reference_bindings_from_the_write_plan_model_by_index()
    {
        var primaryBinding = CreateDocumentReferenceBinding(
            referenceObjectPath: "$.schoolReference",
            fkColumnName: "School_DocumentId"
        );

        var secondaryBinding = CreateDocumentReferenceBinding(
            referenceObjectPath: "$.calendarReference",
            fkColumnName: "Calendar_DocumentId"
        );

        var writePlan = CreateWritePlan([primaryBinding, secondaryBinding]);

        var resolved = WritePlanDocumentReferenceConventions.ResolveBinding(
            writePlan,
            new WriteValueSource.DocumentReference(BindingIndex: 1)
        );

        resolved.Should().Be(secondaryBinding);
        resolved.ReferenceObjectPath.Canonical.Should().Be("$.calendarReference");
    }

    [Test]
    public void It_should_fail_fast_when_document_reference_binding_index_is_out_of_range()
    {
        var writePlan = CreateWritePlan([
            CreateDocumentReferenceBinding("$.schoolReference", "School_DocumentId"),
        ]);

        var act = () =>
            WritePlanDocumentReferenceConventions.ResolveBinding(
                writePlan,
                new WriteValueSource.DocumentReference(BindingIndex: 2)
            );

        var exception = act.Should().Throw<ArgumentOutOfRangeException>().Which;
        exception.ParamName.Should().Be("bindingIndex");
        exception.Message.Should().Contain("DocumentReferenceBindings");
        exception.Message.Should().Contain("count: 1");
    }

    private static ResourceWritePlan CreateWritePlan(
        IReadOnlyList<DocumentReferenceBinding> documentReferenceBindings
    )
    {
        var rootTable = CreateRootTable();

        var model = new RelationalResourceModel(
            Resource: new QualifiedResourceName("Ed-Fi", "Student"),
            PhysicalSchema: new DbSchemaName("edfi"),
            StorageKind: ResourceStorageKind.RelationalTables,
            Root: rootTable,
            TablesInDependencyOrder: [rootTable],
            DocumentReferenceBindings: documentReferenceBindings,
            DescriptorEdgeSources: []
        );

        return new ResourceWritePlan(Model: model, TablePlansInDependencyOrder: []);
    }

    private static DbTableModel CreateRootTable()
    {
        return new DbTableModel(
            Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
            JsonScope: new JsonPathExpression("$", []),
            Key: new TableKey(
                ConstraintName: "PK_Student",
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
        );
    }

    private static DocumentReferenceBinding CreateDocumentReferenceBinding(
        string referenceObjectPath,
        string fkColumnName
    )
    {
        var referencePropertyName = referenceObjectPath[(referenceObjectPath.LastIndexOf('.') + 1)..];

        return new DocumentReferenceBinding(
            IsIdentityComponent: false,
            ReferenceObjectPath: new JsonPathExpression(
                referenceObjectPath,
                [new JsonPathSegment.Property(referencePropertyName)]
            ),
            Table: new DbTableName(new DbSchemaName("edfi"), "Student"),
            FkColumn: new DbColumnName(fkColumnName),
            TargetResource: new QualifiedResourceName("Ed-Fi", "School"),
            IdentityBindings: []
        );
    }
}
