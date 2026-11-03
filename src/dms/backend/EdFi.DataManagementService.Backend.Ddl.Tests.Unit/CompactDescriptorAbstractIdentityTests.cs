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
public class Given_Abstract_Identity_Maintenance_With_Unified_Descriptor_Reference_Identity(
    SqlDialect dialect
)
{
    private string _ddl = null!;

    [SetUp]
    public void Setup()
    {
        var model = CompactDescriptorAbstractIdentityTestModel.Build(dialect, unifiedReferenceIdentity: true);
        _ddl = new RelationalModelDdlEmitter(SqlDialectFactory.Create(dialect)).Emit(model);
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
        _ddl.Should().NotContain("OLD.\"Program_ProgramTypeDescriptor_DescriptorId\" IS DISTINCT FROM");
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
