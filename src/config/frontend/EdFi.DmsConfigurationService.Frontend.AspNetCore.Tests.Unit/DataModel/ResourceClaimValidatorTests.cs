// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.DataModel.Model.ClaimSets;
using FluentAssertions;
using FluentValidation;
using NUnit.Framework;

namespace EdFi.DmsConfigurationService.Frontend.AspNetCore.Tests.Unit.DataModel;

public class ResourceClaimValidatorTests
{
    [TestFixture]
    public class When_Validating_ResourceClaims
    {
        public List<string> _authStrategies = ["AuthStrategy1", "AuthStrategy2", "AuthStrategy3", "AuthStrategy4"];
        public List<string> _actions = ["Create", "Read", "Update", "Delete"];

        public class FakeRequest
        {
            public string? Name { get; set; }
            public List<ResourceClaim>? ResourceClaims { get; set; }
        }

        public class Validator : AbstractValidator<FakeRequest>
        {
            public Validator(List<string> actions, List<string> authStrategies)
            {
                RuleFor(m => m).Custom((claimSet, context) =>
                {
                    var resourceClaimValidator = new ResourceClaimValidator();

                    if (claimSet.ResourceClaims != null && claimSet.ResourceClaims.Count != 0)
                    {
                        foreach (var resourceClaim in claimSet.ResourceClaims)
                        {
                            resourceClaimValidator.Validate(actions, authStrategies,
                                resourceClaim, claimSet.ResourceClaims, context, claimSet.Name);
                        }
                    }
                });
            }
        }

        [Test]
        public async Task Given_valid_resource_claims_List()
        {
            // Arrange
            var existingResourceClaims = new List<ResourceClaim>
            {
                new() {
                    Name = "resourceClaim1",
                    Actions = [ new ResourceClaimAction{Enabled = true, Name="Create"}],
                    DefaultAuthorizationStrategiesForCRUD = [
                        new() { AuthorizationStrategies = new List<AuthorizationStrategy>{
                        new() { AuthStrategyId = 1,
                        AuthStrategyName = "AuthStrategy1",
                        DisplayName = "AuthStrategy1" } } }  ]
                },
                 new() {
                    Name = "resourceClaim2",
                    Actions = [ new ResourceClaimAction{Enabled = true, Name="Create"}],
                    DefaultAuthorizationStrategiesForCRUD = [
                        new() { AuthorizationStrategies = new List<AuthorizationStrategy>{
                        new() { AuthStrategyId = 1,
                        AuthStrategyName = "AuthStrategy1",
                        DisplayName = "AuthStrategy1" } } }  ]
                }
             };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = existingResourceClaims };
            var validator = new Validator(_actions, _authStrategies);

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeTrue();
        }

