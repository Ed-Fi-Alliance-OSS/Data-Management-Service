// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Tests.Common;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

[TestFixture(SqlDialect.Pgsql)]
[TestFixture(SqlDialect.Mssql)]
public class Given_ReferentialIdentity_Maintenance_With_Unified_Descriptor_Reference_Identity(
    SqlDialect dialect
)
{
    private string _ddl = null!;
    private string _hashExpression = null!;

    [SetUp]
    public void Setup()
    {
        var model = CompactDescriptorReferentialIdentityTestModel.Build(
            dialect,
            unifiedReferenceIdentity: true
        );
        _ddl = new RelationalModelDdlEmitter(SqlDialectFactory.Create(dialect)).Emit(model);
        _hashExpression =
            dialect is SqlDialect.Pgsql
                ? "'$.programTypeDescriptor=' || lower((SELECT descriptor.\"Namespace\" || '#' || descriptor.\"CodeValue\" FROM \"dms\".\"Descriptor\" descriptor WHERE descriptor.\"DescriptorId\" = NEW.\"ProgramTypeDescriptor_DescriptorId\")) || '#' || '$.programReference.programTypeDescriptor=' || lower((SELECT descriptor.\"Namespace\" || '#' || descriptor.\"CodeValue\" FROM \"dms\".\"Descriptor\" descriptor WHERE descriptor.\"DescriptorId\" = NEW.\"Program_ProgramTypeDescriptor_DescriptorId\"))"
                : "N'$.programTypeDescriptor=' + LOWER((SELECT descriptor.[Namespace] + N'#' + descriptor.[CodeValue] FROM [dms].[Descriptor] descriptor WHERE descriptor.[DescriptorId] = i.[ProgramTypeDescriptor_DescriptorId])) + N'#' + N'$.programReference.programTypeDescriptor=' + LOWER((SELECT descriptor.[Namespace] + N'#' + descriptor.[CodeValue] FROM [dms].[Descriptor] descriptor WHERE descriptor.[DescriptorId] = i.[Program_ProgramTypeDescriptor_DescriptorId]))";
    }

    [Test]
    public void It_hashes_each_identity_path_once_in_order_including_the_copied_unified_alias()
    {
        _ddl.Should().Contain(_hashExpression);
        _ddl.Should().NotContain(dialect is SqlDialect.Pgsql ? "descriptor.\"Uri\"" : "descriptor.[Uri]");
        _ddl.Should()
            .NotContain(
                dialect is SqlDialect.Pgsql
                    ? "descriptor.\"DocumentId\" = NEW."
                    : "descriptor.[DocumentId] = i."
            );
    }

    [Test]
    public void It_preserves_the_uuid_namespace_resource_prefix_and_document_key_for_both_aliases()
    {
        if (dialect is SqlDialect.Pgsql)
        {
            _ddl.Should()
                .Contain(
                    "'edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiProgramOffering' || "
                        + _hashExpression
                        + "), NEW.\"DocumentId\", 1);"
                );
            _ddl.Should()
                .Contain(
                    "'edf1edf1-3df1-3df1-3df1-3df1edf1edf1'::uuid, 'Ed-FiGeneralProgramOffering' || "
                        + _hashExpression
                        + "), NEW.\"DocumentId\", 2);"
                );
            _ddl.Should().Contain("WHERE \"DocumentId\" = NEW.\"DocumentId\" AND \"ResourceKeyId\" = 2;");
        }
        else
        {
            _ddl.Should()
                .Contain(
                    "'edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiProgramOffering' AS nvarchar(max)) + "
                        + _hashExpression
                        + "), i.[DocumentId], 1"
                );
            _ddl.Should()
                .Contain(
                    "'edf1edf1-3df1-3df1-3df1-3df1edf1edf1', CAST(N'Ed-FiGeneralProgramOffering' AS nvarchar(max)) + "
                        + _hashExpression
                        + "), i.[DocumentId], 2"
                );
            _ddl.Should()
                .Contain(
                    "WHERE [DocumentId] IN (SELECT [DocumentId] FROM @changedDocs) AND [ResourceKeyId] = 2;"
                );
            _ddl.Should()
                .Contain("FROM inserted i INNER JOIN @changedDocs cd ON cd.[DocumentId] = i.[DocumentId]");
        }
    }

    [Test]
    public void It_uses_the_stored_compact_column_for_identity_change_guards()
    {
        _ddl.Should()
            .Contain(
                dialect is SqlDialect.Pgsql
                    ? "IF TG_OP = 'INSERT' OR (OLD.\"ProgramTypeDescriptor_DescriptorId\" IS DISTINCT FROM NEW.\"ProgramTypeDescriptor_DescriptorId\") THEN"
                    : "ELSE IF (UPDATE([ProgramTypeDescriptor_DescriptorId]))"
            );
        _ddl.Should().NotContain("UPDATE([Program_ProgramTypeDescriptor_DescriptorId])");
    }

    [Test]
    public void It_retains_the_abstract_union_discriminator_and_compact_projections()
    {
        _ddl.Should()
            .Contain(
                dialect is SqlDialect.Pgsql
                    ? "'Ed-Fi.ProgramOffering'::varchar AS \"Discriminator\""
                    : "CAST(N'Ed-Fi.ProgramOffering' AS nvarchar(max)) AS [Discriminator]"
            );
        _ddl.Should()
            .Contain(
                dialect is SqlDialect.Pgsql
                    ? "\"Program_ProgramTypeDescriptor_DescriptorId\" integer GENERATED ALWAYS AS"
                    : "[Program_ProgramTypeDescriptor_DescriptorId] AS"
            );
    }
}
