// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.RelationalModel.Tests.Unit;

/// <summary>
/// Helpers that build and inspect derived model sets for identity-collation derivation tests.
/// </summary>
internal static class IdentityCollationTestHelpers
{
    internal static DerivedRelationalModelSet BuildSet(
        ISqlDialectRules dialectRules,
        JsonObject coreProjectSchema,
        JsonObject? extensionProjectSchema = null
    )
    {
        List<EffectiveProjectSchema> projects =
        [
            EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
                coreProjectSchema,
                isExtensionProject: false
            ),
        ];

        if (extensionProjectSchema is not null)
        {
            projects.Add(
                EffectiveSchemaSetFixtureBuilder.CreateEffectiveProjectSchema(
                    extensionProjectSchema,
                    isExtensionProject: true
                )
            );
        }

        var schemaSet = EffectiveSchemaSetFixtureBuilder.CreateEffectiveSchemaSet(projects);
        var builder = new DerivedRelationalModelSetBuilder(RelationalModelSetPasses.CreateDefault());

        return builder.Build(schemaSet, dialectRules.Dialect, dialectRules);
    }

    internal static DbColumnModel Column(
        DerivedRelationalModelSet set,
        string schema,
        string tableName,
        string columnName
    )
    {
        return set
            .ConcreteResourcesInNameOrder.SelectMany(resource =>
                resource.RelationalModel.TablesInDependencyOrder
            )
            .Concat(set.AbstractIdentityTablesInNameOrder.Select(table => table.TableModel))
            .Single(table => table.Table.Schema.Value == schema && table.Table.Name == tableName)
            .Columns.Single(column => column.ColumnName.Value == columnName);
    }

    internal static TrackedChangeColumnInfo TrackedColumn(
        DerivedRelationalModelSet set,
        string tableName,
        string oldColumnName
    )
    {
        return set
            .TrackedChangeTablesInNameOrder.Single(table => table.Table.Name == tableName)
            .ValueColumnsInTableOrder.Single(column => column.OldColumnName.Value == oldColumnName);
    }

    internal static IEnumerable<DbColumnModel> AllColumns(DerivedRelationalModelSet set)
    {
        return set
            .ConcreteResourcesInNameOrder.SelectMany(resource =>
                resource.RelationalModel.TablesInDependencyOrder
            )
            .Concat(set.AbstractIdentityTablesInNameOrder.Select(table => table.TableModel))
            .SelectMany(table => table.Columns);
    }
}

/// <summary>
/// Test fixture for identity-text role derivation under the SQL Server identity-equality contract.
/// </summary>
[TestFixture]
public class Given_ApplySqlServerIdentityCollationPass_With_Mssql_Rules
{
    private DerivedRelationalModelSet _set = default!;

    [SetUp]
    public void Setup()
    {
        _set = IdentityCollationTestHelpers.BuildSet(
            new MssqlDialectRules(),
            IdentityCollationTestSchemaBuilder.BuildCoreProjectSchema(),
            IdentityCollationTestSchemaBuilder.BuildExtensionProjectSchema()
        );
    }

    private bool Flag(string tableName, string columnName, string schema = "edfi") =>
        IdentityCollationTestHelpers
            .Column(_set, schema, tableName, columnName)
            .UsesSqlServerIdentityCollation;

    [Test]
    public void It_flags_a_root_string_identity_column()
    {
        Flag("School", "SchoolCode").Should().BeTrue();
    }

    [Test]
    public void It_does_not_flag_a_root_non_identity_string_column()
    {
        Flag("School", "NameOfInstitution").Should().BeFalse();
    }

    [Test]
    public void It_does_not_flag_a_non_string_identity_column()
    {
        Flag("Offering", "OfferingNumber").Should().BeFalse();
    }

    [Test]
    public void It_flags_a_reference_identity_copy_on_the_root()
    {
        Flag("Enrollment", "School_SchoolCode").Should().BeTrue();
    }

    [Test]
    public void It_flags_a_reference_identity_copy_on_a_collection()
    {
        Flag("OfferingPartner", "PartnerSchool_SchoolCode").Should().BeTrue();
    }

    [Test]
    public void It_flags_a_reference_identity_copy_on_an_extension_table()
    {
        Flag("OfferingExtension", "SponsorSchool_SchoolCode", schema: "sample").Should().BeTrue();
    }

