// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel.Naming;
using EdFi.DataManagementService.Backend.RelationalModel.SetPasses;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_A_Document_Resource_Key_Foreign_Key
{
    private Action _validate = default!;

    [SetUp]
    public void Setup()
    {
        _validate = DocumentTargetValidation.Build([
            RelationalNameConventions.DocumentIdColumnName,
            RelationalNameConventions.ResourceKeyIdColumnName,
        ]);
    }

    [Test]
    public void It_accepts_DocumentId_then_ResourceKeyId()
    {
        _validate.Should().NotThrow();
    }
}

[TestFixture]
public class Given_A_Reversed_Document_Resource_Key_Foreign_Key
{
    private Action _validate = default!;

    [SetUp]
    public void Setup()
    {
        _validate = DocumentTargetValidation.Build([
            RelationalNameConventions.ResourceKeyIdColumnName,
            RelationalNameConventions.DocumentIdColumnName,
        ]);
    }

    [Test]
    public void It_rejects_the_target_columns()
    {
        _validate
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage(
                "*'ResourceKeyId', 'DocumentId'*exactly (DocumentId) or exactly (DocumentId, ResourceKeyId)*"
            );
    }
}

[TestFixture]
public class Given_A_Document_Foreign_Key_On_ResourceKeyId_Only
{
    private Action _validate = default!;

    [SetUp]
    public void Setup()
    {
        _validate = DocumentTargetValidation.Build([RelationalNameConventions.ResourceKeyIdColumnName]);
    }

    [Test]
    public void It_rejects_the_target_columns()
    {
        _validate
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*'ResourceKeyId'*exactly (DocumentId) or exactly (DocumentId, ResourceKeyId)*");
    }
}

file static class DocumentTargetValidation
{
    private static readonly DbTableName _referencingTable = new(
        new DbSchemaName("edfi"),
        "EducationOrganizationIdentity"
    );
    private static readonly DbTableName _documentTable = new(new DbSchemaName("dms"), "Document");

    public static Action Build(IReadOnlyList<DbColumnName> targetColumns)
    {
        var foreignKey = new TableConstraint.ForeignKey(
            "FK_EducationOrganizationIdentity_DocumentResourceKey",
            targetColumns,
            _documentTable,
            targetColumns
        );

        return () =>
            ForeignKeyStorageValidator.ValidateDocumentTargetColumns(
                foreignKey,
                _referencingTable,
                _documentTable,
                targetColumns
            );
    }
}
