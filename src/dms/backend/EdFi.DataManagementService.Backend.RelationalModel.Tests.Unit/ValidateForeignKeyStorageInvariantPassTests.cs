// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_Foreign_Key_Storage_Validation_Of_External_Core_Tables
{
    [Test]
    public void It_accepts_a_stored_document_endpoint()
    {
        var context = CreateContext();
        SetForeignKey(context, "dms", "Document", "DocumentId", "DocumentId");

        var validate = () => new ValidateForeignKeyStorageInvariantPass().Execute(context);

        validate.Should().NotThrow();
    }

    [Test]
    public void It_rejects_a_missing_local_document_endpoint()
    {
        var context = CreateContext();
        SetForeignKey(context, "dms", "Document", "MissingId", "DocumentId");

        var validate = () => new ValidateForeignKeyStorageInvariantPass().Execute(context);

        validate
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*FK_Enrollment_Core*edfi.Enrollment*dms.Document*invalid local column(s)*'MissingId' (missing)*"
            );
    }

    [Test]
    public void It_rejects_an_unsupported_document_target_column()
    {
        var context = CreateContext();
        SetForeignKey(context, "dms", "Document", "DocumentId", "ResourceKeyId");

        var validate = () => new ValidateForeignKeyStorageInvariantPass().Execute(context);

        validate
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*FK_Enrollment_Core*edfi.Enrollment*dms.Document*invalid target column(s)*'ResourceKeyId'*exactly (DocumentId) or exactly (DocumentId, ResourceKeyId)*"
            );
    }

    [TestCase("edfi", "Document")]
    [TestCase("dms", "Other")]
    public void It_requires_both_the_core_schema_and_document_table_name(string schema, string table)
    {
        var context = CreateContext();
        SetForeignKey(context, schema, table, "DocumentId", "DocumentId");

        var validate = () => new ValidateForeignKeyStorageInvariantPass().Execute(context);

        validate
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                $"Foreign key 'FK_Enrollment_Core' from table 'edfi.Enrollment' references unknown target table '{schema}.{table}'."
            );
    }

    [Test]
    public void It_rejects_noncompact_descriptor_storage_even_without_a_foreign_key()
    {
        var context = CreateContext();
        RewriteRoot(
            context,
            root =>
                root with
                {
                    Columns = root
                        .Columns.Select(column =>
                            column.ColumnName.Value == "DocumentId"
                                ? column with
                                {
                                    Kind = ColumnKind.DescriptorFk,
                                }
                                : column
                        )
                        .ToArray(),
                    Constraints = [],
                }
        );

        var validate = () => new ValidateForeignKeyStorageInvariantPass().Execute(context);

        validate
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("Descriptor column 'edfi.Enrollment.DocumentId' must use Int32 storage.");
    }

    [Test]
    public void It_rejects_a_descriptor_foreign_key_with_document_sized_storage()
    {
        var context = CreateContext();
        SetForeignKey(context, "dms", "Descriptor", "DocumentId", "DescriptorId");

        var validate = () => new ValidateForeignKeyStorageInvariantPass().Execute(context);

        validate
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "Descriptor foreign key 'FK_Enrollment_Core' from table 'edfi.Enrollment' must use Int32 storage."
            );
    }

    private static RelationalModelSetBuilderContext CreateContext()
    {
        var projectSchema = PruningTestSchemaBuilder.BuildProjectSchema(
            new PruningTestSchemaBuilder.ResourceSpec("Enrollment", IdentityScalars: ["enrollmentId"])
        );
        var project = EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
            projectSchema,
            isExtensionProject: false
        );
        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet([project]);
        var context = new RelationalModelSetBuilderContext(
            schemaSet,
            SqlDialect.Pgsql,
            new PgsqlDialectRules()
        );
        new BaseTraversalAndDescriptorBindingPass().Execute(context);
        return context;
    }

    private static void SetForeignKey(
        RelationalModelSetBuilderContext context,
        string schema,
        string table,
        string localColumn,
        string targetColumn
    )
    {
        RewriteRoot(
            context,
            root =>
                root with
                {
                    Constraints =
                    [
                        new TableConstraint.ForeignKey(
                            "FK_Enrollment_Core",
                            [new DbColumnName(localColumn)],
                            new DbTableName(new DbSchemaName(schema), table),
                            [new DbColumnName(targetColumn)]
                        ),
                    ],
                }
        );
    }

    private static void RewriteRoot(
        RelationalModelSetBuilderContext context,
        Func<DbTableModel, DbTableModel> rewrite
    )
    {
        var resource = context.ConcreteResourcesInNameOrder.Single();
        var root = rewrite(resource.RelationalModel.Root);
        context.ConcreteResourcesInNameOrder[0] = resource with
        {
            RelationalModel = resource.RelationalModel with { Root = root, TablesInDependencyOrder = [root] },
        };
    }
}
