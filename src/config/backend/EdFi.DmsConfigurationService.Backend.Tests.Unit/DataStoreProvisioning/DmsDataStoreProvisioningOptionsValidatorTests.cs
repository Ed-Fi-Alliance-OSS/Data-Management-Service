// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Secrets;
using FakeItEasy;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Options;
using static EdFi.DmsConfigurationService.Backend.DataStoreProvisioning.DmsDataStoreProvisioningOptions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.DataStoreProvisioning;

public class DmsDataStoreProvisioningOptionsValidatorTests
{
    private const string Section = "DmsDataStoreProvisioning";

    private const string ValidHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    /// <summary>A path that is fully qualified under the test host's own rules, whichever platform runs the tests.</summary>
    private static string LocalPath(string leaf) =>
        Path.GetFullPath(Path.Combine(Path.GetTempPath(), "dms-1438-validator-tests", leaf));

    private static ValidateOptionsResult Validate(
        DmsDataStoreProvisioningOptions options,
        ISecretResolver? secretResolver = null
    ) => new DmsDataStoreProvisioningOptionsValidator(secretResolver).Validate(null, options);

    private static DmsDataStoreProvisioningOptions ValidPostgresql() =>
        new()
        {
            Enabled = true,
            Provider = PostgresqlProvider,
            AdminConnectionString = "Host=db.internal;Port=5432;Username=cms_admin;Password=pw;Pooling=false",
            AdditionalProtectedDatabaseNames = ["EdFi_Admin"],
            TrustPolicyPath = LocalPath("template-trust-policy.json"),
            ArtifactDirectory = LocalPath("templates"),
            Postgresql = new() { PsqlPath = LocalPath("psql") },
            Templates = new(StringComparer.Ordinal)
            {
                ["Minimal"] = new()
                {
                    PackageId = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0",
                    PackageVersion = "1.0.335",
                },
                ["Sample"] = new()
                {
                    PackageId = "EdFi.Api.Populated.Template.PostgreSql.5.2.0",
                    PackageVersion = "1.0.335",
                },
            },
            Compatibility = new()
            {
                DataStandardVersion = "5.2.0",
                EffectiveSchemaHash = ValidHash,
                Projects = ["edfi", "tpdm"],
            },
        };

    private static DmsDataStoreProvisioningOptions ValidMssql()
    {
        DmsDataStoreProvisioningOptions options = ValidPostgresql();
        options.Provider = MssqlProvider;
        options.AdminConnectionString =
            "Server=db.internal,1433;User Id=sa;Password=pw;TrustServerCertificate=true";
        options.Templates!["Minimal"]!.PackageId = "EdFi.Api.Minimal.Template.MsSql.5.2.0";
        options.Templates!["Sample"]!.PackageId = "EdFi.Api.Populated.Template.MsSql.5.2.0";
        options.Mssql = new()
        {
            StagingPath = LocalPath("staging"),
            StagingServerPath = "/var/opt/dms-template-staging",
            StagingFileMode = "0640",
        };
        return options;
    }

    private static string[] Failures(
        DmsDataStoreProvisioningOptions options,
        ISecretResolver? resolver = null
    ) => [.. Validate(options, resolver).Failures ?? []];

    [TestFixture]
    public class Given_the_shipped_defaults
    {
        private readonly DmsDataStoreProvisioningOptions _options = new();
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup() => _result = Validate(_options);

        [Test]
        public void It_is_disabled() => _options.Enabled.Should().BeFalse();

        [Test]
        public void It_passes_with_nothing_else_set() => _result.Succeeded.Should().BeTrue();

        [Test]
        public void It_uses_the_spec_defaults()
        {
            _options.RestoreTimeout.Should().Be(TimeSpan.FromMinutes(30));
            _options.CommandTimeout.Should().Be(TimeSpan.FromMinutes(1));
            _options.LockAcquireTimeout.Should().Be(TimeSpan.FromSeconds(5));
            _options.LockHeartbeatInterval.Should().Be(TimeSpan.FromSeconds(5));
            _options.SessionTerminationTimeout.Should().Be(TimeSpan.FromSeconds(30));
            _options.OutcomeResolutionAttempts.Should().Be(3);
            _options.CleanupAttempts.Should().Be(3);
            _options.Postgresql!.PsqlPath.Should().Be("/usr/bin/psql");
            _options.Postgresql!.MinimumPsqlMajorVersion.Should().Be(18);
            _options.Mssql!.StagingFileMode.Should().Be("0640");
            _options.AdditionalProtectedDatabaseNames!.Should().BeEmpty();
            _options.Templates!.Should().BeEmpty();
        }
    }

    [TestFixture]
    public class Given_a_disabled_section_with_invalid_values
    {
        private ValidateOptionsResult _result = null!;

        [SetUp]
        public void Setup()
        {
            DmsDataStoreProvisioningOptions options = new()
            {
                Enabled = false,
                Provider = "oracle",
                RestoreTimeout = TimeSpan.Zero,
            };
            _result = Validate(options);
        }