    [Test]
    public void It_does_not_flag_a_reference_document_id()
    {
        Flag("Enrollment", "School_DocumentId").Should().BeFalse();
    }

    [Test]
    public void It_flags_a_key_unified_identity_canonical_column_and_its_aliases()
    {
        var alias = IdentityCollationTestHelpers.Column(_set, "edfi", "Offering", "PrimarySchool_SchoolCode");
        var canonicalName = alias
            .Storage.Should()
            .BeOfType<ColumnStorage.UnifiedAlias>()
            .Subject.CanonicalColumn;

        alias.UsesSqlServerIdentityCollation.Should().BeTrue();
        Flag("Offering", "SecondarySchool_SchoolCode").Should().BeTrue();
        Flag("Offering", canonicalName.Value).Should().BeTrue();
    }

    [Test]
    public void It_does_not_flag_a_key_unified_class_with_no_identity_member()
    {
        var alias = IdentityCollationTestHelpers.Column(_set, "edfi", "School", "LocalName");
        var canonicalName = alias
            .Storage.Should()
            .BeOfType<ColumnStorage.UnifiedAlias>()
            .Subject.CanonicalColumn;

        alias.UsesSqlServerIdentityCollation.Should().BeFalse();
        Flag("School", "LegalName").Should().BeFalse();
        Flag("School", canonicalName.Value).Should().BeFalse();
    }

    [Test]
    public void It_flags_a_collection_semantic_identity_string_member()
    {
        Flag("OfferingAddress", "City").Should().BeTrue();
    }

    [Test]
    public void It_does_not_flag_a_collection_non_identity_string()
    {
        Flag("OfferingAddress", "Street").Should().BeFalse();
    }

    [Test]
    public void It_does_not_flag_an_extension_non_identity_string()
    {
        Flag("OfferingExtension", "SponsorNote", schema: "sample").Should().BeFalse();
    }

    [Test]
    public void It_flags_exactly_the_descriptor_identity_text_columns()
    {
        var descriptorRoot = _set
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.StorageKind == ResourceStorageKind.SharedDescriptorTable
            )
            .RelationalModel.Root;

        descriptorRoot
            .Columns.Where(column => column.UsesSqlServerIdentityCollation)
            .Select(column => column.ColumnName)
            .Should()
            .BeEquivalentTo(DescriptorIdentityTextColumns.All);
    }

    [Test]
    public void It_does_not_flag_non_identity_descriptor_columns()
    {
        var descriptorRoot = _set
            .ConcreteResourcesInNameOrder.Single(resource =>
                resource.StorageKind == ResourceStorageKind.SharedDescriptorTable
            )
            .RelationalModel.Root;

        descriptorRoot
            .Columns.Where(column => !DescriptorIdentityTextColumns.All.Contains(column.ColumnName))
            .Should()
            .NotBeEmpty()
            .And.OnlyContain(column => !column.UsesSqlServerIdentityCollation);
    }
}

/// <summary>
/// Test fixture for abstract identity table columns under the SQL Server identity-equality contract.
/// </summary>
[TestFixture]
public class Given_ApplySqlServerIdentityCollationPass_With_An_Abstract_Identity_Table
{
    private DerivedRelationalModelSet _set = default!;

    [SetUp]
    public void Setup()
    {
        _set = IdentityCollationTestHelpers.BuildSet(
            new MssqlDialectRules(),
            AbstractIdentityTableTestSchemaBuilder.BuildProjectSchema(mismatchMemberType: false)
        );
    }

    [Test]
    public void It_flags_an_abstract_identity_string_column()
    {
        IdentityCollationTestHelpers
            .Column(_set, "edfi", "EducationOrganizationIdentity", "OrganizationName")
            .UsesSqlServerIdentityCollation.Should()
            .BeTrue();
    }

    [Test]
    public void It_does_not_flag_the_abstract_discriminator()
    {
        IdentityCollationTestHelpers
            .Column(_set, "edfi", "EducationOrganizationIdentity", "Discriminator")
            .UsesSqlServerIdentityCollation.Should()
            .BeFalse();
    }
}

