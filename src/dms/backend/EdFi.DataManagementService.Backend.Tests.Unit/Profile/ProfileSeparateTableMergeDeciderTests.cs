// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Profile;
using EdFi.DataManagementService.Core.Profile;
using FluentAssertions;
using NUnit.Framework;
using static EdFi.DataManagementService.Backend.Tests.Unit.Profile.ProfileTestDoubles;

namespace EdFi.DataManagementService.Backend.Tests.Unit.Profile;

[TestFixture]
public class Given_SeparateTableDecider_for_visible_present_no_stored_creatable_true_returns_Insert
{
    private ProfileSeparateTableMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var request = RequestVisiblePresentScope("$._ext.sample", creatable: true);
        _outcome = new ProfileSeparateTableMergeDecider().Decide(
            scopeJsonScope: "$._ext.sample",
            requestScopeState: request,
            storedScopeState: null,
            storedRowExists: false
        );
    }

    [Test]
    public void It_returns_Insert() => _outcome.Should().Be(ProfileSeparateTableMergeOutcome.Insert);
}

[TestFixture]
public class Given_SeparateTableDecider_for_visible_present_no_stored_creatable_false_returns_RejectCreateDenied
{
    private ProfileSeparateTableMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var request = RequestVisiblePresentScope("$._ext.sample", creatable: false);
        _outcome = new ProfileSeparateTableMergeDecider().Decide(
            scopeJsonScope: "$._ext.sample",
            requestScopeState: request,
            storedScopeState: null,
            storedRowExists: false
        );
    }

    [Test]
    public void It_returns_RejectCreateDenied() =>
        _outcome.Should().Be(ProfileSeparateTableMergeOutcome.RejectCreateDenied);
}

[TestFixture]
public class Given_SeparateTableDecider_for_visible_present_stored_matched_returns_Update
{
    private ProfileSeparateTableMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        var request = RequestVisiblePresentScope("$._ext.sample", creatable: true);
        var stored = StoredVisiblePresentScope("$._ext.sample");
        _outcome = new ProfileSeparateTableMergeDecider().Decide(
            scopeJsonScope: "$._ext.sample",
            requestScopeState: request,
            storedScopeState: stored,
            storedRowExists: true
        );
    }

    [Test]
    public void It_returns_Update() => _outcome.Should().Be(ProfileSeparateTableMergeOutcome.Update);
}

[TestFixture]
public class Given_SeparateTableDecider_for_visible_absent_stored_matched_returns_Delete
{
    private ProfileSeparateTableMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        // Request-side classifies the scope as VisibleAbsent (writable but request omits it).
        var request = RequestVisibleAbsentScope("$._ext.sample", creatable: true);
        var stored = StoredVisiblePresentScope("$._ext.sample");
        _outcome = new ProfileSeparateTableMergeDecider().Decide(
            scopeJsonScope: "$._ext.sample",
            requestScopeState: request,
            storedScopeState: stored,
            storedRowExists: true
        );
    }

    [Test]
    public void It_returns_Delete() => _outcome.Should().Be(ProfileSeparateTableMergeOutcome.Delete);
}

[TestFixture]
public class Given_SeparateTableDecider_for_hidden_stored_scope_returns_Preserve
{
    private ProfileSeparateTableMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        // Stored visibility is Hidden with an existing row; regardless of the request side
        // the persister must emit the current row unchanged.
        var request = RequestVisibleAbsentScope("$._ext.sample", creatable: true);
        var stored = StoredHiddenScope("$._ext.sample");
        _outcome = new ProfileSeparateTableMergeDecider().Decide(
            scopeJsonScope: "$._ext.sample",
            requestScopeState: request,
            storedScopeState: stored,
            storedRowExists: true
        );
    }

    [Test]
    public void It_returns_Preserve() => _outcome.Should().Be(ProfileSeparateTableMergeOutcome.Preserve);
}

[TestFixture]
public class Given_SeparateTableDecider_for_no_actionable_decision_throws_InvalidOperationException
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        _act = () =>
            new ProfileSeparateTableMergeDecider().Decide(
                scopeJsonScope: "$._ext.sample",
                requestScopeState: null,
                storedScopeState: null,
                storedRowExists: false
            );
    }

    [Test]
    public void It_throws_InvalidOperationException() =>
        _act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ProfileSeparateTableMergeDecider*$._ext.sample*no actionable*");
}

