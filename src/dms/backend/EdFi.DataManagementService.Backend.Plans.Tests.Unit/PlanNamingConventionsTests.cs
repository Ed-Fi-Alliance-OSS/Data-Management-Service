// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Backend.Plans;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
public class Given_PlanNamingConventions
{
    [TestCase("SchoolYear", "schoolYear")]
    [TestCase("Student_StudentUniqueId", "student_StudentUniqueId")]
    [TestCase("_DocumentId", "_DocumentId")]
    public void It_should_derive_write_parameter_base_names_with_first_character_camel_transform(
        string columnName,
        string expected
    )
    {
        var baseName = PlanNamingConventions.DeriveWriteParameterBaseName(new DbColumnName(columnName));

        baseName.Should().Be(expected);
    }

    [TestCase("school-id", "school_id")]
    [TestCase("school---id", "school_id")]
    [TestCase("1school", "_1school")]
    [TestCase("", "p")]
    [TestCase("  ", "_")]
    [TestCase("abc__def", "abc_def")]
    public void It_should_sanitize_candidate_parameter_names_to_safe_sql_identifiers(
        string candidateName,
        string expected
    )
    {
        var sanitized = PlanNamingConventions.SanitizeBareParameterName(candidateName);

        sanitized.Should().Be(expected);
        var validate = () =>
            PlanSqlWriterExtensions.ValidateBareParameterName(sanitized, nameof(candidateName));
        validate.Should().NotThrow();
    }

    [Test]
    public void It_should_deduplicate_case_insensitive_parameter_collisions_in_authoritative_order()
    {
        var deduplicated = PlanNamingConventions.DeduplicateCaseInsensitive([
            "schoolId",
            "SchoolId",
            "SCHOOLID",
            "schoolYear",
            "schoolid",
        ]);

        deduplicated.Should().Equal("schoolId", "SchoolId_2", "SCHOOLID_3", "schoolYear", "schoolid_4");
    }

    [Test]
    public void It_should_bump_suffixes_until_unused_when_generated_names_collide_with_later_inputs()
    {
        var deduplicated = PlanNamingConventions.DeduplicateCaseInsensitive(["a", "a", "a_2"]);

        deduplicated.Should().Equal("a", "a_2", "a_3");
    }

    [Test]
    public void It_should_bump_suffixes_deterministically_for_case_variant_suffix_collisions()
    {
        var deduplicated = PlanNamingConventions.DeduplicateCaseInsensitive(["a", "A", "a_2", "A_2"]);

        deduplicated.Should().Equal("a", "A_2", "a_3", "A_4");
    }

    [Test]
    public void It_should_derive_and_deduplicate_write_parameter_names_from_ordered_columns()
    {
        var parameterNames = PlanNamingConventions.DeriveWriteParameterNamesInOrder([
            new DbColumnName("SchoolId"),
            new DbColumnName("schoolId"),
            new DbColumnName("School-ID"),
            new DbColumnName("School__ID"),
        ]);

        parameterNames.Should().Equal("schoolId", "schoolId_2", "school_ID", "school_ID_2");
    }

    [Test]
    public void It_should_deterministically_sanitize_and_deduplicate_across_repeated_invocations()
    {
        IReadOnlyList<DbColumnName> columns =
        [
            new DbColumnName("School-ID"),
            new DbColumnName("school-id"),
            new DbColumnName("School__ID"),
            new DbColumnName("1School Name"),
            new DbColumnName("1school name"),
        ];

        var first = PlanNamingConventions.DeriveWriteParameterNamesInOrder(columns);
        var second = PlanNamingConventions.DeriveWriteParameterNamesInOrder(columns);

        second.Should().Equal(first);
        second.Should().Equal("school_ID", "school_id_2", "school_ID_3", "_1School_Name", "_1school_name_2");
    }

    [Test]
    public void It_should_deduplicate_suffixes_identically_under_non_default_culture()
    {
        IReadOnlyList<string> names = ["a", "a", "a_2", "A", "a_4", "a"];

        var defaultCultureResult = DeduplicateWithCurrentCulture(CultureInfo.InvariantCulture, names);
        var nonDefaultCultureResult = DeduplicateWithCurrentCulture(
            CultureInfo.GetCultureInfo("tr-TR"),
            names
        );

        nonDefaultCultureResult.Should().Equal(defaultCultureResult);
        nonDefaultCultureResult.Should().Equal("a", "a_2", "a_3", "A_4", "a_5", "a_6");
    }

    [Test]
    public void It_should_return_deterministic_fixed_aliases_by_role()
    {
        PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Root).Should().Be("r");
        PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Keyset).Should().Be("k");
        PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Table).Should().Be("t");
        PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Document).Should().Be("doc");
        PlanNamingConventions.GetFixedAlias(PlanSqlAliasRole.Descriptor).Should().Be("d");
    }

    private static IReadOnlyList<string> DeduplicateWithCurrentCulture(
        CultureInfo culture,
        IReadOnlyList<string> names
    )
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;

            return PlanNamingConventions.DeduplicateCaseInsensitive(names);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }
}

[TestFixture]
public class Given_PlanSqlTableAliasAllocator
{
    private PlanSqlTableAliasAllocator _allocator = null!;

    [SetUp]
    public void Setup()
    {
        _allocator = PlanNamingConventions.CreateTableAliasAllocator();
    }

    [Test]
    public void It_should_allocate_deterministic_table_aliases()
    {
        _allocator.AllocateNext().Should().Be("t0");
        _allocator.AllocateNext().Should().Be("t1");
        _allocator.AllocateNext().Should().Be("t2");
    }

    [Test]
    public void It_should_restart_allocation_for_a_new_allocator_instance()
    {
        _allocator.AllocateNext().Should().Be("t0");

        var newAllocator = PlanNamingConventions.CreateTableAliasAllocator();

        newAllocator.AllocateNext().Should().Be("t0");
    }
}