/// <summary>
/// Test fixture proving the PostgreSQL contract leaves every column unflagged.
/// </summary>
[TestFixture]
public class Given_ApplySqlServerIdentityCollationPass_With_Pgsql_Rules
{
    private DerivedRelationalModelSet _set = default!;
    private DerivedRelationalModelSet _abstractSet = default!;

    [SetUp]
    public void Setup()
    {
        _set = IdentityCollationTestHelpers.BuildSet(
            new PgsqlDialectRules(),
            IdentityCollationTestSchemaBuilder.BuildCoreProjectSchema(),
            IdentityCollationTestSchemaBuilder.BuildExtensionProjectSchema()
        );
        _abstractSet = IdentityCollationTestHelpers.BuildSet(
            new PgsqlDialectRules(),
            AbstractIdentityTableTestSchemaBuilder.BuildProjectSchema(mismatchMemberType: false)
        );
    }

    [Test]
    public void It_flags_no_column()
    {
        IdentityCollationTestHelpers
            .AllColumns(_set)
            .Concat(IdentityCollationTestHelpers.AllColumns(_abstractSet))
            .Should()
            .OnlyContain(column => !column.UsesSqlServerIdentityCollation);
    }

    [Test]
    public void It_flags_no_tracked_change_column()
    {
        _set.TrackedChangeTablesInNameOrder.Concat(_abstractSet.TrackedChangeTablesInNameOrder)
            .SelectMany(table => table.ValueColumnsInTableOrder)
            .Should()
            .OnlyContain(column => !column.UsesSqlServerIdentityCollation);
    }

    [Test]
    public void It_leaves_the_comparer_ordinal_for_an_identity_column()
    {
        var identityColumn = IdentityCollationTestHelpers.Column(_set, "edfi", "School", "SchoolCode");

        IdentityEqualityContract.ComparerFor(identityColumn).Equals("ABC", "abc").Should().BeFalse();
    }
}

/// <summary>
/// Test fixture for tracked-change identity copies under the SQL Server identity-equality contract.
/// </summary>
[TestFixture]
public class Given_DeriveTrackedChangeInventoryPass_With_The_Mssql_Identity_Collation
{
    private DerivedRelationalModelSet _set = default!;
    private DerivedRelationalModelSet _personSet = default!;

    [SetUp]
    public void Setup()
    {
        _set = IdentityCollationTestHelpers.BuildSet(
            new MssqlDialectRules(),
            IdentityCollationTestSchemaBuilder.BuildCoreProjectSchema(),
            IdentityCollationTestSchemaBuilder.BuildExtensionProjectSchema()
        );
        _personSet = IdentityCollationTestHelpers.BuildSet(
            new MssqlDialectRules(),
            TransitivePersonSecurableSchemaBuilder.BuildProjectSchema()
        );
    }

    private TrackedChangeColumnInfo Tracked(string tableName, string oldColumnName) =>
        IdentityCollationTestHelpers.TrackedColumn(_set, tableName, oldColumnName);

    [Test]
    public void It_flags_an_identity_string_value_column()
    {
        var column = Tracked("School", "OldSchoolCode");

        column.Origin.Should().Be(TrackedChangeColumnOrigin.Identity);
        column.UsesSqlServerIdentityCollation.Should().BeTrue();
    }

    [Test]
    public void It_flags_an_identity_and_securable_element_string_value_column()
    {
        var column = Tracked("Enrollment", "OldSchool_SchoolCode");

        column
            .Origin.Should()
            .Be(TrackedChangeColumnOrigin.Identity | TrackedChangeColumnOrigin.SecurableElement);
        column.UsesSqlServerIdentityCollation.Should().BeTrue();
    }

    [Test]
    public void It_flags_descriptor_namespace_and_code_value_projections()
    {
        Tracked("Enrollment", "OldGradeLevelDescriptor_Namespace")
            .UsesSqlServerIdentityCollation.Should()
            .BeTrue();
        Tracked("Enrollment", "OldGradeLevelDescriptor_CodeValue")
            .UsesSqlServerIdentityCollation.Should()
            .BeTrue();
    }

    [Test]
    public void It_does_not_flag_a_securable_element_only_string_value_column()
    {
        var column = Tracked("Enrollment", "OldNamespace");

        column.Origin.Should().Be(TrackedChangeColumnOrigin.SecurableElement);
        column.UsesSqlServerIdentityCollation.Should().BeFalse();
    }