        [Test]
        public void It_still_passes_because_nothing_is_read_while_disabled() =>
            _result.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_valid_enabled_configurations
    {
        private ValidateOptionsResult _postgresql = null!;
        private ValidateOptionsResult _mssql = null!;
        private ValidateOptionsResult _withSecrets = null!;

        [SetUp]
        public void Setup()
        {
            _postgresql = Validate(ValidPostgresql());
            _mssql = Validate(ValidMssql());

            DmsDataStoreProvisioningOptions withSecrets = ValidPostgresql();
            withSecrets.AdminConnectionString =
                "Host=db.internal;Username=${secret:dms/admin-user};Password=${secret:dms/admin-pw};SSL Mode=Require";
            _withSecrets = Validate(withSecrets, A.Fake<ISecretResolver>());
        }

        [Test]
        public void It_accepts_postgresql() => _postgresql.Succeeded.Should().BeTrue();

        [Test]
        public void It_accepts_mssql() => _mssql.Succeeded.Should().BeTrue();

        [Test]
        public void It_accepts_secret_references_when_a_resolver_is_registered() =>
            _withSecrets.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_the_provider
    {
        private string[] _missing = [];
        private string[] _unknown = [];
        private string[] _wrongCase = [];

        [SetUp]
        public void Setup()
        {
            DmsDataStoreProvisioningOptions missing = ValidPostgresql();
            missing.Provider = null;
            _missing = Failures(missing);

            DmsDataStoreProvisioningOptions unknown = ValidPostgresql();
            unknown.Provider = "sqlserver";
            _unknown = Failures(unknown);

            DmsDataStoreProvisioningOptions wrongCase = ValidPostgresql();
            wrongCase.Provider = "PostgreSQL";
            _wrongCase = Failures(wrongCase);
        }

        [Test]
        public void It_requires_a_provider() =>
            _missing.Should().ContainSingle().Which.Should().StartWith($"{Section}:Provider");

        [Test]
        public void It_rejects_the_ordinary_data_store_token() =>
            _unknown.Should().ContainSingle().Which.Should().StartWith($"{Section}:Provider");

        [Test]
        public void It_matches_the_token_exactly() =>
            _wrongCase.Should().ContainSingle().Which.Should().StartWith($"{Section}:Provider");
    }

    [TestFixture]
    public class Given_each_required_key_missing_when_enabled
    {
        private readonly Dictionary<string, string[]> _failures = [];

        [SetUp]
        public void Setup()
        {
            Dictionary<string, Action<DmsDataStoreProvisioningOptions>> removals = new()
            {
                ["AdminConnectionString"] = options => options.AdminConnectionString = " ",
                ["TrustPolicyPath"] = options => options.TrustPolicyPath = null,
                ["ArtifactDirectory"] = options => options.ArtifactDirectory = "",
                ["Templates:Minimal:PackageId"] = options => options.Templates!["Minimal"]!.PackageId = null,
                ["Templates:Minimal:PackageVersion"] = options =>
                    options.Templates!["Minimal"]!.PackageVersion = " ",
                ["Templates:Sample:PackageId"] = options => options.Templates!["Sample"]!.PackageId = "",
                ["Templates:Sample:PackageVersion"] = options =>
                    options.Templates!["Sample"]!.PackageVersion = null,
                ["Compatibility:DataStandardVersion"] = options =>
                    options.Compatibility!.DataStandardVersion = null,
                ["Compatibility:EffectiveSchemaHash"] = options =>
                    options.Compatibility!.EffectiveSchemaHash = "",
                ["Compatibility:Projects"] = options => options.Compatibility!.Projects = [],
                ["Postgresql:PsqlPath"] = options => options.Postgresql!.PsqlPath = null,
            };

            foreach ((string key, Action<DmsDataStoreProvisioningOptions> remove) in removals)
            {
                DmsDataStoreProvisioningOptions options = ValidPostgresql();
                remove(options);
                _failures[key] = Failures(options);
            }

            Dictionary<string, Action<DmsDataStoreProvisioningOptions>> mssqlRemovals = new()
            {
                ["Mssql:StagingPath"] = options => options.Mssql!.StagingPath = null,
                ["Mssql:StagingServerPath"] = options => options.Mssql!.StagingServerPath = " ",
                ["Mssql:StagingFileMode"] = options => options.Mssql!.StagingFileMode = null,
            };

            foreach ((string key, Action<DmsDataStoreProvisioningOptions> remove) in mssqlRemovals)
            {
                DmsDataStoreProvisioningOptions options = ValidMssql();
                remove(options);
                _failures[key] = Failures(options);
            }
        }

        [Test]
        public void It_reports_exactly_that_key_as_required()
        {
            using AssertionScope scope = new();
            foreach ((string key, string[] failures) in _failures)
            {
                failures
                    .Should()
                    .ContainSingle("only {0} was removed", key)
                    .Which.Should()
                    .Be($"{Section}:{key} is required.");
            }
        }

        [Test]
        public void It_covers_the_mssql_only_keys() =>
            _failures
                .Keys.Should()
                .Contain(["Mssql:StagingPath", "Mssql:StagingServerPath", "Mssql:StagingFileMode"]);
    }

    [TestFixture]
    public class Given_each_range_bound
    {
        private sealed record Bound(
            string Key,
            Action<DmsDataStoreProvisioningOptions, bool> SetBoundary,
            Action<DmsDataStoreProvisioningOptions, bool> SetOutside
        );

        private static readonly Bound[] _bounds =
        [
            new(
                "RestoreTimeout",
                (o, max) => o.RestoreTimeout = max ? TimeSpan.FromHours(6) : TimeSpan.FromMinutes(1),
                (o, max) =>
                    o.RestoreTimeout = max
                        ? TimeSpan.FromHours(6) + TimeSpan.FromTicks(1)
                        : TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1)
            ),
            new(
                "CommandTimeout",
                (o, max) => o.CommandTimeout = max ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(5),
                (o, max) =>
                    o.CommandTimeout = max
                        ? TimeSpan.FromMinutes(10) + TimeSpan.FromTicks(1)
                        : TimeSpan.FromSeconds(5) - TimeSpan.FromTicks(1)
            ),
            new(
                "LockAcquireTimeout",
                (o, max) => o.LockAcquireTimeout = max ? TimeSpan.FromSeconds(60) : TimeSpan.FromSeconds(1),
                (o, max) =>
                    o.LockAcquireTimeout = max ? TimeSpan.FromSeconds(61) : TimeSpan.FromMilliseconds(999)
            ),
            new(
                "LockHeartbeatInterval",
                (o, max) =>
                    o.LockHeartbeatInterval = max ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(1),
                (o, max) => o.LockHeartbeatInterval = max ? TimeSpan.FromSeconds(31) : TimeSpan.Zero
            ),
            new(
                "SessionTerminationTimeout",
                (o, max) =>
                    o.SessionTerminationTimeout = max ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(5),
                (o, max) =>
                    o.SessionTerminationTimeout = max
                        ? TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)
                        : TimeSpan.FromSeconds(4)
            ),
            new(
                "OutcomeResolutionAttempts",
                (o, max) => o.OutcomeResolutionAttempts = max ? 10 : 1,
                (o, max) => o.OutcomeResolutionAttempts = max ? 11 : 0
            ),
            new(
                "CleanupAttempts",
                (o, max) => o.CleanupAttempts = max ? 10 : 1,
                (o, max) => o.CleanupAttempts = max ? 11 : 0
            ),
        ];

