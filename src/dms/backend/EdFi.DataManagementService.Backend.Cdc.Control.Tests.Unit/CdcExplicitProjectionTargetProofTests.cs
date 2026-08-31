// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Reflection;
using EdFi.DataManagementService.Core.Configuration;
using EdFi.DataManagementService.Core.DocumentCache;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Control.Tests.Unit;

/// <summary>
/// The proof that the target is one the DMS projector itself is configured to project. It reads the
/// original configuration, because an administrative host replaces the bound target list with its own
/// invocation arguments — proving only that the operator named a target on the command line.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CdcExplicitProjectionTargetProof")]
public class Given_CdcExplicitProjectionTargetProof
{
    private static readonly DateTimeOffset ObservedAt = new(2026, 8, 28, 9, 0, 0, TimeSpan.Zero);

    [Test]
    public void It_accepts_a_configured_projection_target()
    {
        CdcExplicitProjectionTargetProofResult result = Prove(Targets(("", 1)));

        using var _ = new AssertionScope();
        result.Succeeded.Should().BeTrue();
        result.State.Should().Be(CdcExplicitProjectionTargetState.Configured);
        result.Diagnostics.Should().BeEmpty();
    }

    [Test]
    public void It_accepts_the_configured_target_among_several()
    {
        CdcExplicitProjectionTargetProofResult result = Prove(Targets(("", 7), ("", 1), ("", 9)));

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public void It_rejects_a_data_store_the_projector_is_not_configured_for()
    {
        CdcExplicitProjectionTargetProofResult result = Prove(Targets(("", 2)));

        using var _ = new AssertionScope();
        result.Succeeded.Should().BeFalse();
        result.State.Should().Be(CdcExplicitProjectionTargetState.NotConfigured);
        Diagnostic(result).Category.Should().Be(CdcDiagnosticCategory.TargetMismatch);
        Diagnostic(result).ArtifactName.Should().Be(CdcExplicitProjectionTargetProof.TargetsSectionName);
    }

    [Test]
    public void It_rejects_a_tenant_the_projector_is_not_configured_for()
    {
        CdcExplicitProjectionTargetProofResult result = Prove(Targets(("other-tenant", 1)));

        using var _ = new AssertionScope();
        result.Succeeded.Should().BeFalse();
        result.State.Should().Be(CdcExplicitProjectionTargetState.NotConfigured);
    }

    [Test]
    public void It_rejects_an_empty_configured_target_list()
    {
        // The section exists but names nothing, which is a configured projector with no target.
        CdcExplicitProjectionTargetProofResult result = Prove(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{CdcExplicitProjectionTargetProof.TargetsSectionName}:0:Unrelated"] = "value",
            }
        );

        using var _ = new AssertionScope();
        result.Succeeded.Should().BeFalse();
        result.State.Should().Be(CdcExplicitProjectionTargetState.NotConfigured);
    }

    [Test]
    public void It_fails_closed_when_the_targets_section_is_absent()
    {
        CdcExplicitProjectionTargetProofResult result = Prove(
            new Dictionary<string, string?>(StringComparer.Ordinal)
        );

        using var _ = new AssertionScope();
        result.Succeeded.Should().BeFalse();
        result.State.Should().Be(CdcExplicitProjectionTargetState.SectionMissing);
        Diagnostic(result).Retryable.Should().BeFalse();
    }

    [Test]
    public void It_fails_closed_when_the_configuration_source_cannot_be_read()
    {
        CdcExplicitProjectionTargetProof proof = new(new UnreadableConfiguration());

        CdcExplicitProjectionTargetProofResult result = proof.Prove(Target(), ObservedAt);

        using var _ = new AssertionScope();
        result.Succeeded.Should().BeFalse();
        result.State.Should().Be(CdcExplicitProjectionTargetState.Unreadable);

        // An unreadable source is a condition an operator can fix and retry, unlike a target the
        // projector is simply not configured for.
        Diagnostic(result).Retryable.Should().BeTrue();
    }

