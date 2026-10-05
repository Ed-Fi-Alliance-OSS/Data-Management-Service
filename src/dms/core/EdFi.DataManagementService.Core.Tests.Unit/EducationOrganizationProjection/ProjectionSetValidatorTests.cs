// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.External;
using EdFi.DataManagementService.Core.EducationOrganizationProjection;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.EducationOrganizationProjection;

/// <summary>
/// Row builders in the shape the provider readers return: every slot the type can carry, and null in
/// every other slot.
/// </summary>
internal static class ProjectionRows
{
    public static EducationOrganizationProjectionRow Sea(
        long id,
        string name = "Sea",
        string? shortName = null
    ) => new(id, "Ed-Fi:StateEducationAgency", name, shortName, null, null, null, null);

    public static EducationOrganizationProjectionRow Esc(
        long id,
        long? sea = null,
        string name = "Esc",
        string? shortName = null
    ) => new(id, "Ed-Fi:EducationServiceCenter", name, shortName, null, null, null, sea);

    public static EducationOrganizationProjectionRow Lea(
        long id,
        long? parent = null,
        long? esc = null,
        long? sea = null,
        string name = "Lea",
        string? shortName = null
    ) => new(id, "Ed-Fi:LocalEducationAgency", name, shortName, null, parent, esc, sea);

    public static EducationOrganizationProjectionRow School(
        long id,
        long? lea = null,
        string name = "School",
        string? shortName = null
    ) => new(id, "Ed-Fi:School", name, shortName, lea, null, null, null);

    /// <summary>
    /// The contract's worked example (docs/EDUCATION-ORGANIZATION-PROJECTION.md): the set behind
    /// contract/examples/success-page.json and success-last-page.json.
    /// </summary>
    public static EducationOrganizationProjectionRow[] WorkedExample() =>
        [
            Sea(1, "Example State Department of Education", "ESDE"),
            Esc(10, sea: 1, name: "Region 10 Education Service Center", shortName: "ESC 10"),
            Lea(100, esc: 10, sea: 1, name: "Grand Bend ISD", shortName: "GBISD"),
            Lea(101, parent: 100, esc: 10, name: "Grand Bend North ISD"),
            School(100001, lea: 100, name: "Grand Bend High School", shortName: "GBHS"),
            School(101001, lea: 101, name: "Grand Bend North Elementary School"),
            School(900001, name: "Independent Academy"),
        ];
}

public abstract class ProjectionSetValidatorTests
{
    internal static ProjectionSetValidation Validate(params EducationOrganizationProjectionRow[] rows) =>
        ProjectionSetValidator.Validate(
            rows,
            NoOpProjectionProcessingObserver.Instance,
            CancellationToken.None
        );

    internal static IReadOnlyList<ProjectionItem> Items(params EducationOrganizationProjectionRow[] rows)
    {
        ProjectionSetValidation validation = Validate(rows);
        validation.Should().BeOfType<ProjectionSetValidation.Valid>();

        return ProjectionSetValidator.SelectParents(
            ((ProjectionSetValidation.Valid)validation).Set,
            NoOpProjectionProcessingObserver.Instance,
            CancellationToken.None
        );
    }

    internal static long? ParentOf(IReadOnlyList<ProjectionItem> items, long id) =>
        items.Single(item => item.EducationOrganizationId == id).ParentId;

    [TestFixture]
    [Parallelizable]
    public class Given_A_Valid_Hierarchy_Covering_Every_Precedence_Row : ProjectionSetValidatorTests
    {
        private IReadOnlyList<ProjectionItem> _items = [];

        [SetUp]
        public void Setup()
        {
            _items = Items(
                ProjectionRows.Sea(1),
                ProjectionRows.Sea(2),
                ProjectionRows.Esc(10, sea: 1),
                ProjectionRows.Esc(11),
                ProjectionRows.Lea(100, esc: 10, sea: 1),
                ProjectionRows.Lea(101, parent: 100, esc: 10, sea: 2),
                ProjectionRows.Lea(102, sea: 2),
                ProjectionRows.Lea(103),
                ProjectionRows.School(1000, lea: 101),
                ProjectionRows.School(1001)
            );
        }

        [Test]
        public void It_selects_the_parent_agency_over_every_other_valid_reference() =>
            ParentOf(_items, 101).Should().Be(100);

        [Test]
        public void It_falls_back_to_the_service_center_without_a_parent_agency() =>
            ParentOf(_items, 100).Should().Be(10);

        [Test]
        public void It_falls_back_to_the_state_agency_with_only_a_state_agency() =>
            ParentOf(_items, 102).Should().Be(2);

        [Test]
        public void It_leaves_an_agency_with_no_reference_without_a_parent() =>
            ParentOf(_items, 103).Should().BeNull();

        [Test]
        public void It_gives_a_school_its_agency() => ParentOf(_items, 1000).Should().Be(101);

        [Test]
        public void It_leaves_a_school_without_an_agency_without_a_parent() =>
            ParentOf(_items, 1001).Should().BeNull();

