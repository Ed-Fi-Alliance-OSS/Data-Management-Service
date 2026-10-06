// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.OpenIddict.Models;
using EdFi.DmsConfigurationService.Backend.OpenIddict.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

/// <summary>
/// The stored hash carries a version byte, a salt length, the salt, and the subkey, but not the
/// iteration count, so verification derives at whatever count is configured now. These fixtures pin
/// the operator-visible consequence documented in CONFIGURATION.md: changing the count invalidates
/// every secret hashed at the old one.
/// </summary>
public class ClientSecretHasherTests
{
    private const string Secret = "Sup3r-Secret-Client-Value-0123456789";

    // Low counts keep the fixtures fast; the property under test does not depend on magnitude.
    private const int HashedAtIterations = 1000;
    private const int VerifiedAtIterations = 2000;

    private static ClientSecretHasher CreateHasher(int iterations) =>
        new(
            NullLogger<ClientSecretHasher>.Instance,
            Options.Create(new IdentityOptions { ClientSecretHashingIterations = iterations })
        );

    [TestFixture]
    public class Given_a_secret_verified_at_the_count_it_was_hashed_at
    {
        private bool _verified;

        [SetUp]
        public async Task Setup()
        {
            string hash = await CreateHasher(HashedAtIterations).HashSecretAsync(Secret);
            _verified = await CreateHasher(HashedAtIterations).VerifySecretAsync(Secret, hash);
        }

        [Test]
        public void It_verifies() => _verified.Should().BeTrue();
    }

    [TestFixture]
    public class Given_a_secret_verified_at_a_different_count_than_it_was_hashed_at
    {
        private bool _verified;

        [SetUp]
        public async Task Setup()
        {
            string hash = await CreateHasher(HashedAtIterations).HashSecretAsync(Secret);
            _verified = await CreateHasher(VerifiedAtIterations).VerifySecretAsync(Secret, hash);
        }

        [Test]
        public void It_fails_verification() => _verified.Should().BeFalse();
    }
}
