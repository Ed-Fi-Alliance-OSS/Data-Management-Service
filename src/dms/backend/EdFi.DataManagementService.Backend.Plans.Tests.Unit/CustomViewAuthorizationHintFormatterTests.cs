// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Plans.Tests.Unit;

[TestFixture]
[Parallelizable]
public class Given_CustomViewAuthorizationHintFormatter
{
    [Test]
    public void It_should_format_the_hint_documented_in_auth_md()
    {
        // auth.md §"Authorization Failure Hints" gives this exact pairing as the worked example.
        CustomViewAuthorizationHintFormatter
            .Format("StudentWithCTECourseEnrollments")
            .Should()
            .Be("You may need a Student with CTE Course Enrollments.");
    }

    [TestCase("StudentWithCTECourseEnrollments", "Student with CTE Course Enrollments")]
    [TestCase("TransportationTypeDescriptorWithABus", "Transportation Type Descriptor with A Bus")]
    [TestCase(
        "EducationOrganizationWithACategoryContainingAnSWord",
        "Education Organization with A Category Containing An S Word"
    )]
    [TestCase("SchoolContainingAnSWord", "School Containing An S Word")]
    [TestCase("StudentWithGrade3Courses", "Student with Grade3 Courses")]
    public void It_should_split_camel_case_while_keeping_acronym_runs_intact(
        string strategyName,
        string expectedDisplayText
    )
    {
        CustomViewAuthorizationHintFormatter.FormatDisplayText(strategyName).Should().Be(expectedDisplayText);
    }

    [Test]
    public void It_should_lowercase_every_With_token_so_a_description_containing_With_still_reads_as_prose()
    {
        CustomViewAuthorizationHintFormatter
            .FormatDisplayText("StudentWithCoursesWithGrades")
            .Should()
            .Be("Student with Courses with Grades");
    }

    [TestCase("StudentWithCTECourseEnrollments", "a")]
    [TestCase("SchoolContainingAnSWord", "a")]
    [TestCase("EducationOrganizationWithACategory", "an")]
    [TestCase("InterventionWithABudget", "an")]
    [TestCase("ObjectiveAssessmentWithAScore", "an")]
    [TestCase("AssessmentWithAnItem", "an")]
    [TestCase("UniversityWithAProgram", "an")]
    public void It_should_select_the_article_from_the_display_texts_leading_vowel(
        string strategyName,
        string expectedArticle
    )
    {
        CustomViewAuthorizationHintFormatter
            .Format(strategyName)
            .Should()
            .StartWith($"You may need {expectedArticle} ");
    }

    [Test]
    public void It_should_keep_a_leading_acronym_run_as_one_word()
    {
        CustomViewAuthorizationHintFormatter
            .FormatDisplayText("CTEProgramWithAnEnrollment")
            .Should()
            .Be("CTE Program with An Enrollment");
    }

    [Test]
    public void It_should_format_a_name_that_does_not_carry_the_With_separator()
    {
        // Not reachable for a resolved custom view — the convention requires 'With' — but the formatter
        // must not fail on one, because the strategy name is CMS-supplied text.
        CustomViewAuthorizationHintFormatter
            .FormatDisplayText("StudentEnrollments")
            .Should()
            .Be("Student Enrollments");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void It_should_reject_a_missing_strategy_name(string? strategyName)
    {
        var act = () => CustomViewAuthorizationHintFormatter.Format(strategyName!);

        act.Should().Throw<ArgumentException>().WithParameterName("strategyName");
    }
}
