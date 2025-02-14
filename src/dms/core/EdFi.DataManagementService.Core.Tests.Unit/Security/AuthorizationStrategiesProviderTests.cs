// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Security;
using EdFi.DataManagementService.Core.Security.Model;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Security;

public class AuthorizationStrategiesProviderTests
{
    [TestFixture]
    public class Given_ResourceClaim_Has_Default_AuthStrategies : AuthorizationStrategiesProviderTests
    {
        private readonly string _expectedAuthStrategy = "auth-strategy1";
        private IReadOnlyList<string>? _authStrategyList = null;

        [SetUp]
        public void Setup()
        {
            var resourceClaim = new ResourceClaim()
            {
                Name = "schools",
                Actions = [new(Enabled: true, Name: "Create")],
                AuthorizationStrategyOverridesForCrud = [],
                DefaultAuthorizationStrategiesForCrud =
                [
                    new(
                        ActionId: 1,
                        ActionName: "Create",
                        AuthorizationStrategies: [new() { AuthStrategyName = _expectedAuthStrategy }]
                    ),
                ],
            };

            var provider = new AuthorizationStrategiesProvider();
            _authStrategyList = provider.GetAuthorizationStrategies(resourceClaim, "Create");
        }

        [Test]
        public void Should_Return_Expected_AuthorizationStrategy()
        {
            _authStrategyList.Should().NotBeNull();
            _authStrategyList!.Count.Should().Be(1);
            _authStrategyList[0].Should().Be(_expectedAuthStrategy);
        }
    }

    [TestFixture]
    public class Given_ResourceClaim_Has_AuthStrategy_Overrides : AuthorizationStrategiesProviderTests
    {
        private readonly string _expectedAuthStrategy = "auth-strategy-override";
        private IReadOnlyList<string>? _authStrategyList = null;

        [SetUp]
        public void Setup()
        {
            var resourceClaim = new ResourceClaim()
            {
                Name = "schools",
                Actions = [new(Enabled: true, Name: "Create")],
                AuthorizationStrategyOverridesForCrud =
                [
                    new(
                        ActionId: 1,
                        ActionName: "Create",
                        AuthorizationStrategies: [new() { AuthStrategyName = _expectedAuthStrategy }]
                    ),
                ],
                DefaultAuthorizationStrategiesForCrud = [],
            };

            var provider = new AuthorizationStrategiesProvider();
            _authStrategyList = provider.GetAuthorizationStrategies(resourceClaim, "Create");
        }

        [Test]
        public void Should_Return_Expected_AuthorizationStrategy()
        {
            _authStrategyList.Should().NotBeNull();
            _authStrategyList!.Count.Should().Be(1);
            _authStrategyList[0].Should().Be(_expectedAuthStrategy);
        }
    }

    [TestFixture]
    public class Given_ResourceClaim_Has_AuthStrategies_On_Overrides_And_Default_List
        : AuthorizationStrategiesProviderTests
    {
        private readonly string _expectedAuthStrategyOverride = "auth-strategy-override";
        private readonly string _expectedAuthStrategyDefault = "auth-strategy-default";
        private IReadOnlyList<string>? _authStrategyList = null;

        [SetUp]
        public void Setup()
        {
            var resourceClaim = new ResourceClaim()
            {
                Name = "schools",
                Actions = [new(Enabled: true, Name: "Create")],
                AuthorizationStrategyOverridesForCrud =
                [
                    new(
                        ActionId: 1,
                        ActionName: "Create",
                        AuthorizationStrategies: [new() { AuthStrategyName = _expectedAuthStrategyOverride }]
                    ),
                ],
                DefaultAuthorizationStrategiesForCrud =
                [
                    new(
                        ActionId: 1,
                        ActionName: "Create",
                        AuthorizationStrategies: [new() { AuthStrategyName = _expectedAuthStrategyDefault }]
                    ),
                ],
            };

            var provider = new AuthorizationStrategiesProvider();
            _authStrategyList = provider.GetAuthorizationStrategies(resourceClaim, "Create");
        }

        [Test]
        public void Should_Return_AuthorizationStrategy_From_Overrides()
        {
            _authStrategyList.Should().NotBeNull();
            _authStrategyList!.Count.Should().Be(1);
            _authStrategyList[0].Should().Be(_expectedAuthStrategyOverride);
        }
    }
}