    [Test]
    public void It_does_not_flag_a_non_string_identity_value_column()
    {
        var column = Tracked("Offering", "OldOfferingNumber");

        column.Origin.Should().HaveFlag(TrackedChangeColumnOrigin.Identity);
        column.UsesSqlServerIdentityCollation.Should().BeFalse();
    }

    [Test]
    public void It_does_not_flag_a_person_document_id_value_column()
    {
        var personColumns = _personSet
            .TrackedChangeTablesInNameOrder.SelectMany(table => table.ValueColumnsInTableOrder)
            .Where(column => column.Role == TrackedChangeColumnRole.PersonDocumentId)
            .ToArray();

        personColumns.Should().NotBeEmpty();
        personColumns.Should().OnlyContain(column => !column.UsesSqlServerIdentityCollation);
    }

    [Test]
    public void It_flags_the_shared_descriptor_namespace_and_code_value()
    {
        Tracked("Descriptor", "OldNamespace").UsesSqlServerIdentityCollation.Should().BeTrue();
        Tracked("Descriptor", "OldCodeValue").UsesSqlServerIdentityCollation.Should().BeTrue();
    }
}

/// <summary>
/// Test fixture proving dialect identifier shortening keeps the identity-text role.
/// </summary>
[TestFixture]
public class Given_ApplyDialectIdentifierShorteningPass_With_A_Flagged_Long_Identity_Column
{
    private static readonly string _longPropertyName = "longIdentity" + new string('x', 140);
    private DerivedRelationalModelSet _set = default!;
    private string _expectedColumnName = default!;
    private string _expectedTrackedColumnName = default!;

    [SetUp]
    public void Setup()
    {
        var rules = new MssqlDialectRules();
        var physicalName = char.ToUpperInvariant(_longPropertyName[0]) + _longPropertyName[1..];

        _expectedColumnName = rules.ShortenIdentifier(physicalName);
        _expectedTrackedColumnName = rules.ShortenIdentifier("Old" + physicalName);
        _set = IdentityCollationTestHelpers.BuildSet(
            rules,
            IdentityCollationTestSchemaBuilder.BuildLongIdentityProjectSchema(_longPropertyName)
        );
    }

    [Test]
    public void It_keeps_the_flag_on_the_shortened_column()
    {
        _expectedColumnName.Length.Should().BeLessThan(_longPropertyName.Length);
        IdentityCollationTestHelpers
            .Column(_set, "edfi", "LongIdentity", _expectedColumnName)
            .UsesSqlServerIdentityCollation.Should()
            .BeTrue();
    }

    [Test]
    public void It_keeps_the_flag_on_the_shortened_tracked_change_column()
    {
        IdentityCollationTestHelpers
            .TrackedColumn(_set, "LongIdentity", _expectedTrackedColumnName)
            .UsesSqlServerIdentityCollation.Should()
            .BeTrue();
    }
}