    [Test]
    public void It_ignores_a_configured_entry_that_is_not_a_target_pair()
    {
        CdcExplicitProjectionTargetProofResult result = Prove(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [$"{CdcExplicitProjectionTargetProof.TargetsSectionName}:0:TenantKey"] = "",
                [$"{CdcExplicitProjectionTargetProof.TargetsSectionName}:0:DataStoreId"] = "not-a-number",
            }
        );

        result.State.Should().Be(CdcExplicitProjectionTargetState.NotConfigured);
    }

    /// <summary>
    /// The bound options and the target registry are both fed the administrative host's substituted
    /// target list, so a proof that consulted either would answer its own question with the operator's
    /// command line. This asserts the dependency cannot be reintroduced without being noticed.
    /// </summary>
    [Test]
    public void It_never_consults_the_bound_document_cache_options_or_target_registry()
    {
        Type[] dependencies =
        [
            .. typeof(CdcExplicitProjectionTargetProof)
                .GetConstructors()
                .SelectMany(constructor => constructor.GetParameters())
                .Select(parameter => parameter.ParameterType),
            .. typeof(CdcExplicitProjectionTargetProof)
                .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                .Select(field => field.FieldType),
        ];

        using var _ = new AssertionScope();
        dependencies.Should().NotContain(typeof(IOptions<DocumentCacheOptions>));
        dependencies.Should().NotContain(typeof(DocumentCacheOptions));
        dependencies.Should().NotContain(typeof(IDocumentCacheTargetRegistry));
        dependencies.Should().NotContain(typeof(IServiceProvider));
        dependencies.Should().Contain(typeof(IConfiguration));
    }

    [Test]
    public void It_rejects_a_missing_target()
    {
        CdcExplicitProjectionTargetProof proof = new(Configuration(Targets(("", 1))));

        FluentActions.Invoking(() => proof.Prove(null!, ObservedAt)).Should().Throw<ArgumentNullException>();
    }

    private static CdcExplicitProjectionTargetProofResult Prove(Dictionary<string, string?> settings) =>
        new CdcExplicitProjectionTargetProof(Configuration(settings)).Prove(Target(), ObservedAt);

    private static IConfiguration Configuration(Dictionary<string, string?> settings) =>
        new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

    private static Dictionary<string, string?> Targets(params (string TenantKey, long DataStoreId)[] targets)
    {
        Dictionary<string, string?> settings = new(StringComparer.Ordinal);
        for (int index = 0; index < targets.Length; index++)
        {
            string prefix = $"{CdcExplicitProjectionTargetProof.TargetsSectionName}:{index}";
            settings[$"{prefix}:TenantKey"] = targets[index].TenantKey;
            settings[$"{prefix}:DataStoreId"] = targets[index]
                .DataStoreId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return settings;
    }

    /// <summary>The binding target: the default tenant, which the projector configures as the empty key.</summary>
    private static CdcValidatedTarget Target() =>
        new(
            "dms",
            CdcTargetValidator.DefaultBindingTenantKey,
            "1",
            "binding",
            CdcProvider.Postgresql,
            "edfi.documents",
            Generation: 7,
            PartitionCount: 1,
            CdcTargetValidator.KafkaMurmur2V1PartitionerAlgorithm
        );

    private static CdcDiagnostic Diagnostic(CdcExplicitProjectionTargetProofResult result) =>
        result.Diagnostics.Should().ContainSingle().Subject;

    private sealed class UnreadableConfiguration : IConfiguration
    {
        public string? this[string key]
        {
            get => throw new InvalidOperationException("The configuration source cannot be read.");
            set => throw new InvalidOperationException("The configuration source cannot be read.");
        }

        public IEnumerable<IConfigurationSection> GetChildren() =>
            throw new InvalidOperationException("The configuration source cannot be read.");

        public IChangeToken GetReloadToken() =>
            throw new InvalidOperationException("The configuration source cannot be read.");

        public IConfigurationSection GetSection(string key) =>
            throw new InvalidOperationException("The configuration source cannot be read.");
    }
}
