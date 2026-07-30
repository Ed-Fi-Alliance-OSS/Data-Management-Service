// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Configuration;

[TestFixture]
[Parallelizable]
public class DocumentCacheOptionsTests
{
    private static DocumentCacheOptions BindOptions(Dictionary<string, string?> configurationValues)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configurationValues)
            .Build();

        DocumentCacheOptions options = new();
        configuration.GetSection(DocumentCacheOptions.SectionName).Bind(options);
        return options;
    }

    private static ValidateOptionsResult Validate(DocumentCacheOptions options) =>
        new DocumentCacheOptionsValidator().Validate(null, options);

    [TestFixture]
    [Parallelizable]
    public class Given_Default_DocumentCacheOptions : DocumentCacheOptionsTests
    {
        private DocumentCacheOptions _options = null!;
        private ValidateOptionsResult _validationResult = null!;

        [SetUp]
        public void Setup()
        {
            _options = BindOptions([]);
            _validationResult = Validate(_options);
        }

        [Test]
        public void It_should_use_the_story_owned_defaults()
        {
            _options.Targets.Should().BeEmpty();
            _options.ReadAcceleration.Enabled.Should().BeFalse();
            _options.ReadAcceleration.DirectFillTimeout.Should().Be(TimeSpan.FromMilliseconds(250));
            _options.Projector.PollInterval.Should().Be(TimeSpan.FromSeconds(5));
            _options.Projector.PageSize.Should().Be(100);
            _options.Projector.MaxConcurrentTargets.Should().Be(2);
            _options.Projector.FailureBackoff.Should().Be(TimeSpan.FromSeconds(30));
            _options.Projector.BaselineHighWaterMark.Should().Be(1000);
        }

        [Test]
        public void It_should_validate_successfully()
        {
            _validationResult.Succeeded.Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Configured_DocumentCacheOptions : DocumentCacheOptionsTests
    {
        private DocumentCacheOptions _options = null!;
        private ValidateOptionsResult _validationResult = null!;

        [SetUp]
        public void Setup()
        {
            _options = BindOptions(
                new Dictionary<string, string?>
                {
                    ["DataManagement:DocumentCache:Targets:0:TenantKey"] = "TenantA",
                    ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "7",
                    ["DataManagement:DocumentCache:ReadAcceleration:Enabled"] = "true",
                    ["DataManagement:DocumentCache:ReadAcceleration:DirectFillTimeout"] = "00:00:00.125",
                    ["DataManagement:DocumentCache:Projector:PollInterval"] = "00:00:07",
                    ["DataManagement:DocumentCache:Projector:PageSize"] = "25",
                    ["DataManagement:DocumentCache:Projector:MaxConcurrentTargets"] = "4",
                    ["DataManagement:DocumentCache:Projector:FailureBackoff"] = "00:01:15",
                    ["DataManagement:DocumentCache:Projector:BaselineHighWaterMark"] = "2500",
                }
            );
            _validationResult = Validate(_options);
        }

        [Test]
        public void It_should_bind_from_DataManagement_DocumentCache()
        {
            _options.Targets.Should().ContainSingle();
            _options.Targets[0].TenantKey.Should().Be("TenantA");
            _options.Targets[0].DataStoreId.Should().Be(7);
            _options.ReadAcceleration.Enabled.Should().BeTrue();
            _options.ReadAcceleration.DirectFillTimeout.Should().Be(TimeSpan.FromMilliseconds(125));
            _options.Projector.PollInterval.Should().Be(TimeSpan.FromSeconds(7));
            _options.Projector.PageSize.Should().Be(25);
            _options.Projector.MaxConcurrentTargets.Should().Be(4);
            _options.Projector.FailureBackoff.Should().Be(TimeSpan.FromSeconds(75));
            _options.Projector.BaselineHighWaterMark.Should().Be(2500);
        }

        [Test]
        public void It_should_validate_successfully()
        {
            _validationResult.Succeeded.Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_DocumentCache_TargetKeys : DocumentCacheOptionsTests
    {
        [Test]
        public void It_should_compare_tenant_keys_case_insensitively()
        {
            DocumentCacheTargetKey upper = DocumentCacheTargetKey.Create("TenantA", 1);
            DocumentCacheTargetKey lower = DocumentCacheTargetKey.Create("tenanta", 1);

            upper.Should().Be(lower);
            new HashSet<DocumentCacheTargetKey> { upper, lower }
                .Should()
                .ContainSingle();
        }

        [Test]
        public void It_should_represent_the_default_tenant_as_an_empty_string()
        {
            DocumentCacheTargetKey missingTenant = DocumentCacheTargetKey.Create(null, 1);
            DocumentCacheTargetKey emptyTenant = DocumentCacheTargetKey.Create(string.Empty, 2);

            missingTenant.TenantKey.Should().BeEmpty();
            emptyTenant.TenantKey.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Empty_Targets_With_ReadAcceleration_Enabled : DocumentCacheOptionsTests
    {
        private ValidateOptionsResult _validationResult = null!;

        [SetUp]
        public void Setup()
        {
            DocumentCacheOptions options = new()
            {
                Targets = [],
                ReadAcceleration = new DocumentCacheReadAccelerationOptions { Enabled = true },
            };

            _validationResult = Validate(options);
        }

        [Test]
        public void It_should_select_no_targets_and_remain_valid()
        {
            _validationResult.Succeeded.Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Duplicate_Targets_After_Tenant_Normalization : DocumentCacheOptionsTests
    {
        private ValidateOptionsResult _validationResult = null!;

        [SetUp]
        public void Setup()
        {
            DocumentCacheOptions options = BindOptions(
                new Dictionary<string, string?>
                {
                    ["DataManagement:DocumentCache:Targets:0:TenantKey"] = "TenantA",
                    ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "1",
                    ["DataManagement:DocumentCache:Targets:1:TenantKey"] = "tenanta",
                    ["DataManagement:DocumentCache:Targets:1:DataStoreId"] = "1",
                }
            );

            _validationResult = Validate(options);
        }

        [Test]
        public void It_should_fail_validation()
        {
            _validationResult.Failed.Should().BeTrue();
            _validationResult.Failures.Should().Contain(failure => failure.Contains("duplicates"));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Target_TenantKey_With_Leading_Or_Trailing_Whitespace : DocumentCacheOptionsTests
    {
        [TestCase(" TenantA")]
        [TestCase("TenantA ")]
        [TestCase("\tTenantA")]
        public void It_should_fail_validation(string tenantKey)
        {
            DocumentCacheOptions options = BindOptions(
                new Dictionary<string, string?>
                {
                    ["DataManagement:DocumentCache:Targets:0:TenantKey"] = tenantKey,
                    ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "1",
                }
            );

            Validate(options).Failures.Should().Contain(failure => failure.Contains("TenantKey"));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Nonpositive_DocumentCache_Settings : DocumentCacheOptionsTests
    {
        [TestCase("Projector:PollInterval", "00:00:00", "PollInterval")]
        [TestCase("Projector:PageSize", "0", "PageSize")]
        [TestCase("Projector:MaxConcurrentTargets", "-1", "MaxConcurrentTargets")]
        [TestCase("Projector:FailureBackoff", "00:00:00", "FailureBackoff")]
        [TestCase("Projector:BaselineHighWaterMark", "0", "BaselineHighWaterMark")]
        [TestCase("ReadAcceleration:DirectFillTimeout", "00:00:00", "DirectFillTimeout")]
        [TestCase("Targets:0:DataStoreId", "0", "DataStoreId")]
        public void It_should_fail_validation(string settingName, string settingValue, string expectedFailure)
        {
            DocumentCacheOptions options = BindOptions(
                new Dictionary<string, string?>
                {
                    [$"DataManagement:DocumentCache:{settingName}"] = settingValue,
                }
            );

            Validate(options).Failures.Should().Contain(failure => failure.Contains(expectedFailure));
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Legacy_DocumentCache_Settings : DocumentCacheOptionsTests
    {
        private DocumentCacheOptions _options = null!;
        private ValidateOptionsResult _validationResult = null!;

        [SetUp]
        public void Setup()
        {
            _options = BindOptions(
                new Dictionary<string, string?>
                {
                    ["DataManagement:DocumentCache:Scan:PollInterval"] = "00:00:01",
                    ["DataManagement:DocumentCache:Audit:Enabled"] = "true",
                    ["DataManagement:DocumentCache:SourcePoll:PollInterval"] = "00:00:01",
                    ["DataManagement:DocumentCache:ScanAgeReadiness:MaximumAge"] = "00:00:01",
                    ["DataManagement:DocumentCache:ExactCountBacklog:Enabled"] = "true",
                    ["DataManagement:DocumentCache:ExactCountBacklog:PageSize"] = "1",
                }
            );
            _validationResult = Validate(_options);
        }

        [Test]
        public void It_should_not_consume_them_as_aliases_for_the_new_options()
        {
            _options.Targets.Should().BeEmpty();
            _options.ReadAcceleration.Enabled.Should().BeFalse();
            _options.ReadAcceleration.DirectFillTimeout.Should().Be(TimeSpan.FromMilliseconds(250));
            _options.Projector.PollInterval.Should().Be(TimeSpan.FromSeconds(5));
            _options.Projector.PageSize.Should().Be(100);
            _options.Projector.MaxConcurrentTargets.Should().Be(2);
            _options.Projector.FailureBackoff.Should().Be(TimeSpan.FromSeconds(30));
            _options.Projector.BaselineHighWaterMark.Should().Be(1000);
            _validationResult.Succeeded.Should().BeTrue();
        }
    }
}