        [Test]
        public async Task Given_duplicate_resource_claims()
        {
            // Arrange
            var existingResourceClaims = new List<ResourceClaim>
            {
                new() {
                    Name = "resourceClaim1",
                    Actions = [ new ResourceClaimAction{Enabled = true, Name="Create"}],
                    DefaultAuthorizationStrategiesForCRUD = [
                        new() { AuthorizationStrategies = [
                        new() { AuthStrategyId = 1,
                        AuthStrategyName = "AuthStrategy1",
                        DisplayName = "AuthStrategy1" } ] }  ]
                },
                 new() {
                    Name = "resourceClaim1",
                    Actions = [ new ResourceClaimAction{Enabled = true, Name="Create"}],
                    DefaultAuthorizationStrategiesForCRUD = [
                        new() { AuthorizationStrategies = [
                        new() { AuthStrategyId = 1,
                        AuthStrategyName = "AuthStrategy1",
                        DisplayName = "AuthStrategy1" } ] }  ]
                }
             };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = existingResourceClaims };
            var validator = new Validator(_actions, _authStrategies);

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().Be(1);
            validationResult.Errors[0].ErrorMessage.Should().Contain("Only unique resource claims can be added. The following is a duplicate resource: 'resourceClaim1'.");
        }

        [Test]
        public async Task Given_an_invalid_action()
        {
            // Arrange
            var existingResourceClaims = new List<ResourceClaim>
            {
                new() {
                    Name = "resourceClaim1",
                    Actions = [ new ResourceClaimAction{Enabled = true, Name="ActionNotExists"}],
                    DefaultAuthorizationStrategiesForCRUD = [
                        new() { AuthorizationStrategies = [
                        new() { AuthStrategyId = 1,
                        AuthStrategyName = "AuthStrategy1",
                        DisplayName = "AuthStrategy1" } ] }  ]
                }
             };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = existingResourceClaims };
            var validator = new Validator(_actions, _authStrategies);

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().Be(1);
            validationResult.Errors[0].ErrorMessage.Should().Contain("ActionNotExists is not a valid action. Resource name: 'resourceClaim1'");
        }

        [Test]
        public async Task Given_an_invalid_auth_strategy()
        {
            // Arrange
            var existingResourceClaims = new List<ResourceClaim>
            {
                new() {
                    Name = "resourceClaim1",
                    Actions = [ new ResourceClaimAction{Enabled = true, Name="Create"}],
                    DefaultAuthorizationStrategiesForCRUD = [
                        new() { AuthorizationStrategies = [
                        new() { AuthStrategyId = 1,
                        AuthStrategyName = "InvalidAuthStrategy",
                        DisplayName = "InvalidAuthStrategy" } ] }  ]
                }
             };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = existingResourceClaims };
            var validator = new Validator(_actions, _authStrategies);

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().Be(1);
            validationResult.Errors[0].ErrorMessage
                .Should().Contain("This resource claim contains an authorization strategy which is not in the system. Claimset Name: 'TestClaimset' Resource name: 'resourceClaim1' Authorization strategy: 'InvalidAuthStrategy'.");
        }

        [Test]
        public async Task Given_duplicate_resource_claims_on_children_list()
        {
            // Arrange
            var existingResourceClaims = new List<ResourceClaim>
            {
                new() {
                    Name = "resourceClaim1",
                    Actions = [ new ResourceClaimAction{Enabled = true, Name="Create"}],
                    DefaultAuthorizationStrategiesForCRUD = [
                        new() { AuthorizationStrategies = [
                        new() { AuthStrategyId = 1,
                        AuthStrategyName = "AuthStrategy1",
                        DisplayName = "AuthStrategy1" } ] }  ],
                    Children =
                    [
                        new() {
                        Name = "childResourceClaim1",
                        Actions = [ new ResourceClaimAction{Enabled = true, Name="Create"}],
                        DefaultAuthorizationStrategiesForCRUD = [
                            new() { AuthorizationStrategies = [
                            new() { AuthStrategyId = 1,
                            AuthStrategyName = "AuthStrategy1",
                            DisplayName = "AuthStrategy1" } ] }  ]
                        },
                        new() {
                        Name = "childResourceClaim1",
                        Actions = [ new ResourceClaimAction{Enabled = true, Name="Create"}],
                        DefaultAuthorizationStrategiesForCRUD = [
                            new() { AuthorizationStrategies = [
                            new() { AuthStrategyId = 1,
                            AuthStrategyName = "AuthStrategy1",
                            DisplayName = "AuthStrategy1" } ] }  ]
                        }
                    ]
                }
             };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = existingResourceClaims };
            var validator = new Validator(_actions, _authStrategies);

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().Be(1);
            validationResult.Errors[0].ErrorMessage.Should().Contain("Only unique resource claims can be added. The following is a duplicate resource: 'childResourceClaim1'.");
        }

        [Test]
        public async Task Given_an_invalid_auth_strategy_on_child_resource_claim()
        {
            // Arrange
            var existingResourceClaims = new List<ResourceClaim>
            {
                new() {
                    Name = "resourceClaim1",
                    Actions = [ new ResourceClaimAction{Enabled = true, Name="Create"}],
                    DefaultAuthorizationStrategiesForCRUD = [
                        new() { AuthorizationStrategies = [
                        new() { AuthStrategyId = 1,
                        AuthStrategyName = "AuthStrategy1",
                        DisplayName = "AuthStrategy1" } ] }  ],
                    Children =
                    [
                        new() {
                        Name = "childResourceClaim1",
                        Actions = [ new ResourceClaimAction{Enabled = true, Name="Create"}],
                        DefaultAuthorizationStrategiesForCRUD = [
                            new() { AuthorizationStrategies = [
                            new() { AuthStrategyId = 1,
                            AuthStrategyName = "InvalidAuthStrategy",
                            DisplayName = "InvalidAuthStrategy" } ] }  ]
                        }
                    ]
                }
             };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = existingResourceClaims };
            var validator = new Validator(_actions, _authStrategies);

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().Be(1);
            validationResult.Errors[0].ErrorMessage
                .Should().Contain("This resource claim contains an authorization strategy which is not in the system. Claimset Name: 'TestClaimset' Resource name: 'childResourceClaim1' Authorization strategy: 'InvalidAuthStrategy'.");
        }
    }
};