[TestFixture]
public class Given_SeparateTableDecider_for_hidden_request_with_visible_stored_fails_closed
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        // A scope cannot simultaneously be Hidden on the request side and VisiblePresent
        // on the stored side under the same writable profile — this is an inconsistent
        // tuple and the decider must fall through to the throw rather than silently
        // delete the stored row.
        var decider = new ProfileSeparateTableMergeDecider();
        var requestScope = RequestHiddenScope("$._ext.sample");
        var storedScope = StoredVisiblePresentScope("$._ext.sample");
        _act = () => decider.Decide("$._ext.sample", requestScope, storedScope, storedRowExists: true);
    }

    [Test]
    public void It_throws_InvalidOperationException_for_inconsistent_tuple() =>
        _act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ProfileSeparateTableMergeDecider*$._ext.sample*no actionable*");
}

[TestFixture]
public class Given_SeparateTableDecider_for_null_request_with_visible_stored_fails_closed
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        // A null request-side scope state paired with a matched stored visible row is a
        // caller-contract violation: callers must supply request-side state for any scope
        // whose stored side participates in the decision. Fail closed rather than delete.
        var decider = new ProfileSeparateTableMergeDecider();
        var storedScope = StoredVisiblePresentScope("$._ext.sample");
        _act = () =>
            decider.Decide("$._ext.sample", requestScopeState: null, storedScope, storedRowExists: true);
    }

    [Test]
    public void It_throws_InvalidOperationException_for_caller_contract_violation() =>
        _act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ProfileSeparateTableMergeDecider*$._ext.sample*no actionable*");
}

[TestFixture]
public class Given_SeparateTableDecider_for_visible_present_request_with_hidden_stored_fails_closed
{
    private Action _act = null!;

    [SetUp]
    public void Setup()
    {
        // A scope cannot simultaneously be VisiblePresent on the request side and Hidden
        // on the stored side under the same writable profile — Hidden classification is
        // profile-level and applied uniformly to both sides. The decider narrows the
        // "Preserve dominates" rule so this inconsistent tuple falls through to the throw
        // rather than silently discarding the request's visible values (which the previous
        // unconditional Preserve would have done) or routing to Insert (which would
        // collide with the existing stored row).
        var decider = new ProfileSeparateTableMergeDecider();
        var requestScope = RequestVisiblePresentScope("$._ext.sample", creatable: true);
        var storedScope = StoredHiddenScope("$._ext.sample");
        _act = () => decider.Decide("$._ext.sample", requestScope, storedScope, storedRowExists: true);
    }

    [Test]
    public void It_throws_InvalidOperationException_for_inconsistent_tuple() =>
        _act.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*ProfileSeparateTableMergeDecider*$._ext.sample*no actionable*");
}

[TestFixture]
public class Given_SeparateTableDecider_for_visible_absent_request_with_hidden_stored_returns_Preserve
{
    private ProfileSeparateTableMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        // VisibleAbsent request paired with a Hidden stored row is the legitimate
        // preservation case: the requester omits the scope, the profile hides the
        // stored side, and the decider must emit the current row unchanged so the
        // persister does not interpret the omission as a delete.
        var decider = new ProfileSeparateTableMergeDecider();
        var requestScope = RequestVisibleAbsentScope("$._ext.sample", creatable: true);
        var storedScope = StoredHiddenScope("$._ext.sample");
        _outcome = decider.Decide("$._ext.sample", requestScope, storedScope, storedRowExists: true);
    }

    [Test]
    public void It_returns_Preserve() => _outcome.Should().Be(ProfileSeparateTableMergeOutcome.Preserve);
}

[TestFixture]
public class Given_SeparateTableDecider_for_null_request_with_hidden_stored_returns_Preserve
{
    private ProfileSeparateTableMergeOutcome _outcome;

    [SetUp]
    public void Setup()
    {
        // The narrowed "Preserve dominates" rule returns Preserve for a null
        // request-side scope state paired with a Hidden stored row. This case is
        // still reachable end-to-end: the synthesizer's request-scope contract
        // gate only fires when a flattened root-extension buffer is present, so
        // when Core omits the RequestScopeState entry AND the writable profile
        // hides the scope (so the flattener emits no buffer), the tuple flows
        // from production into the decider (see Fixture 2 in the PG/MSSQL
        // separate-table integration suites).
        var decider = new ProfileSeparateTableMergeDecider();
        var storedScope = StoredHiddenScope("$._ext.sample");
        _outcome = decider.Decide(
            "$._ext.sample",
            requestScopeState: null,
            storedScope,
            storedRowExists: true
        );
    }

    [Test]
    public void It_returns_Preserve() => _outcome.Should().Be(ProfileSeparateTableMergeOutcome.Preserve);
}
