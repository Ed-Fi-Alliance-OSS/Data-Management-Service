// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Model.ClaimSets;

[TestFixture]
public class ResourceClaimActionRequestValidatorTests
{
    [Test]
    public async Task It_rejects_empty_action_collections()
    {
        var validator = new AddResourceClaimActionsOnClaimSetRequest.Validator();
        var request = new AddResourceClaimActionsOnClaimSetRequest
        {
            ClaimSetId = 1,
            ResourceClaimId = 2,
            ResourceClaimActions = [],
        };

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ResourceClaimActions");
    }

    [Test]
    public async Task It_rejects_null_action_collections()
    {
        var validator = new AddResourceClaimActionsOnClaimSetRequest.Validator();
        var request = new AddResourceClaimActionsOnClaimSetRequest
        {
            ClaimSetId = 1,
            ResourceClaimId = 2,
            ResourceClaimActions = null,
        };

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ResourceClaimActions");
    }

    [Test]
    public async Task It_rejects_disabled_only_action_collections()
    {
        var validator = new EditResourceClaimActionsOnClaimSetRequest.Validator();
        var request = new EditResourceClaimActionsOnClaimSetRequest
        {
            ClaimSetId = 1,
            ResourceClaimId = 2,
            ResourceClaimActions = [new() { Name = "Read", Enabled = false }],
        };

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ResourceClaimActions");
    }

    [Test]
    public async Task It_rejects_duplicate_actions_case_insensitively()
    {
        var validator = new AddResourceClaimActionsOnClaimSetRequest.Validator();
        var request = new AddResourceClaimActionsOnClaimSetRequest
        {
            ClaimSetId = 1,
            ResourceClaimId = 2,
            ResourceClaimActions =
            [
                new() { Name = "Read", Enabled = true },
                new() { Name = "read", Enabled = true },
            ],
        };

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.ErrorMessage.Contains("duplicated"));
    }

    [Test]
    public async Task It_rejects_missing_override_fields()
    {
        var validator = new OverrideAuthStategyOnClaimSetRequest.Validator();
        var request = new OverrideAuthStategyOnClaimSetRequest();

        var result = await validator.ValidateAsync(request);

        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyName == "ClaimSetId");
        result.Errors.Should().Contain(e => e.PropertyName == "ResourceClaimId");
        result.Errors.Should().Contain(e => e.PropertyName == "ActionName");
        result.Errors.Should().Contain(e => e.PropertyName == "AuthorizationStrategies");
    }

    [Test]
    public void It_defaults_request_action_and_strategy_collections()
    {
        var actionsRequest = new AddResourceClaimActionsOnClaimSetRequest();
        var overrideRequest = new OverrideAuthStategyOnClaimSetRequest();

        actionsRequest.ResourceClaimActions.Should().BeEmpty();
        overrideRequest.AuthStrategyIds.Should().BeEmpty();
        overrideRequest.AuthorizationStrategies.Should().BeEmpty();
    }
}