        private readonly Dictionary<
            string,
            (string[] AtMinimum, string[] AtMaximum, string[] BelowMinimum, string[] AboveMaximum)
        > _results = [];

        [SetUp]
        public void Setup()
        {
            foreach (Bound bound in _bounds)
            {
                _results[bound.Key] = (
                    Failures(With(bound.SetBoundary, false)),
                    Failures(With(bound.SetBoundary, true)),
                    Failures(With(bound.SetOutside, false)),
                    Failures(With(bound.SetOutside, true))
                );
            }
        }

        private static DmsDataStoreProvisioningOptions With(
            Action<DmsDataStoreProvisioningOptions, bool> set,
            bool max
        )
        {
            DmsDataStoreProvisioningOptions options = ValidPostgresql();
            set(options, max);
            return options;
        }

        [Test]
        public void It_accepts_both_bounds()
        {
            using AssertionScope scope = new();
            foreach ((string key, var result) in _results)
            {
                result.AtMinimum.Should().BeEmpty("{0} at its minimum is valid", key);
                result.AtMaximum.Should().BeEmpty("{0} at its maximum is valid", key);
            }
        }

        [Test]
        public void It_rejects_values_outside_the_bounds_naming_the_key()
        {
            using AssertionScope scope = new();
            foreach ((string key, var result) in _results)
            {
                result
                    .BelowMinimum.Should()
                    .ContainSingle("{0} below minimum", key)
                    .Which.Should()
                    .StartWith($"{Section}:{key} must be between");
                result
                    .AboveMaximum.Should()
                    .ContainSingle("{0} above maximum", key)
                    .Which.Should()
                    .StartWith($"{Section}:{key} must be between");
            }
        }

        [Test]
        public void It_covers_every_bounded_setting() =>
            _results
                .Keys.Should()
                .BeEquivalentTo(
                    "RestoreTimeout",
                    "CommandTimeout",
                    "LockAcquireTimeout",
                    "LockHeartbeatInterval",
                    "SessionTerminationTimeout",
                    "OutcomeResolutionAttempts",
                    "CleanupAttempts"
                );
    }

    [TestFixture]
    public class Given_package_ids_and_versions
    {
        private readonly Dictionary<string, string[]> _ids = [];
        private readonly Dictionary<string, string[]> _versions = [];

        [SetUp]
        public void Setup()
        {
            foreach (
                string id in new[]
                {
                    ".Leading.5.2.0",
                    "Has Space.5.2.0",
                    "Bad/Char.5.2.0",
                    new string('a', 95) + ".5.2.0",
                    "EdFi.Api.Minimal.Template.PostgreSql.5.2.0",
                }
            )
            {
                DmsDataStoreProvisioningOptions options = ValidPostgresql();
                options.Templates!["Minimal"]!.PackageId = id;
                _ids[id] = Failures(options);
            }

            foreach (
                string version in new[]
                {
                    "1.0.335",
                    "1.0.335.7",
                    "1.0.335-pre.1",
                    "1.0",
                    "v1.0.0",
                    "1.0.0.0.0",
                    "1.0.335 ",
                }
            )
            {
                DmsDataStoreProvisioningOptions options = ValidPostgresql();
                options.Templates!["Minimal"]!.PackageVersion = version;
                _versions[version] = Failures(options);
            }
        }

        [Test]
        public void It_accepts_a_valid_id() =>
            _ids["EdFi.Api.Minimal.Template.PostgreSql.5.2.0"].Should().BeEmpty();