        [Test]
        public void It_gives_a_service_center_its_state_agency() => ParentOf(_items, 10).Should().Be(1);

        [Test]
        public void It_leaves_a_service_center_without_a_state_agency_without_a_parent() =>
            ParentOf(_items, 11).Should().BeNull();

        [Test]
        public void It_never_gives_a_state_agency_a_parent() => ParentOf(_items, 1).Should().BeNull();

        [Test]
        public void It_maps_every_stored_discriminator_to_its_core_type() =>
            _items
                .Select(item => (item.EducationOrganizationId, item.Kind))
                .Should()
                .Contain([
                    (1, ProjectionItemKind.StateEducationAgency),
                    (10, ProjectionItemKind.EducationServiceCenter),
                    (100, ProjectionItemKind.LocalEducationAgency),
                    (1000, ProjectionItemKind.School),
                ]);

        [Test]
        public void It_keeps_the_rows_in_ascending_id_order() =>
            _items.Select(item => item.EducationOrganizationId).Should().BeInAscendingOrder();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Set_That_Contradicts_Itself : ProjectionSetValidatorTests
    {
        private static IEnumerable<TestCaseData> Cases()
        {
            TestCaseData Case(
                string name,
                ProjectionDataInvalidReason reason,
                params EducationOrganizationProjectionRow[] rows
            ) => new TestCaseData(rows, reason).SetName($"It_refuses_{name}");

            yield return Case(
                "a_duplicate_identifier_across_types",
                ProjectionDataInvalidReason.DuplicateIdentifier,
                ProjectionRows.Sea(1),
                ProjectionRows.School(1)
            );
            yield return Case(
                "a_self_parent",
                ProjectionDataInvalidReason.ParentCycle,
                ProjectionRows.Lea(100, parent: 100)
            );
            yield return Case(
                "a_two_agency_cycle",
                ProjectionDataInvalidReason.ParentCycle,
                ProjectionRows.Lea(100, parent: 101),
                ProjectionRows.Lea(101, parent: 100)
            );
            yield return Case(
                "a_three_agency_cycle_entered_from_a_chain",
                ProjectionDataInvalidReason.ParentCycle,
                ProjectionRows.Lea(99, parent: 100),
                ProjectionRows.Lea(100, parent: 101),
                ProjectionRows.Lea(101, parent: 102),
                ProjectionRows.Lea(102, parent: 100)
            );
            yield return Case(
                "a_parent_agency_slot_holding_a_service_center",
                ProjectionDataInvalidReason.UnresolvedReference,
                ProjectionRows.Esc(10),
                ProjectionRows.Lea(100, parent: 10)
            );
            yield return Case(
                "a_dangling_service_center_beside_a_valid_parent_agency",
                ProjectionDataInvalidReason.UnresolvedReference,
                ProjectionRows.Lea(100),
                ProjectionRows.Lea(101, parent: 100, esc: 10)
            );
            yield return Case(
                "a_dangling_state_agency_beside_a_valid_parent_agency",
                ProjectionDataInvalidReason.UnresolvedReference,
                ProjectionRows.Lea(100),
                ProjectionRows.Lea(101, parent: 100, sea: 1)
            );
            yield return Case(
                "a_service_center_slot_holding_a_state_agency",
                ProjectionDataInvalidReason.UnresolvedReference,
                ProjectionRows.Sea(1),
                ProjectionRows.Lea(100, esc: 1)
            );
            yield return Case(
                "a_state_agency_slot_holding_a_school",
                ProjectionDataInvalidReason.UnresolvedReference,
                ProjectionRows.Esc(10, sea: 1000),
                ProjectionRows.School(1000)
            );
            yield return Case(
                "a_school_agency_slot_holding_a_school",
                ProjectionDataInvalidReason.UnresolvedReference,
                ProjectionRows.School(1000),
                ProjectionRows.School(1001, lea: 1000)
            );
            yield return Case(
                "a_school_agency_that_does_not_exist",
                ProjectionDataInvalidReason.UnresolvedReference,
                ProjectionRows.School(1000, lea: 100)
            );
            yield return Case(
                "a_lone_high_surrogate_in_a_name",
                ProjectionDataInvalidReason.MalformedName,
                ProjectionRows.Sea(1, name: "Bad \uD83D name")
            );
            yield return Case(
                "a_lone_low_surrogate_in_a_short_name",
                ProjectionDataInvalidReason.MalformedName,
                ProjectionRows.Sea(1, shortName: "\uDE00")
            );
            yield return Case(
                "a_high_surrogate_ending_a_name",
                ProjectionDataInvalidReason.MalformedName,
                ProjectionRows.Sea(1, name: "End\uD83D")
            );
        }

        [TestCaseSource(nameof(Cases))]
        public void It_reports_the_reason(EducationOrganizationProjectionRow[] rows, object reason) =>
            Validate(rows)
                .Should()
                .BeEquivalentTo(
                    new ProjectionSetValidation.DataInvalid((ProjectionDataInvalidReason)reason),
                    options => options.RespectingRuntimeTypes()
                );
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Well_Formed_Supplementary_Characters : ProjectionSetValidatorTests
    {
        [Test]
        public void It_accepts_a_name_of_75_supplementary_characters() =>
            Validate(ProjectionRows.Sea(1, name: string.Concat(Enumerable.Repeat("\U0001F600", 75))))
                .Should()
                .BeOfType<ProjectionSetValidation.Valid>();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Discriminator_Outside_The_Core_Types : ProjectionSetValidatorTests
    {
        [Test]
        public void It_reports_the_set_unsupported() =>
            Validate(
                    ProjectionRows.Sea(1),
                    new EducationOrganizationProjectionRow(
                        2,
                        "Ed-Fi:PostSecondaryInstitution",
                        "Other",
                        null,
                        null,
                        null,
                        null,
                        null
                    )
                )
                .Should()
                .BeOfType<ProjectionSetValidation.UnsupportedDiscriminator>();

        [Test]
        public void It_does_not_accept_the_wire_spelling_as_a_stored_literal() =>
            Validate(
                    new EducationOrganizationProjectionRow(
                        1,
                        "edfi.StateEducationAgency",
                        "Sea",
                        null,
                        null,
                        null,
                        null,
                        null
                    )
                )
                .Should()
                .BeOfType<ProjectionSetValidation.UnsupportedDiscriminator>();

        /// <summary>
        /// Discriminators are checked over the whole set before any data rule, so a deployment whose
        /// model yields an unexpected literal is reported as unsupported wherever the literal sits.
        /// </summary>
        [Test]
        public void It_reports_unsupported_ahead_of_an_earlier_duplicate() =>
            Validate(
                    ProjectionRows.Sea(1),
                    ProjectionRows.School(1),
                    new EducationOrganizationProjectionRow(
                        5,
                        "Ed-Fi:Other",
                        "Other",
                        null,
                        null,
                        null,
                        null,
                        null
                    )
                )
                .Should()
                .BeOfType<ProjectionSetValidation.UnsupportedDiscriminator>();
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Reader_That_Broke_Its_Contract : ProjectionSetValidatorTests
    {
        [Test]
        public void It_throws_for_rows_out_of_id_order()
        {
            Action act = () => Validate(ProjectionRows.Sea(2), ProjectionRows.Sea(1));

            act.Should().Throw<InvalidOperationException>().WithMessage("*ascending identifier order*");
        }

        [Test]
        public void It_throws_for_a_reference_the_type_cannot_carry()
        {
            Action act = () =>
                Validate(
                    ProjectionRows.Lea(100),
                    new EducationOrganizationProjectionRow(
                        1000,
                        "Ed-Fi:School",
                        "School",
                        null,
                        100,
                        100,
                        null,
                        null
                    )
                );

            act.Should().Throw<InvalidOperationException>().WithMessage("*cannot carry*");
        }
    }

    /// <summary>
    /// 50,000 agencies, each the child of the next, on a thread with a 256 KB stack. The walk from the
    /// first agency follows the whole chain, so a recursive walk needs a frame per agency and overflows
    /// such a stack long before the end of the chain.
    /// </summary>
    [TestFixture]
    public class Given_A_Chain_Of_50000_Local_Education_Agencies : ProjectionSetValidatorTests
    {
        private const int Length = 50_000;
        private const int SmallStackBytes = 256 * 1024;

        private static EducationOrganizationProjectionRow[] Chain(bool closeIntoCycle)
        {
            long? lastParent = closeIntoCycle ? 1 : null;

            return
            [
                .. Enumerable
                    .Range(1, Length)
                    .Select(id => ProjectionRows.Lea(id, parent: id == Length ? lastParent : id + 1)),
            ];
        }

        private static T OnSmallStack<T>(Func<T> work)
        {
            T result = default!;
            System.Runtime.ExceptionServices.ExceptionDispatchInfo? failure = null;
            var thread = new Thread(
                () =>
                {
                    try
                    {
                        result = work();
                    }
                    catch (Exception exception)
                    {
                        failure = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(exception);
                    }
                },
                SmallStackBytes
            );
            thread.Start();
            thread.Join();

            failure?.Throw();
            return result;
        }

        [Test]
        public void It_validates_the_chain_and_selects_each_parent()
        {
            IReadOnlyList<ProjectionItem> items = OnSmallStack(() => Items(Chain(closeIntoCycle: false)));

            items.Should().HaveCount(Length);
            items[0].ParentId.Should().Be(2);
            items[^1].ParentId.Should().BeNull();
        }

        [Test]
        public void It_finds_the_cycle_when_the_chain_is_closed() =>
            OnSmallStack(() => Validate(Chain(closeIntoCycle: true)))
                .Should()
                .BeEquivalentTo(
                    new ProjectionSetValidation.DataInvalid(ProjectionDataInvalidReason.ParentCycle),
                    options => options.RespectingRuntimeTypes()
                );
    }
}
