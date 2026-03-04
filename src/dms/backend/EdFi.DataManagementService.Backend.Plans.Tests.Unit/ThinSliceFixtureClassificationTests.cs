// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_ThinSlice_RuntimePlanCompilation_ApiSchema_Fixture
{
    private const string FixturePath = "Fixtures/runtime-plan-compilation/ApiSchema.json";
    private static readonly QualifiedResourceName _studentResource = new("Ed-Fi", "Student");
    private static readonly QualifiedResourceName _schoolResource = new("Ed-Fi", "School");
    private static readonly QualifiedResourceName _descriptorResource = new(
        "Ed-Fi",
        "AcademicSubjectDescriptor"
    );
    private static readonly QualifiedResourceName _nonRootOnlyResource = new(
        "Ed-Fi",
        "StudentAddressCollection"
    );
    private static readonly QualifiedResourceName _keyUnificationResource = new("Ed-Fi", "Program");

    [TestCase(SqlDialect.Pgsql)]
    [TestCase(SqlDialect.Mssql)]
    public void It_should_derive_expected_supported_vs_unsupported_classifications(SqlDialect dialect)
    {
        var modelSet = ThinSliceFixtureModelSetBuilder.Build(FixturePath, dialect);
        var resourcesByName = modelSet.ConcreteResourcesInNameOrder.ToDictionary(resource =>
            resource.ResourceKey.Resource
        );

        resourcesByName.Should().ContainKey(_studentResource);
        resourcesByName.Should().ContainKey(_schoolResource);
        resourcesByName.Should().ContainKey(_descriptorResource);
        resourcesByName.Should().ContainKey(_nonRootOnlyResource);
        resourcesByName.Should().ContainKey(_keyUnificationResource);

        var studentModel = resourcesByName[_studentResource].RelationalModel;
        studentModel.StorageKind.Should().Be(ResourceStorageKind.RelationalTables);
        studentModel.TablesInDependencyOrder.Should().HaveCount(1);
        studentModel.DocumentReferenceBindings.Should().NotBeEmpty();
        studentModel.Root.Columns.Should().Contain(column => column.Kind == ColumnKind.DescriptorFk);

        var schoolModel = resourcesByName[_schoolResource].RelationalModel;
        schoolModel.StorageKind.Should().Be(ResourceStorageKind.RelationalTables);
        schoolModel.TablesInDependencyOrder.Should().HaveCount(1);

        var descriptorModel = resourcesByName[_descriptorResource];
        descriptorModel.StorageKind.Should().Be(ResourceStorageKind.SharedDescriptorTable);
        descriptorModel.RelationalModel.TablesInDependencyOrder.Should().HaveCount(1);

        var nonRootOnlyModel = resourcesByName[_nonRootOnlyResource].RelationalModel;
        nonRootOnlyModel.StorageKind.Should().Be(ResourceStorageKind.RelationalTables);
        nonRootOnlyModel.TablesInDependencyOrder.Count.Should().BeGreaterThan(1);

        var keyUnificationModel = resourcesByName[_keyUnificationResource].RelationalModel;
        keyUnificationModel.StorageKind.Should().Be(ResourceStorageKind.RelationalTables);
        keyUnificationModel.TablesInDependencyOrder.Should().HaveCount(1);

        var hasKeyUnificationClasses = keyUnificationModel.Root.KeyUnificationClasses.Count > 0;
        var hasSourceLessStoredNonKey = HasSourceLessStoredNonKeyColumn(keyUnificationModel.Root);
        (hasKeyUnificationClasses || hasSourceLessStoredNonKey).Should().BeTrue();
    }

    private static bool HasSourceLessStoredNonKeyColumn(DbTableModel table)
    {
        var keyColumns = table.Key.Columns.Select(keyColumn => keyColumn.ColumnName).ToHashSet();

        return table.Columns.Any(column =>
            !keyColumns.Contains(column.ColumnName)
            && column.Storage is ColumnStorage.Stored
            && column.SourceJsonPath is null
        );
    }
}
