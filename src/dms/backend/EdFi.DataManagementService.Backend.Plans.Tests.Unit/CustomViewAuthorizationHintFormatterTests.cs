// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
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
    [TestCase("TransportationTypeDescriptorWithABus", "Transportation Type Descriptor with a Bus")]
    [TestCase(
        "EducationOrganizationWithACategoryContainingAnSWord",
        "Education Organization with a Category Containing an S Word"
    )]
    [TestCase("SchoolContainingAnSWord", "School Containing an S Word")]
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

    [TestCase("SchoolWithAProgramOfTheYear", "School with a Program of the Year")]
    [TestCase("StudentWithAnAbsenceInASemester", "Student with an Absence in a Semester")]
    [TestCase("StaffWithAssignmentsAndTheirSchools", "Staff with Assignments and Their Schools")]
    [TestCase("SectionWithASessionAtAnySchoolOrDistrict", "Section with a Session at Any School or District")]
    public void It_should_lowercase_the_ods_preposition_and_article_list_so_the_hint_matches_the_legacy_api(
        string strategyName,
        string expectedDisplayText
    )
    {
        // ODS's CustomAuthorizationViewHintProvider lowercases every token found in its preposition list
        // (articles, conjunctions, and prepositions), not only the 'With' separator; migrating clients see the
        // same hint text from both APIs.
        CustomViewAuthorizationHintFormatter
            .FormatDisplayText(strategyName)
            .Should()
            .Be(expectedDisplayText);
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
            .Be("CTE Program with an Enrollment");
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

    [Test]
    public void It_should_format_the_change_queries_non_identifying_basis_hint_from_the_ods_wording()
    {
        // change-queries.md §"Design: error behavior": the ODS "Non-identifying properties" text carried over,
        // narrowed by the securable allowance. Verbatim for the Section/Location case.
        CustomViewAuthorizationHintFormatter
            .FormatBasisNotIdentifyingOrSecurable(
                "locationReference",
                new QualifiedResourceName("Ed-Fi", "Section"),
                new QualifiedResourceName("Ed-Fi", "Location")
            )
            .Should()
            .Be(
                "The reference 'locationReference' on 'Ed-Fi.Section' leads to custom view basis 'Ed-Fi.Location' "
                    + "but is neither an identifying property nor a securable element of the subject. This is not "
                    + "supported by Change Queries, which only track deleted/changed values of identifying and "
                    + "securable properties. Should a different authorization strategy be used?"
            );
    }

    [Test]
    public void It_formats_the_descriptor_basis_not_identifying_on_intermediate_hint()
    {
        // change-queries.md §"Error behavior": a descriptor basis reached through School whose property is not
        // part of School's identity. Verbatim for the StudentSchoolAssociation/SchoolTypeDescriptor case.
        CustomViewAuthorizationHintFormatter
            .FormatDescriptorBasisNotIdentifyingOnIntermediate(
                "schoolTypeDescriptor",
                new QualifiedResourceName("Ed-Fi", "School"),
                "schoolReference",
                new QualifiedResourceName("Ed-Fi", "StudentSchoolAssociation"),
                new QualifiedResourceName("Ed-Fi", "SchoolTypeDescriptor")
            )
            .Should()
            .Be(
                "The descriptor property 'schoolTypeDescriptor' on 'Ed-Fi.School', reached from "
                    + "'Ed-Fi.StudentSchoolAssociation' through 'schoolReference', leads to custom view basis "
                    + "'Ed-Fi.SchoolTypeDescriptor' but is not an identifying property of 'Ed-Fi.School', so the "
                    + "subject's tombstone does not store its value. This is not supported by Change Queries, which "
                    + "only track deleted/changed values of identifying and securable properties. Should a different "
                    + "authorization strategy be used?"
            );
    }
}