/// <summary>
/// Builds focused project schemas that cover every identity-text column category.
/// </summary>
internal static class IdentityCollationTestSchemaBuilder
{
    internal static JsonObject BuildCoreProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["enrollments"] = BuildEnrollmentSchema(),
                ["gradeLevelDescriptors"] = CommonInventoryTestSchemaBuilder.BuildDescriptorSchema(),
                ["offerings"] = BuildOfferingSchema(),
                ["schools"] = BuildSchoolSchema(),
            },
        };
    }

    internal static JsonObject BuildExtensionProjectSchema()
    {
        return new JsonObject
        {
            ["projectName"] = "Sample",
            ["projectEndpointName"] = "sample",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject { ["offerings"] = BuildOfferingExtensionSchema() },
        };
    }

    internal static JsonObject BuildLongIdentityProjectSchema(string propertyName)
    {
        return new JsonObject
        {
            ["projectName"] = "Ed-Fi",
            ["projectEndpointName"] = "ed-fi",
            ["projectVersion"] = "1.0.0",
            ["resourceSchemas"] = new JsonObject
            {
                ["longIdentities"] = Resource(
                    "LongIdentity",
                    identityJsonPaths: [$"$.{propertyName}"],
                    documentPathsMapping: new JsonObject
                    {
                        ["LongIdentity"] = Scalar($"$.{propertyName}", isPartOfIdentity: true),
                    },
                    properties: new JsonObject { [propertyName] = String(20) },
                    required: [propertyName]
                ),
            },
        };
    }

    /// <summary>
    /// School: string identity, a non-identity string, and an identity-free key-unified string class.
    /// </summary>
    private static JsonObject BuildSchoolSchema()
    {
        var school = Resource(
            "School",
            identityJsonPaths: ["$.schoolCode"],
            documentPathsMapping: new JsonObject
            {
                ["SchoolCode"] = Scalar("$.schoolCode", isPartOfIdentity: true),
                ["NameOfInstitution"] = Scalar("$.nameOfInstitution"),
                ["LocalName"] = Scalar("$.localName"),
                ["LegalName"] = Scalar("$.legalName"),
            },
            properties: new JsonObject
            {
                ["schoolCode"] = String(20),
                ["nameOfInstitution"] = String(75),
                ["localName"] = String(75),
                ["legalName"] = String(75),
            },
            required: ["schoolCode", "nameOfInstitution"]
        );
        school["equalityConstraints"] = new JsonArray
        {
            new JsonObject { ["sourceJsonPath"] = "$.localName", ["targetJsonPath"] = "$.legalName" },
        };

        return school;
    }

    /// <summary>
    /// Offering: a string + integer identity, two key-unified identity references to School, a collection
    /// with a string semantic identity member, and a collection holding a School reference.
    /// </summary>
    private static JsonObject BuildOfferingSchema()
    {
        var offering = Resource(
            "Offering",
            identityJsonPaths:
            [
                "$.offeringName",
                "$.offeringNumber",
                "$.primarySchoolReference.schoolCode",
                "$.secondarySchoolReference.schoolCode",
            ],
            documentPathsMapping: new JsonObject
            {
                ["OfferingName"] = Scalar("$.offeringName", isPartOfIdentity: true),
                ["OfferingNumber"] = Scalar("$.offeringNumber", isPartOfIdentity: true),
                ["PrimarySchool"] = SchoolReference("$.primarySchoolReference.schoolCode", isRequired: true),
                ["SecondarySchool"] = SchoolReference(
                    "$.secondarySchoolReference.schoolCode",
                    isRequired: true
                ),
                ["PartnerSchool"] = SchoolReference(
                    "$.partners[*].partnerSchoolReference.schoolCode",
                    isRequired: true
                ),
                ["City"] = Scalar("$.addresses[*].city"),
                ["Street"] = Scalar("$.addresses[*].street"),
            },
            properties: new JsonObject
            {
                ["offeringName"] = String(30),
                ["offeringNumber"] = new JsonObject { ["type"] = "integer" },
                ["primarySchoolReference"] = SchoolReferenceSchema(),
                ["secondarySchoolReference"] = SchoolReferenceSchema(),
                ["addresses"] = Array(
                    new JsonObject { ["city"] = String(30), ["street"] = String(50) },
                    required: ["city"]
                ),
                ["partners"] = Array(
                    new JsonObject { ["partnerSchoolReference"] = SchoolReferenceSchema() },
                    required: ["partnerSchoolReference"]
                ),
            },
            required: ["offeringName", "offeringNumber", "primarySchoolReference", "secondarySchoolReference"]
        );
        offering["equalityConstraints"] = new JsonArray
        {
            new JsonObject
            {
                ["sourceJsonPath"] = "$.primarySchoolReference.schoolCode",
                ["targetJsonPath"] = "$.secondarySchoolReference.schoolCode",
            },
        };
        offering["arrayUniquenessConstraints"] = new JsonArray
        {
            new JsonObject { ["paths"] = new JsonArray { "$.addresses[*].city" } },
            new JsonObject
            {
                ["paths"] = new JsonArray { "$.partners[*].partnerSchoolReference.schoolCode" },
            },
        };

        return offering;
    }

    /// <summary>
    /// Enrollment: a School reference whose identity copy is also a securable element, a securable-only
    /// namespace string, and a descriptor reference that is part of the identity.
    /// </summary>
    private static JsonObject BuildEnrollmentSchema()
    {
        var enrollment = Resource(
            "Enrollment",
            identityJsonPaths: ["$.schoolReference.schoolCode", "$.gradeLevelDescriptor"],
            documentPathsMapping: new JsonObject
            {
                ["School"] = SchoolReference("$.schoolReference.schoolCode", isRequired: true),
                ["GradeLevelDescriptor"] = new JsonObject
                {
                    ["isReference"] = true,
                    ["isDescriptor"] = true,
                    ["isPartOfIdentity"] = true,
                    ["isRequired"] = true,
                    ["projectName"] = "Ed-Fi",
                    ["resourceName"] = "GradeLevelDescriptor",
                    ["path"] = "$.gradeLevelDescriptor",
                },
                ["Namespace"] = Scalar("$.namespace"),
            },
            properties: new JsonObject
            {
                ["schoolReference"] = SchoolReferenceSchema(),
                ["gradeLevelDescriptor"] = String(306),
                ["namespace"] = String(255),
            },
            required: ["schoolReference", "gradeLevelDescriptor"]
        );
        // Namespace securable paths become tracked value columns: the School identity copy carries both
        // origins, and the plain namespace string is securable-only.
        enrollment["securableElements"] = new JsonObject
        {
            ["Namespace"] = new JsonArray { "$.schoolReference.schoolCode", "$.namespace" },
        };

        return enrollment;
    }

    /// <summary>
    /// Offering extension: a root-level School reference and a non-identity string.
    /// </summary>
    private static JsonObject BuildOfferingExtensionSchema()
    {
        var extension = Resource(
            "Offering",
            identityJsonPaths: [],
            documentPathsMapping: new JsonObject
            {
                ["SponsorSchool"] = SchoolReference(
                    "$._ext.sample.sponsorReference.schoolCode",
                    isRequired: false
                ),
                ["SponsorNote"] = Scalar("$._ext.sample.sponsorNote"),
            },
            properties: new JsonObject
            {
                ["_ext"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["sample"] = new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["sponsorNote"] = String(50),
                                ["sponsorReference"] = SchoolReferenceSchema(),
                            },
                        },
                    },
                },
            },
            required: []
        );
        extension["isResourceExtension"] = true;

        return extension;
    }

    private static JsonObject Resource(
        string resourceName,
        string[] identityJsonPaths,
        JsonObject documentPathsMapping,
        JsonObject properties,
        string[] required
    )
    {
        return new JsonObject
        {
            ["resourceName"] = resourceName,
            ["isDescriptor"] = false,
            ["isResourceExtension"] = false,
            ["isSubclass"] = false,
            ["allowIdentityUpdates"] = false,
            ["arrayUniquenessConstraints"] = new JsonArray(),
            ["decimalPropertyValidationInfos"] = new JsonArray(),
            ["identityJsonPaths"] = new JsonArray(identityJsonPaths.Select(path => (JsonNode)path).ToArray()),
            ["documentPathsMapping"] = documentPathsMapping,
            ["jsonSchemaForInsert"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(name => (JsonNode)name).ToArray()),
            },
        };
    }

    private static JsonObject Scalar(string path, bool isPartOfIdentity = false)
    {
        return new JsonObject
        {
            ["isReference"] = false,
            ["isDescriptor"] = false,
            ["isPartOfIdentity"] = isPartOfIdentity,
            ["isRequired"] = isPartOfIdentity,
            ["path"] = path,
        };
    }

    private static JsonObject SchoolReference(string referenceJsonPath, bool isRequired)
    {
        return new JsonObject
        {
            ["isReference"] = true,
            ["isDescriptor"] = false,
            ["isRequired"] = isRequired,
            ["projectName"] = "Ed-Fi",
            ["resourceName"] = "School",
            ["referenceJsonPaths"] = new JsonArray
            {
                new JsonObject
                {
                    ["identityJsonPath"] = "$.schoolCode",
                    ["referenceJsonPath"] = referenceJsonPath,
                },
            },
        };
    }

    private static JsonObject SchoolReferenceSchema()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject { ["schoolCode"] = String(20) },
            ["required"] = new JsonArray("schoolCode"),
        };
    }

    private static JsonObject Array(JsonObject itemProperties, string[] required)
    {
        return new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = itemProperties,
                ["required"] = new JsonArray(required.Select(name => (JsonNode)name).ToArray()),
            },
        };
    }

    private static JsonObject String(int maxLength)
    {
        return new JsonObject { ["type"] = "string", ["maxLength"] = maxLength };
    }
}
