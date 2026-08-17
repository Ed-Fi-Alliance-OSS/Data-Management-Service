// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
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
            _options.Administration.WorkflowTimeout.Should().Be(TimeSpan.FromHours(24));
            _options.Status.StatusObservationTimeout.Should().Be(TimeSpan.FromSeconds(5));
            _options.Status.EndpointTimeout.Should().Be(TimeSpan.FromSeconds(30));
            _options.Status.RequiredRole.Should().BeNull();
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
                    ["DataManagement:DocumentCache:Administration:WorkflowTimeout"] = "12:00:00",
                    ["DataManagement:DocumentCache:Status:StatusObservationTimeout"] = "00:00:08",
                    ["DataManagement:DocumentCache:Status:EndpointTimeout"] = "00:00:45",
                    ["DataManagement:DocumentCache:Status:RequiredRole"] = "dms-document-cache-operator",
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
            _options.Administration.WorkflowTimeout.Should().Be(TimeSpan.FromHours(12));
            _options.Status.StatusObservationTimeout.Should().Be(TimeSpan.FromSeconds(8));
            _options.Status.EndpointTimeout.Should().Be(TimeSpan.FromSeconds(45));
            _options.Status.RequiredRole.Should().Be("dms-document-cache-operator");
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

        [TestCase("Tenant\rA")]
        [TestCase("Tenant\nA")]
        [TestCase("Tenant\tA")]
        [TestCase("Tenant\u0001A")]
        public void It_should_reject_tenant_keys_that_cannot_be_sent_as_headers(string tenantKey)
        {
            bool created = DocumentCacheTargetKey.TryCreate(
                tenantKey,
                1,
                out DocumentCacheTargetKey? targetKey,
                out string? validationFailure
            );

            created.Should().BeFalse();
            targetKey.Should().BeNull();
            validationFailure.Should().Contain("Tenant HTTP header");
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
    public class Given_A_Target_TenantKey_With_Header_Invalid_Characters : DocumentCacheOptionsTests
    {
        [TestCase("Tenant\rA")]
        [TestCase("Tenant\nA")]
        [TestCase("Tenant\tA")]
        [TestCase("Tenant\u0001A")]
        public void It_should_fail_validation(string tenantKey)
        {
            DocumentCacheOptions options = BindOptions(
                new Dictionary<string, string?>
                {
                    ["DataManagement:DocumentCache:Targets:0:TenantKey"] = tenantKey,
                    ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "1",
                }
            );

            Validate(options).Failures.Should().Contain(failure => failure.Contains("Tenant HTTP header"));
        }

        [Test]
        public void It_should_allow_a_blank_tenant_key_for_the_default_tenant()
        {
            DocumentCacheOptions options = BindOptions(
                new Dictionary<string, string?>
                {
                    ["DataManagement:DocumentCache:Targets:0:TenantKey"] = "",
                    ["DataManagement:DocumentCache:Targets:0:DataStoreId"] = "1",
                }
            );

            Validate(options).Succeeded.Should().BeTrue();
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
        [TestCase("Administration:WorkflowTimeout", "00:00:00", "WorkflowTimeout")]
        [TestCase("Status:StatusObservationTimeout", "00:00:00", "StatusObservationTimeout")]
        [TestCase("Status:EndpointTimeout", "-00:00:01", "EndpointTimeout")]
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
    public class Given_Too_Large_BaselineHighWaterMark : DocumentCacheOptionsTests
    {
        private ValidateOptionsResult _validationResult = null!;

        [SetUp]
        public void Setup()
        {
            DocumentCacheOptions options = BindOptions(
                new Dictionary<string, string?>
                {
                    ["DataManagement:DocumentCache:Projector:BaselineHighWaterMark"] =
                        int.MaxValue.ToString(),
                }
            );

            _validationResult = Validate(options);
        }

        [Test]
        public void It_should_fail_validation()
        {
            _validationResult.Failed.Should().BeTrue();
            _validationResult
                .Failures.Should()
                .ContainSingle(failure =>
                    failure.Contains("Projector:BaselineHighWaterMark")
                    && failure.Contains("high-water-plus-one")
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Too_Large_CancelAfter_Timeouts : DocumentCacheOptionsTests
    {
        [TestCase("ReadAcceleration:DirectFillTimeout", "ReadAcceleration:DirectFillTimeout")]
        [TestCase("Status:StatusObservationTimeout", "Status:StatusObservationTimeout")]
        [TestCase("Status:EndpointTimeout", "Status:EndpointTimeout")]
        public void It_should_fail_validation(string settingName, string expectedFailure)
        {
            DocumentCacheOptions options = new();
            SetTimeout(options, settingName, TimeSpan.FromMilliseconds(4_294_967_295D));

            Validate(options)
                .Failures.Should()
                .ContainSingle(failure =>
                    failure.Contains(expectedFailure) && failure.Contains("4294967294")
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Maximum_Supported_CancelAfter_Timeouts : DocumentCacheOptionsTests
    {
        [TestCase("ReadAcceleration:DirectFillTimeout")]
        [TestCase("Status:StatusObservationTimeout")]
        [TestCase("Status:EndpointTimeout")]
        public void It_should_validate_successfully(string settingName)
        {
            DocumentCacheOptions options = new();
            SetTimeout(options, settingName, TimeSpan.FromMilliseconds(4_294_967_294D));

            Validate(options).Succeeded.Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Maximum_Supported_BaselineHighWaterMark : DocumentCacheOptionsTests
    {
        private ValidateOptionsResult _validationResult = null!;

        [SetUp]
        public void Setup()
        {
            DocumentCacheOptions options = BindOptions(
                new Dictionary<string, string?>
                {
                    ["DataManagement:DocumentCache:Projector:BaselineHighWaterMark"] = (
                        int.MaxValue - 1
                    ).ToString(),
                }
            );

            _validationResult = Validate(options);
        }

        [Test]
        public void It_should_validate_successfully()
        {
            _validationResult.Succeeded.Should().BeTrue();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Null_DocumentCache_Option_Groups : DocumentCacheOptionsTests
    {
        [Test]
        public void It_should_reject_null_administration_options()
        {
            DocumentCacheOptions options = new() { Administration = null! };

            Validate(options)
                .Failures.Should()
                .Contain($"{nameof(DocumentCacheOptions.Administration)} must not be null.");
        }

        [Test]
        public void It_should_reject_null_status_options()
        {
            DocumentCacheOptions options = new() { Status = null! };

            Validate(options)
                .Failures.Should()
                .Contain($"{nameof(DocumentCacheOptions.Status)} must not be null.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_DocumentCache_Status_RequiredRole : DocumentCacheOptionsTests
    {
        [Test]
        public void It_should_preserve_an_exact_valid_role_for_endpoint_mapping()
        {
            DocumentCacheStatusOptions status = new() { RequiredRole = "DMS.cache-status:Operator" };

            bool valid = status.TryGetRequiredRoleForEndpointMapping(out string? requiredRole);

            valid.Should().BeTrue();
            requiredRole.Should().Be("DMS.cache-status:Operator");
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        [TestCase(" role")]
        [TestCase("role ")]
        [TestCase("role name")]
        [TestCase("role\tname")]
        [TestCase("role,name")]
        [TestCase("role;name")]
        [TestCase("\"role\"")]
        [TestCase("'role'")]
        [TestCase("[role]")]
        [TestCase("{role}")]
        [TestCase("role\u0001")]
        public void It_should_reject_invalid_roles_for_endpoint_mapping(string? requiredRole)
        {
            DocumentCacheStatusOptions status = new() { RequiredRole = requiredRole };

            bool valid = status.TryGetRequiredRoleForEndpointMapping(out string? endpointMappingRole);

            valid.Should().BeFalse();
            endpointMappingRole.Should().BeNull();
        }

        [Test]
        public void It_should_reject_roles_longer_than_256_characters_for_endpoint_mapping()
        {
            DocumentCacheStatusOptions status = new() { RequiredRole = new string('a', 257) };

            status.TryGetRequiredRoleForEndpointMapping(out string? endpointMappingRole).Should().BeFalse();
            endpointMappingRole.Should().BeNull();
        }

        [Test]
        public void It_should_not_fail_startup_validation_for_missing_or_invalid_role_values()
        {
            DocumentCacheOptions options = new()
            {
                Status = new DocumentCacheStatusOptions { RequiredRole = "role name" },
            };

            Validate(options).Succeeded.Should().BeTrue();
        }

        [Test]
        public void It_should_omit_RequiredRole_when_status_settings_are_serialized()
        {
            DocumentCacheStatusOptions status = new()
            {
                StatusObservationTimeout = TimeSpan.FromSeconds(5),
                EndpointTimeout = TimeSpan.FromSeconds(30),
                RequiredRole = "dms-document-cache-operator",
            };

            string json = JsonSerializer.Serialize(status);

            json.Should().NotContain("RequiredRole");
            json.Should().Contain("StatusObservationTimeout");
            json.Should().Contain("EndpointTimeout");
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
            _options.Administration.WorkflowTimeout.Should().Be(TimeSpan.FromHours(24));
            _options.Status.StatusObservationTimeout.Should().Be(TimeSpan.FromSeconds(5));
            _options.Status.EndpointTimeout.Should().Be(TimeSpan.FromSeconds(30));
            _options.Status.RequiredRole.Should().BeNull();
            _validationResult.Succeeded.Should().BeTrue();
        }
    }

    private static void SetTimeout(DocumentCacheOptions options, string settingName, TimeSpan value)
    {
        switch (settingName)
        {
            case "ReadAcceleration:DirectFillTimeout":
                options.ReadAcceleration.DirectFillTimeout = value;
                break;
            case "Status:StatusObservationTimeout":
                options.Status.StatusObservationTimeout = value;
                break;
            case "Status:EndpointTimeout":
                options.Status.EndpointTimeout = value;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(settingName),
                    settingName,
                    "Unknown timeout setting."
                );
        }
    }
}
