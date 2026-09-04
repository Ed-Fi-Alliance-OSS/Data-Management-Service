// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.DocumentCache;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Tests.Unit;

[TestFixture]
public class Given_RepresentationRestampStoreContract
{
    [Test]
    public void It_rejects_a_page_that_is_not_strictly_ordered_by_document_id()
    {
        Action act = () =>
            new RepresentationRestampPage([
                new RepresentationRestampDocument(2, Guid.NewGuid(), Route()),
                new RepresentationRestampDocument(1, Guid.NewGuid(), Route()),
            ]);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_a_page_commit_with_tuples_outside_the_selected_page()
    {
        RepresentationRestampPage page = new([new RepresentationRestampDocument(1, Guid.NewGuid(), Route())]);

        Action act = () =>
            new RepresentationRestampPageCommit(
                page,
                [new RepresentationRestampStamp(2, 19, DateTimeOffset.UtcNow)],
                canonicalStampedCount: 1,
                mirrorStampedCount: 1
            );

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_a_page_commit_whose_canonical_or_mirror_counts_do_not_equal_the_page_size()
    {
        RepresentationRestampPage page = new([new RepresentationRestampDocument(1, Guid.NewGuid(), Route())]);

        Action canonicalAct = () =>
            new RepresentationRestampPageCommit(
                page,
                [new RepresentationRestampStamp(1, 19, DateTimeOffset.UtcNow)],
                canonicalStampedCount: 0,
                mirrorStampedCount: 1
            );
        Action mirrorAct = () =>
            new RepresentationRestampPageCommit(
                page,
                [new RepresentationRestampStamp(1, 19, DateTimeOffset.UtcNow)],
                canonicalStampedCount: 1,
                mirrorStampedCount: 0
            );

        canonicalAct.Should().Throw<ArgumentException>();
        mirrorAct.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_a_self_consistent_commit_for_a_different_selected_page()
    {
        RepresentationRestampPage firstPage = new([
            new RepresentationRestampDocument(1, Guid.NewGuid(), Route()),
        ]);
        RepresentationRestampPage secondPage = new([
            new RepresentationRestampDocument(2, Guid.NewGuid(), Route()),
        ]);
        RepresentationRestampPageCommit commit = new(
            secondPage,
            [new RepresentationRestampStamp(2, 19, DateTimeOffset.UtcNow)],
            canonicalStampedCount: 1,
            mirrorStampedCount: 1
        );

        Action act = () => commit.RequireSelectedPage(firstPage);

        act.Should().Throw<InvalidOperationException>();
    }

    [TestCase(DocumentCacheRepresentationRestampOperationState.Draft)]
    [TestCase(DocumentCacheRepresentationRestampOperationState.Incomplete)]
    public void It_allows_only_draft_or_incomplete_operations_to_execute(
        DocumentCacheRepresentationRestampOperationState state
    )
    {
        RepresentationRestampOperationAdmission.CanExecute(state).Should().BeTrue();
    }

    [TestCase(DocumentCacheRepresentationRestampOperationState.Completed)]
    [TestCase((DocumentCacheRepresentationRestampOperationState)99)]
    public void It_rejects_completed_or_unknown_operations_before_page_selection(
        DocumentCacheRepresentationRestampOperationState state
    )
    {
        RepresentationRestampOperationAdmission.CanExecute(state).Should().BeFalse();
    }

    private static RepresentationRestampMirrorRoute Route() => new(1, "edfi", "Student", isDescriptor: false);
}
