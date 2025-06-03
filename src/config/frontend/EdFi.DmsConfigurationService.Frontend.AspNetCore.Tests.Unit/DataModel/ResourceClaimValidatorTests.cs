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
        private readonly List<string> _authStrategies =
        [
            "AuthStrategy1",
            "AuthStrategy2",
            "AuthStrategy3",
            "AuthStrategy4",
        ];

        private readonly List<string> _actions = ["Create", "Read", "Update", "Delete"];

        public class FakeRequest
        {
            public string? Name { get; set; }
            public List<ResourceClaim>? ResourceClaims { get; set; }
        }

        public class Validator : AbstractValidator<FakeRequest>
        {
            public Validator(
                List<string> actions,
                List<string> authStrategies,
                Dictionary<string, string?> parentResourceClaimByResourceClaim
            )
            {
                RuleFor(m => m)
                    .Custom(
                        (claimSet, context) =>
                        {
                            var resourceClaimValidator = new ResourceClaimValidator();

                            if (claimSet.ResourceClaims != null && claimSet.ResourceClaims.Count != 0)
                            {
                                foreach (var resourceClaim in claimSet.ResourceClaims)
                                {
                                    resourceClaimValidator.Validate(
                                        actions,
                                        authStrategies,
                                        resourceClaim,
                                        parentResourceClaimByResourceClaim,
                                        context,
                                        claimSet.Name
                                    );
                                }
                            }
                        }
                    );
            }
        }

        [Test]
        public async Task Given_valid_resource_claims_should_return_success()
        {
            // Arrange
            var resourceClaims = new List<ResourceClaim>
            {
                new()
                {
                    Name = "resourceClaim1",
                    Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies = new List<AuthorizationStrategy>
                            {
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            },
                        },
                    ],
                },
                new()
                {
                    Name = "resourceClaim2",
                    Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies = new List<AuthorizationStrategy>
                            {
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            },
                        },
                    ],
                },
            };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = resourceClaims };

            var validator = new Validator(
                _actions,
                _authStrategies,
                CreateResourceClaimsMap(("resourceClaim1", null), ("resourceClaim2", null))
            );

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeTrue();
        }

        private static Dictionary<string, string?> CreateResourceClaimsMap(
            params (string resourceClaimName, string? parentResourceClaimName)[] resourceClaims
        )
        {
            return new Dictionary<string, string?>(
                resourceClaims.Select(t => new KeyValuePair<string, string?>(
                    t.resourceClaimName,
                    t.parentResourceClaimName
                )),
                StringComparer.OrdinalIgnoreCase
            );
        }

        [Test]
        public async Task Given_a_non_existing_resource_claim_should_return_validation_error()
        {
            // Arrange
            var resourceClaims = new List<ResourceClaim>
            {
                new()
                {
                    Name = "resourceClaim1",
                    Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies =
                            [
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            ],
                        },
                    ],
                },
                new()
                {
                    Name = "NonExistingClaim",
                    Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies =
                            [
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            ],
                        },
                    ],
                },
            };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = resourceClaims };

            var validator = new Validator(
                _actions,
                _authStrategies,
                CreateResourceClaimsMap(("resourceClaim1", null))
            );

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().Be(1);
            validationResult
                .Errors[0]
                .ErrorMessage.Should()
                .Contain(
                    "This Claim Set contains a resource which is not in the system. ClaimSet Name: 'TestClaimset' Resource name: 'NonExistingClaim'"
                );
        }

        [Test]
        public async Task Given_a_root_resource_claim_added_as_a_child_should_return_validation_error()
        {
            // Arrange
            var resourceClaims = new List<ResourceClaim>
            {
                new()
                {
                    Name = "resourceClaim1",
                    Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies =
                            [
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            ],
                        },
                    ],
                    Children =
                    [
                        new()
                        {
                            Name = "resourceClaim2",
                            Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                            DefaultAuthorizationStrategiesForCRUD =
                            [
                                new()
                                {
                                    AuthorizationStrategies =
                                    [
                                        new()
                                        {
                                            Id = 1,
                                            AuthorizationStrategyName = "AuthStrategy1",
                                            DisplayName = "AuthStrategy1",
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = resourceClaims };

            var validator = new Validator(
                _actions,
                _authStrategies,
                CreateResourceClaimsMap(("resourceClaim1", null), ("resourceClaim2", null))
            );

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().Be(1);
            validationResult
                .Errors[0]
                .ErrorMessage.Should()
                .Contain("'resourceClaim2' can not be added as a child resource.");
        }

        [Test]
        public async Task Given_a_non_existing_action_should_return_validation_error()
        {
            // Arrange
            var resourceClaims = new List<ResourceClaim>
            {
                new()
                {
                    Name = "resourceClaim1",
                    Actions = [new ResourceClaimAction { Enabled = true, Name = "ActionNotExists" }],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies =
                            [
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            ],
                        },
                    ],
                },
            };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = resourceClaims };

            var validator = new Validator(
                _actions,
                _authStrategies,
                CreateResourceClaimsMap(("resourceClaim1", null))
            );

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().Be(1);
            validationResult
                .Errors[0]
                .ErrorMessage.Should()
                .Contain("ActionNotExists is not a valid action. Resource name: 'resourceClaim1'");
        }

        [Test]
        public async Task Given_a_duplicate_action_should_return_validation_error()
        {
            // Arrange
            var resourceClaims = new List<ResourceClaim>
            {
                new()
                {
                    Name = "resourceClaim1",
                    Actions =
                    [
                        new ResourceClaimAction { Enabled = true, Name = "Create" },
                        new ResourceClaimAction { Enabled = true, Name = "Read" },
                        new ResourceClaimAction { Enabled = true, Name = "Create" },
                    ],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies =
                            [
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            ],
                        },
                    ],
                },
            };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = resourceClaims };

            var validator = new Validator(
                _actions,
                _authStrategies,
                CreateResourceClaimsMap(("resourceClaim1", null))
            );

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().Be(1);
            validationResult
                .Errors[0]
                .ErrorMessage.Should()
                .Contain("Create action is duplicated. Resource name: 'resourceClaim1'");
        }

        [Test]
        public async Task Given_a_resource_claim_with_no_enabled_actions_should_return_validation_error()
        {
            // Arrange
            var resourceClaims = new List<ResourceClaim>
            {
                new()
                {
                    Name = "resourceClaim1",
                    Actions =
                    [
                        new ResourceClaimAction { Enabled = false, Name = "Create" },
                        new ResourceClaimAction { Enabled = false, Name = "Read" },
                    ],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies =
                            [
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            ],
                        },
                    ],
                },
            };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = resourceClaims };

            var validator = new Validator(
                _actions,
                _authStrategies,
                CreateResourceClaimsMap(("resourceClaim1", null))
            );

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().Be(1);
            validationResult
                .Errors[0]
                .ErrorMessage.Should()
                .Contain(
                    "A resource must have at least one action associated with it to be added. Resource name: 'resourceClaim1'"
                );
        }

        [Test]
        public async Task Given_a_resource_with_no_actions_should_return_validation_error()
        {
            // Arrange
            var resourceClaims = new List<ResourceClaim>
            {
                new()
                {
                    Name = "resourceClaim1",
                    Actions = [],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies =
                            [
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            ],
                        },
                    ],
                },
            };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = resourceClaims };

            var validator = new Validator(
                _actions,
                _authStrategies,
                CreateResourceClaimsMap(("resourceClaim1", null))
            );

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().Be(1);
            validationResult
                .Errors[0]
                .ErrorMessage.Should()
                .Contain($"Actions can not be empty. Resource name: 'resourceClaim1'");
        }

        [Test]
        public async Task Given_a_non_existing_authorization_strategy_should_return_validation_error()
        {
            // Arrange
            var resourceClaims = new List<ResourceClaim>
            {
                new()
                {
                    Name = "resourceClaim1",
                    Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies =
                            [
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            ],
                        },
                    ],
                    Children =
                    [
                        new()
                        {
                            Name = "childResourceClaim1a",
                            Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                            DefaultAuthorizationStrategiesForCRUD =
                            [
                                new()
                                {
                                    AuthorizationStrategies =
                                    [
                                        new()
                                        {
                                            Id = 1,
                                            AuthorizationStrategyName = "InvalidAuthStrategy",
                                            DisplayName = "InvalidAuthStrategy",
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = resourceClaims };

            var validator = new Validator(
                _actions,
                _authStrategies,
                CreateResourceClaimsMap(("resourceClaim1", null), ("childResourceClaim1a", "resourceClaim1"))
            );

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().Be(1);
            validationResult
                .Errors[0]
                .ErrorMessage.Should()
                .Contain(
                    "This resource claim contains an authorization strategy which is not in the system. ClaimSet Name: 'TestClaimset' Resource name: 'childResourceClaim1a' Authorization strategy: 'InvalidAuthStrategy'."
                );
        }

        [Test]
        public async Task Given_a_non_existing_authorization_strategy_used_for_an_override_should_return_validation_error()
        {
            // Arrange
            var resourceClaims = new List<ResourceClaim>
            {
                new()
                {
                    Name = "resourceClaim1",
                    Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies =
                            [
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            ],
                        },
                    ],
                    Children =
                    [
                        new()
                        {
                            Name = "childResourceClaim1a",
                            Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                            AuthorizationStrategyOverridesForCRUD =
                            [
                                new()
                                {
                                    AuthorizationStrategies =
                                    [
                                        new()
                                        {
                                            Id = 1,
                                            AuthorizationStrategyName = "InvalidAuthStrategy",
                                            DisplayName = "InvalidAuthStrategy",
                                        },
                                    ],
                                },
                            ],
                            DefaultAuthorizationStrategiesForCRUD =
                            [
                                new()
                                {
                                    AuthorizationStrategies =
                                    [
                                        new()
                                        {
                                            Id = 1,
                                            AuthorizationStrategyName = "AuthStrategy2",
                                            DisplayName = "AuthStrategy2",
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = resourceClaims };

            var validator = new Validator(
                _actions,
                _authStrategies,
                CreateResourceClaimsMap(("resourceClaim1", null), ("childResourceClaim1a", "resourceClaim1"))
            );

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().Be(1);
            validationResult
                .Errors[0]
                .ErrorMessage.Should()
                .Contain(
                    "This resource claim contains an authorization strategy which is not in the system. ClaimSet Name: 'TestClaimset' Resource name: 'childResourceClaim1a' Authorization strategy: 'InvalidAuthStrategy'."
                );
        }

        [Test]
        public async Task Given_duplicate_resource_claims_at_root_level_should_return_validation_error()
        {
            // Arrange
            var existingResourceClaims = new List<ResourceClaim>
            {
                new()
                {
                    Name = "resourceClaim1",
                    Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies =
                            [
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            ],
                        },
                    ],
                },
                new()
                {
                    Name = "resourceClaim1",
                    Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies =
                            [
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            ],
                        },
                    ],
                },
            };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = existingResourceClaims };
            var validator = new Validator(
                _actions,
                _authStrategies,
                CreateResourceClaimsMap(("resourceClaim1", null))
            );

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().Be(1);
            validationResult
                .Errors[0]
                .ErrorMessage.Should()
                .Contain(
                    "Only unique resource claims can be added. The following is a duplicate resource: 'resourceClaim1'."
                );
        }

        [Test]
        public async Task Given_duplicate_resource_claims_at_different_levels_should_return_validation_error()
        {
            // Arrange
            var suppliedResourceClaims = new List<ResourceClaim>
            {
                new()
                {
                    Name = "resourceClaim1",
                    Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies =
                            [
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            ],
                        },
                    ],
                    Children =
                    [
                        new()
                        {
                            Name = "childResourceClaim1a",
                            Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                            DefaultAuthorizationStrategiesForCRUD =
                            [
                                new()
                                {
                                    AuthorizationStrategies =
                                    [
                                        new()
                                        {
                                            Id = 1,
                                            AuthorizationStrategyName = "AuthStrategy1",
                                            DisplayName = "AuthStrategy1",
                                        },
                                    ],
                                },
                            ],
                        },
                        new()
                        {
                            Name = "childResourceClaim1b",
                            Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                            DefaultAuthorizationStrategiesForCRUD =
                            [
                                new()
                                {
                                    AuthorizationStrategies =
                                    [
                                        new()
                                        {
                                            Id = 1,
                                            AuthorizationStrategyName = "AuthStrategy1",
                                            DisplayName = "AuthStrategy1",
                                        },
                                    ],
                                },
                            ],
                            Children =
                            [
                                new()
                                {
                                    // This is a duplicate resource claim
                                    Name = "childResourceClaim1a",
                                    Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                                    DefaultAuthorizationStrategiesForCRUD =
                                    [
                                        new()
                                        {
                                            AuthorizationStrategies =
                                            [
                                                new()
                                                {
                                                    Id = 1,
                                                    AuthorizationStrategyName = "AuthStrategy1",
                                                    DisplayName = "AuthStrategy1",
                                                },
                                            ],
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = suppliedResourceClaims };

            var validator = new Validator(
                _actions,
                _authStrategies,
                CreateResourceClaimsMap(
                    ("resourceClaim1", null),
                    ("childResourceClaim1a", "resourceClaim1"),
                    ("childResourceClaim1b", "resourceClaim1")
                )
            );

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().BeGreaterThan(0);

            validationResult
                .Errors.Should()
                .ContainSingle(f =>
                    f.ErrorMessage.Equals(
                        "Only unique resource claims can be added. The following is a duplicate resource: 'childResourceClaim1a'."
                    )
                );
        }

        [Test]
        public async Task Given_duplicate_resource_claims_on_same_child_level_should_return_validation_error()
        {
            // Arrange
            var existingResourceClaims = new List<ResourceClaim>
            {
                new()
                {
                    Name = "resourceClaim1",
                    Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies =
                            [
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            ],
                        },
                    ],
                    Children =
                    [
                        new()
                        {
                            Name = "childResourceClaim1a",
                            Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                            DefaultAuthorizationStrategiesForCRUD =
                            [
                                new()
                                {
                                    AuthorizationStrategies =
                                    [
                                        new()
                                        {
                                            Id = 1,
                                            AuthorizationStrategyName = "AuthStrategy1",
                                            DisplayName = "AuthStrategy1",
                                        },
                                    ],
                                },
                            ],
                        },
                        new()
                        {
                            Name = "childResourceClaim1a",
                            Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                            DefaultAuthorizationStrategiesForCRUD =
                            [
                                new()
                                {
                                    AuthorizationStrategies =
                                    [
                                        new()
                                        {
                                            Id = 1,
                                            AuthorizationStrategyName = "AuthStrategy1",
                                            DisplayName = "AuthStrategy1",
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = existingResourceClaims };

            var validator = new Validator(
                _actions,
                _authStrategies,
                CreateResourceClaimsMap(("resourceClaim1", null), ("childResourceClaim1a", "resourceClaim1a"))
            );

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().BeGreaterThan(0);
            validationResult
                .Errors.Should()
                .ContainSingle(f =>
                    f.ErrorMessage.Equals(
                        "Only unique resource claims can be added. The following is a duplicate resource: 'childResourceClaim1a'."
                    )
                );
        }

        [Test]
        public async Task Given_an_invalid_auth_strategy_on_child_resource_claim()
        {
            // Arrange
            var existingResourceClaims = new List<ResourceClaim>
            {
                new()
                {
                    Name = "resourceClaim1",
                    Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                    DefaultAuthorizationStrategiesForCRUD =
                    [
                        new()
                        {
                            AuthorizationStrategies =
                            [
                                new()
                                {
                                    Id = 1,
                                    AuthorizationStrategyName = "AuthStrategy1",
                                    DisplayName = "AuthStrategy1",
                                },
                            ],
                        },
                    ],
                    Children =
                    [
                        new()
                        {
                            Name = "childResourceClaim1",
                            Actions = [new ResourceClaimAction { Enabled = true, Name = "Create" }],
                            DefaultAuthorizationStrategiesForCRUD =
                            [
                                new()
                                {
                                    AuthorizationStrategies =
                                    [
                                        new()
                                        {
                                            Id = 1,
                                            AuthorizationStrategyName = "InvalidAuthStrategy",
                                            DisplayName = "InvalidAuthStrategy",
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                },
            };

            var request = new FakeRequest { Name = "TestClaimset", ResourceClaims = existingResourceClaims };
            var validator = new Validator(
                _actions,
                _authStrategies,
                CreateResourceClaimsMap(("resourceClaim1", null), ("childResourceClaim1", "resourceClaim1"))
            );

            // Act
            var validationResult = await validator.ValidateAsync(request);

            // Assert
            validationResult.IsValid.Should().BeFalse();
            validationResult.Errors.Count.Should().Be(1);
            validationResult
                .Errors[0]
                .ErrorMessage.Should()
                .Contain(
                    "This resource claim contains an authorization strategy which is not in the system. ClaimSet Name: 'TestClaimset' Resource name: 'childResourceClaim1' Authorization strategy: 'InvalidAuthStrategy'."
                );
        }
    }
};