        [Test]
        public void It_rejects_a_leading_separator() =>
            _ids[".Leading.5.2.0"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("Templates:Minimal:PackageId must match");

        [Test]
        public void It_rejects_whitespace() =>
            _ids["Has Space.5.2.0"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("Templates:Minimal:PackageId must match");

        [Test]
        public void It_rejects_path_characters() =>
            _ids["Bad/Char.5.2.0"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("Templates:Minimal:PackageId must match");

        [Test]
        public void It_rejects_more_than_one_hundred_characters() =>
            _ids[new string('a', 95) + ".5.2.0"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("at most 100 characters");

        [Test]
        public void It_accepts_three_and_four_part_versions_with_a_prerelease_suffix()
        {
            _versions["1.0.335"].Should().BeEmpty();
            _versions["1.0.335.7"].Should().BeEmpty();
            _versions["1.0.335-pre.1"].Should().BeEmpty();
        }

        [Test]
        public void It_rejects_other_version_shapes()
        {
            using AssertionScope scope = new();
            foreach (string version in new[] { "1.0", "v1.0.0", "1.0.0.0.0", "1.0.335 " })
            {
                _versions[version]
                    .Should()
                    .ContainSingle("version {0}", version)
                    .Which.Should()
                    .Contain("Templates:Minimal:PackageVersion must be");
            }
        }
    }

    [TestFixture]
    public class Given_allowlist_shape_rules
    {
        private string[] _sameIdAndVersion = [];
        private string[] _sameIdDifferentVersion = [];
        private string[] _dataStandardSuffixMismatch = [];
        private string[] _extraEntry = [];
        private string[] _missingSample = [];
        private string[] _lowercaseKey = [];

        [SetUp]
        public void Setup()
        {
            DmsDataStoreProvisioningOptions same = ValidPostgresql();
            same.Templates!["Sample"]!.PackageId = "edfi.api.minimal.template.postgresql.5.2.0";
            _sameIdAndVersion = Failures(same);

            DmsDataStoreProvisioningOptions sameId = ValidPostgresql();
            sameId.Templates!["Sample"]!.PackageId = "EdFi.Api.Minimal.Template.PostgreSql.5.2.0";
            sameId.Templates!["Sample"]!.PackageVersion = "1.0.336";
            _sameIdDifferentVersion = Failures(sameId);

            DmsDataStoreProvisioningOptions suffix = ValidPostgresql();
            suffix.Templates!["Sample"]!.PackageId = "EdFi.Api.Populated.Template.PostgreSql.6.1.0";
            _dataStandardSuffixMismatch = Failures(suffix);

            DmsDataStoreProvisioningOptions extra = ValidPostgresql();
            extra.Templates!["Custom"] = new()
            {
                PackageId = "EdFi.Api.Custom.5.2.0",
                PackageVersion = "1.0.0",
            };
            _extraEntry = Failures(extra);

            DmsDataStoreProvisioningOptions missing = ValidPostgresql();
            missing.Templates!.Remove("Sample");
            _missingSample = Failures(missing);

            DmsDataStoreProvisioningOptions lowercase = ValidPostgresql();
            TemplateAllowlistEntry? sample = lowercase.Templates!["Sample"];
            lowercase.Templates!.Remove("Sample");
            lowercase.Templates!["sample"] = sample;
            _lowercaseKey = Failures(lowercase);
        }

        [Test]
        public void It_rejects_the_same_package_for_both_templates_ignoring_id_case() =>
            _sameIdAndVersion
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:Templates Minimal and Sample must name different packages.");

        [Test]
        public void It_treats_different_versions_of_one_id_as_different_packages() =>
            _sameIdDifferentVersion.Should().BeEmpty();

        [Test]
        public void It_requires_each_package_id_to_end_with_the_data_standard_version() =>
            _dataStandardSuffixMismatch
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    $"{Section}:Templates:Sample:PackageId must end with the {Section}:Compatibility:DataStandardVersion segment."
                );

        [Test]
        public void It_rejects_a_third_entry() =>
            _extraEntry
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:Templates must define exactly the entries Minimal and Sample.");

        [Test]
        public void It_rejects_a_missing_entry() =>
            _missingSample
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:Templates must define exactly the entries Minimal and Sample.");

        [Test]
        public void It_matches_entry_keys_case_sensitively() =>
            _lowercaseKey
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:Templates must define exactly the entries Minimal and Sample.");
    }

    [TestFixture]
    public class Given_compatibility_expectations
    {
        private readonly Dictionary<string, string[]> _failures = [];

        [SetUp]
        public void Setup()
        {
            Dictionary<string, Action<DmsDataStoreProvisioningOptions>> cases = new()
            {
                ["ds not dotted"] = o => o.Compatibility!.DataStandardVersion = "5",
                ["ds with letters"] = o => o.Compatibility!.DataStandardVersion = "5.2.0-pre",
                ["hash uppercase"] = o => o.Compatibility!.EffectiveSchemaHash = ValidHash.ToUpperInvariant(),
                ["hash short"] = o => o.Compatibility!.EffectiveSchemaHash = ValidHash[..63],
                ["projects without edfi"] = o => o.Compatibility!.Projects = ["tpdm"],
                ["projects duplicated by case"] = o => o.Compatibility!.Projects = ["edfi", "EdFi"],
                ["projects blank entry"] = o => o.Compatibility!.Projects = ["edfi", " "],
                ["projects uppercase edfi"] = o => o.Compatibility!.Projects = ["EDFI"],
                ["format version blank"] = o => o.Compatibility!.ApiSchemaFormatVersion = " ",
                ["mapping version blank"] = o => o.Compatibility!.RelationalMappingVersion = "",
                ["format and mapping set"] = o =>
                {
                    o.Compatibility!.ApiSchemaFormatVersion = "1.0.0";
                    o.Compatibility!.RelationalMappingVersion = "v3";
                },
            };

            foreach ((string label, Action<DmsDataStoreProvisioningOptions> mutate) in cases)
            {
                DmsDataStoreProvisioningOptions options = ValidPostgresql();
                mutate(options);
                _failures[label] = Failures(options);
            }
        }

        [Test]
        public void It_requires_a_dotted_numeric_data_standard_version()
        {
            _failures["ds not dotted"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .StartWith($"{Section}:Compatibility:DataStandardVersion must be");
            _failures["ds with letters"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .StartWith($"{Section}:Compatibility:DataStandardVersion must be");
        }

        [Test]
        public void It_requires_a_lowercase_sixty_four_hex_hash()
        {
            _failures["hash uppercase"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .StartWith($"{Section}:Compatibility:EffectiveSchemaHash must be");
            _failures["hash short"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .StartWith($"{Section}:Compatibility:EffectiveSchemaHash must be");
        }

        [Test]
        public void It_requires_edfi_among_the_projects() =>
            _failures["projects without edfi"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:Compatibility:Projects must include edfi.");

        [Test]
        public void It_accepts_edfi_in_any_case() => _failures["projects uppercase edfi"].Should().BeEmpty();

        [Test]
        public void It_rejects_projects_that_repeat_ignoring_case() =>
            _failures["projects duplicated by case"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("must not repeat");

        [Test]
        public void It_rejects_blank_project_entries() =>
            _failures["projects blank entry"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("must not contain blank entries");

        [Test]
        public void It_rejects_blank_optional_expectations()
        {
            _failures["format version blank"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:Compatibility:ApiSchemaFormatVersion must be omitted or non-blank.");
            _failures["mapping version blank"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:Compatibility:RelationalMappingVersion must be omitted or non-blank.");
        }

        [Test]
        public void It_accepts_set_optional_expectations() =>
            _failures["format and mapping set"].Should().BeEmpty();
    }

    [TestFixture]
    public class Given_the_admin_connection_string
    {
        private readonly Dictionary<string, string[]> _postgresql = [];
        private readonly Dictionary<string, string[]> _mssql = [];
        private string[] _secretsWithoutResolver = [];
        private string[] _malformedSecret = [];
        private string[] _unterminatedSecret = [];

        [SetUp]
        public void Setup()
        {
            foreach (
                string value in new[]
                {
                    "Host=db;Username=u;Password=p;Passfile=/root/.pgpass",
                    "Server=db;Username=u;Password=p",
                    "Host=db;Username=u;Password=p;SSL Mode=Require;Root Certificate=/etc/ca.crt;Timeout=15;Minimum Pool Size=0;Maximum Pool Size=5;Application Name=cms",
                    "host=db;username=u;password=p;database=postgres",
                    "Host=db;Username=u;Password=p;Database=edfi_configurationservice",
                    "Host=db;Username=u;Password=p;SSL Mode=VerifyFull",
                    "Host=db;Username=u;Password=p;SSL Mode=verify-ca",
                    "Host=db;Username=u;Password=p;SSL Mode=VerifyFull;Root Certificate=certs/root.crt",
                    "=;;=",
                    ";",
                }
            )
            {
                DmsDataStoreProvisioningOptions options = ValidPostgresql();
                options.AdminConnectionString = value;
                _postgresql[value] = Failures(options);
            }

            foreach (
                string value in new[]
                {
                    "Server=db,1433;User Id=sa;Password=p;Encrypt=true;TrustServerCertificate=true",
                    "Passfile=x",
                }
            )
            {
                DmsDataStoreProvisioningOptions options = ValidMssql();
                options.AdminConnectionString = value;
                _mssql[value] = Failures(options);
            }

            DmsDataStoreProvisioningOptions secrets = ValidPostgresql();
            secrets.AdminConnectionString = "Host=db;Username=u;Password=${secret:dms/admin-pw}";
            _secretsWithoutResolver = Failures(secrets);

            DmsDataStoreProvisioningOptions malformed = ValidPostgresql();
            malformed.AdminConnectionString = "Host=db;Username=u;Password=${secret:}";
            _malformedSecret = Failures(malformed, A.Fake<ISecretResolver>());

            DmsDataStoreProvisioningOptions unterminated = ValidPostgresql();
            unterminated.AdminConnectionString = "Host=db;Username=u;Password=${secret:dms/admin-pw";
            _unterminatedSecret = Failures(unterminated, A.Fake<ISecretResolver>());
        }

        [Test]
        public void It_rejects_passfile_for_postgresql() =>
            _postgresql["Host=db;Username=u;Password=p;Passfile=/root/.pgpass"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("may contain only the keys");

        [Test]
        public void It_rejects_host_aliases_for_postgresql() =>
            _postgresql["Server=db;Username=u;Password=p"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("may contain only the keys");

        [Test]
        public void It_accepts_the_whole_postgresql_key_subset() =>
            _postgresql[
                "Host=db;Username=u;Password=p;SSL Mode=Require;Root Certificate=/etc/ca.crt;Timeout=15;Minimum Pool Size=0;Maximum Pool Size=5;Application Name=cms"
            ]
                .Should()
                .BeEmpty();

        [Test]
        public void It_matches_keys_case_insensitively_and_accepts_database_postgres() =>
            _postgresql["host=db;username=u;password=p;database=postgres"].Should().BeEmpty();

        [Test]
        public void It_rejects_another_postgresql_database() =>
            _postgresql["Host=db;Username=u;Password=p;Database=edfi_configurationservice"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("Database must be absent or postgres");

        [Test]
        public void It_requires_a_root_certificate_for_a_verifying_postgresql_ssl_mode()
        {
            foreach (
                string value in new[]
                {
                    "Host=db;Username=u;Password=p;SSL Mode=VerifyFull",
                    "Host=db;Username=u;Password=p;SSL Mode=verify-ca",
                }
            )
            {
                _postgresql[value]
                    .Should()
                    .ContainSingle()
                    .Which.Should()
                    .Contain("must name a Root Certificate when SSL Mode is VerifyCA or VerifyFull");
            }

            _postgresql["Host=db;Username=u;Password=p;SSL Mode=VerifyFull;Root Certificate=certs/root.crt"]
                .Should()
                .BeEmpty();
        }

        [Test]
        public void It_rejects_unparseable_text() =>
            _postgresql["=;;="]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:AdminConnectionString must be a syntactically valid connection string.");

        [Test]
        public void It_rejects_a_connection_string_with_no_keywords() =>
            _postgresql[";"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:AdminConnectionString must contain at least one keyword.");

        [Test]
        public void It_does_not_apply_the_postgresql_key_subset_to_mssql()
        {
            _mssql["Server=db,1433;User Id=sa;Password=p;Encrypt=true;TrustServerCertificate=true"]
                .Should()
                .BeEmpty();
            _mssql["Passfile=x"].Should().BeEmpty();
        }

        [Test]
        public void It_requires_a_resolver_when_secrets_are_referenced() =>
            _secretsWithoutResolver
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("no ISecretResolver is registered");

        [Test]
        public void It_rejects_a_reference_without_a_name() =>
            _malformedSecret
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("malformed ${secret:<name>} reference");

        [Test]
        public void It_rejects_an_unterminated_reference() =>
            _unterminatedSecret
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("malformed ${secret:<name>} reference");
    }

    [TestFixture]
    public class Given_paths_and_names
    {
        private readonly Dictionary<string, string[]> _failures = [];

        [SetUp]
        public void Setup()
        {
            string otherConventionLocal = OperatingSystem.IsWindows()
                ? "/var/lib/dms/templates"
                : @"C:\dms\templates";
            Dictionary<string, Action<DmsDataStoreProvisioningOptions>> cases = new()
            {
                ["relative trust policy"] = o => o.TrustPolicyPath = "config/trust.json",
                ["relative artifact directory"] = o => o.ArtifactDirectory = "templates",
                ["relative psql"] = o => o.Postgresql!.PsqlPath = "psql",
                ["other convention artifact directory"] = o => o.ArtifactDirectory = otherConventionLocal,
                ["host qualified artifact directory"] = o => o.ArtifactDirectory = LocalPath("elsewhere"),
                ["relative operation directory"] = o => o.OperationDirectory = "operations",
                ["blank operation directory"] = o => o.OperationDirectory = "",
                ["host qualified operation directory"] = o => o.OperationDirectory = LocalPath("operations"),
                ["psql major below one"] = o => o.Postgresql!.MinimumPsqlMajorVersion = 0,
                ["blank protected name"] = o => o.AdditionalProtectedDatabaseNames = ["EdFi_Admin", ""],
            };

            foreach ((string label, Action<DmsDataStoreProvisioningOptions> mutate) in cases)
            {
                DmsDataStoreProvisioningOptions options = ValidPostgresql();
                mutate(options);
                _failures[label] = Failures(options);
            }

            Dictionary<string, Action<DmsDataStoreProvisioningOptions>> mssqlCases = new()
            {
                ["relative staging"] = o => o.Mssql!.StagingPath = "staging",
                ["other convention staging"] = o => o.Mssql!.StagingPath = otherConventionLocal,
                ["windows server path"] = o => o.Mssql!.StagingServerPath = @"D:\dms-staging",
                ["unix server path"] = o => o.Mssql!.StagingServerPath = "/var/opt/dms-template-staging",
                ["unc server path"] = o => o.Mssql!.StagingServerPath = @"\\fileserver\dms-staging",
                ["relative server path"] = o => o.Mssql!.StagingServerPath = "staging",
            };

            foreach ((string label, Action<DmsDataStoreProvisioningOptions> mutate) in mssqlCases)
            {
                DmsDataStoreProvisioningOptions options = ValidMssql();
                mutate(options);
                _failures[label] = Failures(options);
            }
        }

        [Test]
        public void It_requires_local_paths_to_be_fully_qualified_on_the_host()
        {
            using AssertionScope scope = new();
            _failures["relative trust policy"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    $"{Section}:TrustPolicyPath must be a fully qualified path on the Configuration Service host."
                );
            _failures["relative artifact directory"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    $"{Section}:ArtifactDirectory must be a fully qualified path on the Configuration Service host."
                );
            _failures["relative psql"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    $"{Section}:Postgresql:PsqlPath must be a fully qualified path on the Configuration Service host."
                );
            _failures["relative staging"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    $"{Section}:Mssql:StagingPath must be a fully qualified path on the Configuration Service host."
                );
        }

        [Test]
        public void It_rejects_a_local_path_in_the_other_platforms_convention()
        {
            _failures["other convention artifact directory"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .StartWith($"{Section}:ArtifactDirectory must be a fully qualified path");
            _failures["other convention staging"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .StartWith($"{Section}:Mssql:StagingPath must be a fully qualified path");
        }

        [Test]
        public void It_accepts_a_host_qualified_local_path() =>
            _failures["host qualified artifact directory"].Should().BeEmpty();

        [Test]
        public void It_requires_an_operation_directory_to_be_omitted_or_fully_qualified()
        {
            const string Expected =
                $"{Section}:OperationDirectory must be omitted or a fully qualified path on the Configuration Service host.";
            using AssertionScope scope = new();
            _failures["relative operation directory"].Should().ContainSingle().Which.Should().Be(Expected);
            _failures["blank operation directory"].Should().ContainSingle().Which.Should().Be(Expected);
            _failures["host qualified operation directory"].Should().BeEmpty();
        }

        [Test]
        public void It_accepts_server_paths_in_either_convention()
        {
            _failures["windows server path"].Should().BeEmpty();
            _failures["unix server path"].Should().BeEmpty();
            _failures["unc server path"].Should().BeEmpty();
        }

        [Test]
        public void It_rejects_a_relative_server_path() =>
            _failures["relative server path"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be(
                    $"{Section}:Mssql:StagingServerPath must be an absolute path in the database server's convention."
                );

        [Test]
        public void It_requires_a_positive_psql_major_floor() =>
            _failures["psql major below one"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:Postgresql:MinimumPsqlMajorVersion must be at least 1.");

        [Test]
        public void It_rejects_blank_protected_names() =>
            _failures["blank protected name"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:AdditionalProtectedDatabaseNames must not contain blank entries.");
    }

    [TestFixture]
    public class Given_the_staging_file_mode
    {
        private readonly Dictionary<string, string[]> _failures = [];

        [SetUp]
        public void Setup()
        {
            foreach (
                string mode in new[]
                {
                    "0640",
                    "640",
                    "0600",
                    "0750",
                    "0660",
                    "0642",
                    "0620",
                    "0646",
                    "0640x",
                    "08",
                    "1640",
                    "0640 ",
                }
            )
            {
                DmsDataStoreProvisioningOptions options = ValidMssql();
                options.Mssql!.StagingFileMode = mode;
                _failures[mode] = Failures(options);
            }
        }

        [Test]
        public void It_accepts_modes_without_group_or_other_write()
        {
            using AssertionScope scope = new();
            foreach (string mode in new[] { "0640", "640", "0600", "0750" })
            {
                _failures[mode].Should().BeEmpty("mode {0}", mode);
            }
        }

        [Test]
        public void It_rejects_group_write()
        {
            _failures["0660"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("must not grant write permission to group or others");
            _failures["0620"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("must not grant write permission to group or others");
        }

        [Test]
        public void It_rejects_other_write()
        {
            _failures["0642"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("must not grant write permission to group or others");
            _failures["0646"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Contain("must not grant write permission to group or others");
        }

        [Test]
        public void It_rejects_non_octal_shapes()
        {
            using AssertionScope scope = new();
            foreach (string mode in new[] { "0640x", "08", "1640", "0640 " })
            {
                _failures[mode]
                    .Should()
                    .ContainSingle("mode {0}", mode)
                    .Which.Should()
                    .Contain("must be three or four octal digits");
            }
        }
    }

    [TestFixture]
    public class Given_values_that_must_never_be_echoed
    {
        private const string Password = "SENTINEL-PW-7f3a";
        private const string Host = "SENTINEL-HOST-9c1b";
        private const string PackageId = "SENTINEL ID 2d4e";
        private const string Version = "SENTINEL-VERSION-5a6b";
        private const string PathValue = "SENTINEL-PATH-8e7d";
        private const string Hash = "SENTINEL-HASH-1a2b";
        private const string Project = "SENTINEL-PROJECT-3c4d";
        private const string Mode = "SENTINEL-MODE-6e5f";
        private string[] _failures = [];

        [SetUp]
        public void Setup()
        {
            DmsDataStoreProvisioningOptions options = ValidMssql();
            options.AdminConnectionString = $"Server={Host};Password={Password};Passfile={Password}";
            options.Provider = PostgresqlProvider;
            options.TrustPolicyPath = PathValue;
            options.ArtifactDirectory = PathValue;
            options.OperationDirectory = PathValue;
            options.Postgresql!.PsqlPath = PathValue;
            options.Templates!["Minimal"]!.PackageId = PackageId;
            options.Templates!["Sample"]!.PackageVersion = Version;
            options.Compatibility!.EffectiveSchemaHash = Hash;
            options.Compatibility!.DataStandardVersion = Version;
            options.Compatibility!.Projects = [Project, Project];
            options.Mssql!.StagingFileMode = Mode;
            options.RestoreTimeout = TimeSpan.FromDays(1);
            _failures = Failures(options);
        }

        [Test]
        public void It_reports_many_failures() => _failures.Should().HaveCountGreaterThan(8);

        [Test]
        public void It_echoes_no_configured_value()
        {
            string joined = string.Join("\n", _failures);
            joined
                .Should()
                .NotContainAny(
                    Password,
                    Host,
                    PackageId,
                    Version,
                    PathValue,
                    Hash,
                    Project,
                    Mode,
                    "1.00:00:00"
                );
        }

        [Test]
        public void It_names_the_section_in_every_message() =>
            _failures.Should().OnlyContain(message => message.StartsWith(Section + ":"));
    }

    [TestFixture]
    public class Given_null_sections_entries_and_collections
    {
        private readonly Dictionary<string, string[]> _failures = [];
        private ValidateOptionsResult _disabledWithNulls = null!;

        [SetUp]
        public void Setup()
        {
            Dictionary<string, Action<DmsDataStoreProvisioningOptions>> cases = new()
            {
                ["null Minimal entry"] = o => o.Templates!["Minimal"] = null,
                ["null Templates"] = o => o.Templates = null,
                ["null Compatibility"] = o => o.Compatibility = null,
                ["null Projects"] = o => o.Compatibility!.Projects = null,
                ["null project element"] = o => o.Compatibility!.Projects = ["edfi", null!],
                ["null Postgresql"] = o => o.Postgresql = null,
                ["null protected names"] = o => o.AdditionalProtectedDatabaseNames = null,
                ["null protected element"] = o => o.AdditionalProtectedDatabaseNames = [null!],
            };

            foreach ((string label, Action<DmsDataStoreProvisioningOptions> mutate) in cases)
            {
                DmsDataStoreProvisioningOptions options = ValidPostgresql();
                mutate(options);
                _failures[label] = Failures(options);
            }

            DmsDataStoreProvisioningOptions nullMssql = ValidMssql();
            nullMssql.Mssql = null;
            _failures["null Mssql"] = Failures(nullMssql);

            _disabledWithNulls = Validate(
                new DmsDataStoreProvisioningOptions
                {
                    Enabled = false,
                    Templates = null,
                    Compatibility = null,
                    Postgresql = null,
                    Mssql = null,
                    AdditionalProtectedDatabaseNames = null,
                }
            );
        }

        [Test]
        public void It_reports_a_null_allowlist_entry_as_required() =>
            _failures["null Minimal entry"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:Templates:Minimal is required.");

        [Test]
        public void It_reports_a_null_templates_section() =>
            _failures["null Templates"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:Templates must define exactly the entries Minimal and Sample.");

        [Test]
        public void It_reports_a_null_compatibility_section() =>
            _failures["null Compatibility"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:Compatibility is required.");

        [Test]
        public void It_reports_null_projects_as_required() =>
            _failures["null Projects"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:Compatibility:Projects is required.");

        [Test]
        public void It_treats_a_null_project_element_as_blank() =>
            _failures["null project element"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:Compatibility:Projects must not contain blank entries.");

        [Test]
        public void It_reports_a_null_provider_section()
        {
            _failures["null Postgresql"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:Postgresql is required.");
            _failures["null Mssql"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:Mssql is required.");
        }

        [Test]
        public void It_treats_null_protected_names_as_none() =>
            _failures["null protected names"].Should().BeEmpty();

        [Test]
        public void It_treats_a_null_protected_element_as_blank() =>
            _failures["null protected element"]
                .Should()
                .ContainSingle()
                .Which.Should()
                .Be($"{Section}:AdditionalProtectedDatabaseNames must not contain blank entries.");

        [Test]
        public void It_keeps_the_disabled_short_circuit_with_null_sections() =>
            _disabledWithNulls.Succeeded.Should().BeTrue();
    }

    [TestFixture]
    public class Given_values_with_a_trailing_line_break
    {
        private readonly Dictionary<string, string[]> _failures = [];

        /// <summary>The grammar rule each field must report, not a later rule that happens to fail as well.</summary>
        private static readonly Dictionary<string, string> _expectedRule = new()
        {
            ["Provider"] = "must be exactly postgresql or mssql.",
            ["Templates:Minimal:PackageId"] =
                "must match the NuGet package id grammar and be at most 100 characters.",
            ["Templates:Minimal:PackageVersion"] =
                "must be a three- or four-part numeric version with an optional prerelease suffix.",
            ["Compatibility:DataStandardVersion"] = "must be a dotted numeric version such as 5.2.0.",
            ["Compatibility:EffectiveSchemaHash"] = "must be 64 lowercase hexadecimal characters.",
            ["Mssql:StagingFileMode"] = "must be three or four octal digits such as 0640.",
        };

        [SetUp]
        public void Setup()
        {
            foreach ((string ending, string suffix) in new[] { ("LF", "\n"), ("CRLF", "\r\n") })
            {
                Dictionary<string, Action<DmsDataStoreProvisioningOptions>> cases = new()
                {
                    ["Provider"] = o => o.Provider = "postgresql" + suffix,
                    ["Templates:Minimal:PackageId"] = o =>
                        o.Templates!["Minimal"]!.PackageId =
                            "EdFi.Api.Minimal.Template.PostgreSql.5.2.0" + suffix,
                    ["Templates:Minimal:PackageVersion"] = o =>
                        o.Templates!["Minimal"]!.PackageVersion = "1.0.335" + suffix,
                    ["Compatibility:DataStandardVersion"] = o =>
                        o.Compatibility!.DataStandardVersion = "5.2.0" + suffix,
                    ["Compatibility:EffectiveSchemaHash"] = o =>
                        o.Compatibility!.EffectiveSchemaHash = ValidHash + suffix,
                };

                foreach ((string key, Action<DmsDataStoreProvisioningOptions> mutate) in cases)
                {
                    DmsDataStoreProvisioningOptions options = ValidPostgresql();
                    mutate(options);
                    _failures[$"{ending} {key}"] = Failures(options);
                }

                DmsDataStoreProvisioningOptions mssql = ValidMssql();
                mssql.Mssql!.StagingFileMode = "0640" + suffix;
                _failures[$"{ending} Mssql:StagingFileMode"] = Failures(mssql);
            }
        }

        [Test]
        public void It_rejects_each_constrained_field_with_lf_or_crlf()
        {
            using AssertionScope scope = new();
            foreach ((string label, string[] failures) in _failures)
            {
                string key = label[(label.IndexOf(' ') + 1)..];
                failures
                    .Should()
                    .ContainSingle("{0}", label)
                    .Which.Should()
                    .Be($"{Section}:{key} {_expectedRule[key]}");
            }
        }

        [Test]
        public void It_covers_both_endings_for_every_field() => _failures.Should().HaveCount(12);
    }
}
