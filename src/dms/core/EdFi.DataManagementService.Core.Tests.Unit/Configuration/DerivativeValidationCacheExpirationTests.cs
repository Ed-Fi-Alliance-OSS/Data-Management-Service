// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

[TestFixture]
[Parallelizable]
public class DerivativeValidationCacheExpirationTests
{
    [TestFixture]
    [Parallelizable]
    public class Given_The_Setting_Is_Absent : DerivativeValidationCacheExpirationTests
    {
        private (int EffectiveSeconds, string? Warning) _resolved;

        [SetUp]
        public void Setup()
        {
            _resolved = DerivativeValidationCacheExpiration.Resolve(null);
        }

        [Test]
        public void It_uses_the_default()
        {
            _resolved.EffectiveSeconds.Should().Be(600);
        }

        /// <summary>
        /// Absence is the normal case, so it is silent. Warning about it would train operators to
        /// ignore the warnings that do mean something.
        /// </summary>
        [Test]
        public void It_warns_about_nothing()
        {
            _resolved.Warning.Should().BeNull();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Value_Inside_The_Accepted_Range : DerivativeValidationCacheExpirationTests
    {
        [TestCase(1)]
        [TestCase(60)]
        [TestCase(600)]
        [TestCase(3600)]
        public void It_uses_the_configured_value(int configured)
        {
            DerivativeValidationCacheExpiration.Resolve(configured).EffectiveSeconds.Should().Be(configured);
        }

        [TestCase(1)]
        [TestCase(60)]
        [TestCase(600)]
        [TestCase(3600)]
        public void It_warns_about_nothing(int configured)
        {
            DerivativeValidationCacheExpiration.Resolve(configured).Warning.Should().BeNull();
        }
    }

    /// <summary>
    /// The inversion that matters: elsewhere in this settings section a non-positive expiration means
    /// "keep it until an explicit reload". Here it means "use the default", and the operator is told
    /// so rather than left to assume the convention carried over.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Non_Positive_Value : DerivativeValidationCacheExpirationTests
    {
        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(-600)]
        public void It_falls_back_to_the_default(int configured)
        {
            DerivativeValidationCacheExpiration.Resolve(configured).EffectiveSeconds.Should().Be(600);
        }

        [TestCase(0)]
        [TestCase(-1)]
        [TestCase(-600)]
        public void It_warns_naming_the_configured_and_the_effective_value(int configured)
        {
            string? warning = DerivativeValidationCacheExpiration.Resolve(configured).Warning;

            warning.Should().NotBeNull();
            warning.Should().Contain(configured.ToString(System.Globalization.CultureInfo.InvariantCulture));
            warning.Should().Contain("600");
        }

        [Test]
        public void It_says_that_expiration_is_not_disabled()
        {
            DerivativeValidationCacheExpiration
                .Resolve(0)
                .Warning.Should()
                .Contain("does not disable expiration");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Value_Above_The_Maximum : DerivativeValidationCacheExpirationTests
    {
        [TestCase(3601)]
        [TestCase(86400)]
        [TestCase(int.MaxValue)]
        public void It_clamps_to_the_maximum(int configured)
        {
            DerivativeValidationCacheExpiration.Resolve(configured).EffectiveSeconds.Should().Be(3600);
        }

        [TestCase(3601)]
        [TestCase(86400)]
        public void It_warns_naming_the_configured_and_the_effective_value(int configured)
        {
            string? warning = DerivativeValidationCacheExpiration.Resolve(configured).Warning;

            warning.Should().NotBeNull();
            warning.Should().Contain(configured.ToString(System.Globalization.CultureInfo.InvariantCulture));
            warning.Should().Contain("3600");
        }
    }

    /// <summary>
    /// A verdict about a derivative must not outlive the configuration its connection string came
    /// from, so the resolved value is bounded by the data store cache TTL - but only where that TTL
    /// exists.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_The_Data_Store_Cache_Expires : DerivativeValidationCacheExpirationTests
    {
        private static CacheSettings SettingsWith(int dataStoreSeconds) =>
            new() { DataStoreCacheRefreshEnabled = true, DataStoreCacheExpirationSeconds = dataStoreSeconds };

        [Test]
        public void It_uses_the_shorter_data_store_ttl()
        {
            DerivativeValidationCacheExpiration
                .Effective(600, SettingsWith(120))
                .Should()
                .Be(TimeSpan.FromSeconds(120));
        }

        [Test]
        public void It_keeps_the_resolved_value_when_the_data_store_ttl_is_equal()
        {
            DerivativeValidationCacheExpiration
                .Effective(600, SettingsWith(600))
                .Should()
                .Be(TimeSpan.FromSeconds(600));
        }

        [Test]
        public void It_keeps_the_resolved_value_when_the_data_store_ttl_is_longer()
        {
            DerivativeValidationCacheExpiration
                .Effective(600, SettingsWith(3600))
                .Should()
                .Be(TimeSpan.FromSeconds(600));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_The_Data_Store_Cache_Does_Not_Expire : DerivativeValidationCacheExpirationTests
    {
        /// <summary>
        /// With refresh disabled the data store configuration is held until an explicit reload, so
        /// there is no shorter lifetime to bound by. The resolved value still bounds the result.
        /// </summary>
        [Test]
        public void It_keeps_the_resolved_value_when_refresh_is_disabled()
        {
            CacheSettings cacheSettings = new()
            {
                DataStoreCacheRefreshEnabled = false,
                DataStoreCacheExpirationSeconds = 60,
            };

            DerivativeValidationCacheExpiration
                .Effective(600, cacheSettings)
                .Should()
                .Be(TimeSpan.FromSeconds(600));
        }

        [TestCase(0)]
        [TestCase(-1)]
        public void It_keeps_the_resolved_value_when_the_data_store_ttl_is_not_positive(int dataStoreSeconds)
        {
            CacheSettings cacheSettings = new()
            {
                DataStoreCacheRefreshEnabled = true,
                DataStoreCacheExpirationSeconds = dataStoreSeconds,
            };

            DerivativeValidationCacheExpiration
                .Effective(600, cacheSettings)
                .Should()
                .Be(TimeSpan.FromSeconds(600));
        }

        /// <summary>
        /// A non-positive data store TTL must not become the derivative expiration. Taking the minimum
        /// unconditionally would produce a zero or negative lifetime, which is the one thing this
        /// setting is not allowed to express.
        /// </summary>
        [Test]
        public void It_never_produces_a_non_positive_expiration()
        {
            CacheSettings cacheSettings = new()
            {
                DataStoreCacheRefreshEnabled = true,
                DataStoreCacheExpirationSeconds = -600,
            };

            DerivativeValidationCacheExpiration
                .Effective(DerivativeValidationCacheExpiration.Minimum, cacheSettings)
                .Should()
                .BeGreaterThan(TimeSpan.Zero);
        }
    }
}
