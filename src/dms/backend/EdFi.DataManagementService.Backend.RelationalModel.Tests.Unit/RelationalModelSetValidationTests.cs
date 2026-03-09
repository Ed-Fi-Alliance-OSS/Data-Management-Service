// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.RelationalModel;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

[TestFixture]
public class Given_RelationalModelSetValidation_With_An_Extension_Project_Resource_Key_Universe
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = RelationalModelSetValidationFixture.CreateExtensionAwareEffectiveSchemaSet([
            EffectiveSchemaFixture.CreateResourceKey(
                1,
                "Ed-Fi",
                "EducationOrganization",
                isAbstractResource: true
            ),
            EffectiveSchemaFixture.CreateResourceKey(2, "Ed-Fi", "School"),
            EffectiveSchemaFixture.CreateResourceKey(3, "Sample", "BusRoute"),
        ]);

        var builder = new DerivedRelationalModelSetBuilder(Array.Empty<IRelationalModelSetPass>());

        try
        {
            builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_allow_missing_keys_for_only_resource_extension_overlays()
    {
        _exception.Should().BeNull();
    }
}

[TestFixture]
public class Given_RelationalModelSetValidation_With_A_Missing_Non_Extension_Extension_Project_Resource_Key
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = RelationalModelSetValidationFixture.CreateExtensionAwareEffectiveSchemaSet([
            EffectiveSchemaFixture.CreateResourceKey(
                1,
                "Ed-Fi",
                "EducationOrganization",
                isAbstractResource: true
            ),
            EffectiveSchemaFixture.CreateResourceKey(2, "Ed-Fi", "School"),
        ]);

        var builder = new DerivedRelationalModelSetBuilder(Array.Empty<IRelationalModelSetPass>());

        try
        {
            builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_fail_with_the_missing_non_extension_extension_project_resource()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Missing resource keys");
        _exception.Message.Should().Contain("Sample:BusRoute");
        _exception.Message.Should().NotContain("Sample:School");
    }
}

[TestFixture]
public class Given_RelationalModelSetValidation_With_An_Extra_Unknown_Resource_Key_In_An_Extension_Aware_Schema_Set
{
    private Exception? _exception;

    [SetUp]
    public void Setup()
    {
        var effectiveSchemaSet = RelationalModelSetValidationFixture.CreateExtensionAwareEffectiveSchemaSet([
            EffectiveSchemaFixture.CreateResourceKey(
                1,
                "Ed-Fi",
                "EducationOrganization",
                isAbstractResource: true
            ),
            EffectiveSchemaFixture.CreateResourceKey(2, "Ed-Fi", "School"),
            EffectiveSchemaFixture.CreateResourceKey(3, "Sample", "BusRoute"),
            EffectiveSchemaFixture.CreateResourceKey(4, "Sample", "Ghost"),
        ]);

        var builder = new DerivedRelationalModelSetBuilder(Array.Empty<IRelationalModelSetPass>());

        try
        {
            builder.Build(effectiveSchemaSet, SqlDialect.Pgsql, new PgsqlDialectRules());
        }
        catch (Exception ex)
        {
            _exception = ex;
        }
    }

    [Test]
    public void It_should_preserve_the_unknown_resource_diagnostic()
    {
        _exception.Should().BeOfType<InvalidOperationException>();
        _exception!.Message.Should().Contain("Resource keys reference unknown resources");
        _exception.Message.Should().Contain("Sample:Ghost");
    }
}

internal static class RelationalModelSetValidationFixture
{
    private static readonly SchemaComponentInfo[] SchemaComponents =
    [
        new(
            "ed-fi",
            "Ed-Fi",
            "5.0.0",
            false,
            "edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1edf1"
        ),
        new(
            "sample",
            "Sample",
            "1.0.0",
            true,
            "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd"
        ),
    ];

    public static EffectiveSchemaSet CreateExtensionAwareEffectiveSchemaSet(
        IReadOnlyList<ResourceKeyEntry> resourceKeys
    )
    {
        var coreProjectSchema = EffectiveSchemaFixture.CreateProjectSchema(("schools", "School", false));
        EffectiveSchemaFixture.AddAbstractResources(
            coreProjectSchema,
            ("educationOrganizations", "EducationOrganization")
        );

        var extensionProjectSchema = EffectiveSchemaFixture.CreateProjectSchema(
            ("busRoutes", "BusRoute", false),
            ("schoolExtension", "School", true)
        );

        EffectiveProjectSchema[] projects =
        [
            new("ed-fi", "Ed-Fi", "5.0.0", false, coreProjectSchema),
            new("sample", "Sample", "1.0.0", true, extensionProjectSchema),
        ];

        var effectiveSchemaInfo = new EffectiveSchemaInfo(
            ApiSchemaFormatVersion: "1.0.0",
            RelationalMappingVersion: "1.0.0",
            EffectiveSchemaHash: "edf1edf1",
            ResourceKeyCount: resourceKeys.Count,
            ResourceKeySeedHash: [0x01],
            SchemaComponentsInEndpointOrder: SchemaComponents,
            ResourceKeysInIdOrder: resourceKeys
        );

        return new EffectiveSchemaSet(effectiveSchemaInfo, projects);
    }
}
