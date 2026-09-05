// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EdFi.DataManagementService.Core.DocumentCache.Cdc;

public sealed record CdcArtifactNameInput(
    string? DeploymentKey,
    string? TopicPrefix,
    string? InstanceKey,
    long Generation,
    CdcProvider Provider
);

public sealed record CdcGovernedArtifactName(CdcGovernedArtifactKind Kind, string Name);

public sealed record CdcArtifactInventory(
    string DeploymentKey,
    string TopicPrefix,
    string InstanceKey,
    long Generation,
    CdcProvider Provider,
    string ConnectorName,
    string TopicName,
    string ProgressTopicName,
    string? SchemaHistoryTopicName,
    string? PostgresqlPublicationName,
    string? PostgresqlLogicalSlotName,
    string? SqlServerCdcGatingRoleName,
    string? SqlServerCaptureInstanceDocumentName,
    string? SqlServerCaptureInstanceDocumentCacheName,
    string? SqlServerCaptureInstanceCdcHeartbeatName,
    IReadOnlyList<CdcGovernedArtifactName> GovernedArtifacts
);

public sealed record CdcArtifactNameResult
{
    private CdcArtifactNameResult(CdcArtifactInventory? inventory, IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        Inventory = inventory;
        Diagnostics = diagnostics;
    }

    public CdcArtifactInventory? Inventory { get; }

    public IReadOnlyList<CdcDiagnostic> Diagnostics { get; }

    public bool Succeeded => Inventory is not null && Diagnostics.Count == 0;

    public static CdcArtifactNameResult Success(CdcArtifactInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        return new(inventory, []);
    }

    public static CdcArtifactNameResult Failure(IReadOnlyList<CdcDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        return new(null, diagnostics);
    }
}

public static class CdcArtifactNameGenerator
{
    public const int MaximumKafkaOrConnectNameLength = 249;
    public const int MaximumPostgresqlIdentifierLength = 63;
    public const int MaximumSqlServerCaptureInstanceLength = 100;
    public const int MaximumSqlServerGatingRoleLength = 128;

    private const string PostgresqlPublicationArtifactKind = "postgresql-publication";
    private const string PostgresqlLogicalSlotArtifactKind = "postgresql-logical-slot";
    private const string SqlServerCdcGatingRoleArtifactKind = "sqlserver-cdc-gating-role";
    private const string SqlServerCaptureInstanceDocumentArtifactKind = "sqlserver-capture-instance-document";
    private const string SqlServerCaptureInstanceDocumentCacheArtifactKind =
        "sqlserver-capture-instance-documentcache";
    private const string SqlServerCaptureInstanceCdcHeartbeatArtifactKind =
        "sqlserver-capture-instance-cdcheartbeat";

    private const int ProviderArtifactHashLength = 12;
    private const int ProviderArtifactDiscriminatorLength = 12;

    public static CdcArtifactNameResult Render(CdcArtifactNameInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        CdcDiagnosticCollector diagnostics = new();

        string? deploymentKey = CdcKafkaSafeTokenValidator.Validate(
            input.DeploymentKey,
            "$.deploymentKey",
            "deploymentKey",
            diagnostics
        );
        string? topicPrefix = CdcKafkaSafeTokenValidator.Validate(
            input.TopicPrefix,
            "$.topicPrefix",
            "topicPrefix",
            diagnostics
        );
        string? instanceKey = CdcKafkaSafeTokenValidator.Validate(
            input.InstanceKey,
            "$.instanceKey",
            "instanceKey",
            diagnostics
        );

        ValidateGeneration(input.Generation, diagnostics);
        ValidateProvider(input.Provider, diagnostics);

        if (diagnostics.HasDiagnostics || deploymentKey is null || topicPrefix is null || instanceKey is null)
        {
            return CdcArtifactNameResult.Failure(diagnostics.Diagnostics);
        }

        string generation = input.Generation.ToString(CultureInfo.InvariantCulture);
        string connectorName = $"{deploymentKey}-{instanceKey}-g{generation}";
        string topicName = $"{topicPrefix}.instance.{instanceKey}-g{generation}.documents.v1";
        string progressTopicName = $"{topicName}.cdc-progress";
        string? schemaHistoryTopicName =
            input.Provider == CdcProvider.SqlServer ? $"{topicName}.schema-history" : null;

        ValidateKafkaOrConnectNameLength(connectorName, "$.deploymentKey", "connectorName", diagnostics);
        ValidateKafkaOrConnectNameLength(topicName, "$.topicPrefix", "topicName", diagnostics);
        ValidateKafkaOrConnectNameLength(
            progressTopicName,
            "$.topicPrefix",
            "progressTopicName",
            diagnostics
        );
        if (schemaHistoryTopicName is not null)
        {
            ValidateKafkaOrConnectNameLength(
                schemaHistoryTopicName,
                "$.topicPrefix",
                "schemaHistoryTopicName",
                diagnostics
            );
        }

        if (diagnostics.HasDiagnostics)
        {
            return CdcArtifactNameResult.Failure(diagnostics.Diagnostics);
        }

        return CdcArtifactNameResult.Success(
            input.Provider == CdcProvider.Postgresql
                ? CreatePostgresqlInventory(
                    deploymentKey,
                    topicPrefix,
                    instanceKey,
                    input.Generation,
                    connectorName,
                    topicName,
                    progressTopicName
                )
                : CreateSqlServerInventory(
                    deploymentKey,
                    topicPrefix,
                    instanceKey,
                    input.Generation,
                    connectorName,
                    topicName,
                    progressTopicName,
                    schemaHistoryTopicName!
                )
        );
    }

    /// <summary>
    /// Whether <paramref name="topicName"/> is the public topic of some generation of the target
    /// identified by <paramref name="topicPrefix"/> and <paramref name="instanceKey"/>.
    /// </summary>
    /// <remarks>
    /// Generations of one target are deliberately long-lived together: a guarded source replacement
    /// retains the generation it supersedes until an operator retires it, and a stable consumer reads
    /// both. Deciding that a topic belongs to this target rather than to another instance is therefore
    /// something the ACL isolation rule needs, and the answer is a property of the naming rule above,
    /// which is why it is answered here rather than by a pattern written at the call site.
    ///
    /// Only the public topic matches. The progress and schema-history topics extend the public name
    /// with their own suffixes, and neither is a topic any consumer principal may hold a grant on.
    /// </remarks>
    public static bool IsTargetPublicTopicName(string topicPrefix, string instanceKey, string topicName)
    {
        ArgumentNullException.ThrowIfNull(topicPrefix);
        ArgumentNullException.ThrowIfNull(instanceKey);
        ArgumentNullException.ThrowIfNull(topicName);

        string prefix = $"{topicPrefix}.instance.{instanceKey}-g";
        const string Suffix = ".documents.v1";

        if (
            !topicName.StartsWith(prefix, StringComparison.Ordinal)
            || !topicName.EndsWith(Suffix, StringComparison.Ordinal)
            || topicName.Length <= prefix.Length + Suffix.Length
        )
        {
            return false;
        }

        string generation = topicName[prefix.Length..^Suffix.Length];

        return long.TryParse(
                generation,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out long parsedGeneration
            )
            && parsedGeneration >= 1;
    }

    public static CdcArtifactNameResult RecoverFromBinding(CdcBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        return RecoverFromCompleteBindingIdentity(binding.ToCompleteBindingIdentity());
    }

    public static CdcArtifactNameResult RecoverFromCompleteBindingIdentity(
        CdcCompleteBindingIdentity bindingIdentity
    )
    {
        ArgumentNullException.ThrowIfNull(bindingIdentity);

        CdcDiagnosticCollector diagnostics = new();

        string? topicPrefix = RecoverTopicPrefix(
            bindingIdentity.TopicName,
            bindingIdentity.InstanceKey,
            bindingIdentity.Generation,
            diagnostics
        );

        if (topicPrefix is null || diagnostics.HasDiagnostics)
        {
            return CdcArtifactNameResult.Failure(diagnostics.Diagnostics);
        }

        CdcArtifactNameResult renderResult = Render(
            new(
                bindingIdentity.DeploymentKey,
                topicPrefix,
                bindingIdentity.InstanceKey,
                bindingIdentity.Generation,
                bindingIdentity.Provider
            )
        );
        foreach (CdcDiagnostic diagnostic in renderResult.Diagnostics)
        {
            diagnostics.Add(diagnostic);
        }

        if (renderResult.Inventory is not null)
        {
            if (
                !string.Equals(
                    bindingIdentity.ConnectorName,
                    renderResult.Inventory.ConnectorName,
                    StringComparison.Ordinal
                )
            )
            {
                diagnostics.MalformedPayload(
                    "$.connectorName",
                    "CDC connectorName does not match the deterministic artifact inventory."
                );
            }

            if (
                !string.Equals(
                    bindingIdentity.TopicName,
                    renderResult.Inventory.TopicName,
                    StringComparison.Ordinal
                )
            )
            {
                diagnostics.MalformedPayload(
                    "$.topicName",
                    "CDC topicName does not match the deterministic artifact inventory."
                );
            }
        }

        return diagnostics.HasDiagnostics || renderResult.Inventory is null
            ? CdcArtifactNameResult.Failure(diagnostics.Diagnostics)
            : CdcArtifactNameResult.Success(renderResult.Inventory);
    }

    private static string? RecoverTopicPrefix(
        string? topicName,
        string? instanceKey,
        long generation,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (topicName is null || topicName.Length == 0)
        {
            diagnostics.MissingRequiredField("$.topicName", "topicName");
            return null;
        }

        if (instanceKey is null || instanceKey.Length == 0)
        {
            diagnostics.MissingRequiredField("$.instanceKey", "instanceKey");
            return null;
        }

        ValidateGeneration(generation, diagnostics);
        if (generation <= 0)
        {
            return null;
        }

        string suffix =
            $".instance.{instanceKey}-g{generation.ToString(CultureInfo.InvariantCulture)}.documents.v1";
        if (!topicName.EndsWith(suffix, StringComparison.Ordinal) || topicName.Length == suffix.Length)
        {
            diagnostics.MalformedPayload(
                "$.topicName",
                "CDC topicName must end with the deterministic binding topic suffix."
            );
            return null;
        }

        string topicPrefix = topicName[..^suffix.Length];
        return CdcKafkaSafeTokenValidator.Validate(topicPrefix, "$.topicPrefix", "topicPrefix", diagnostics);
    }

    private static CdcArtifactInventory CreatePostgresqlInventory(
        string deploymentKey,
        string topicPrefix,
        string instanceKey,
        long generation,
        string connectorName,
        string topicName,
        string progressTopicName
    )
    {
        string providerPrefix = CreateProviderArtifactPrefix(deploymentKey, instanceKey, generation);
        string publicationName = TruncateProviderArtifactName(
            $"{providerPrefix}_pub",
            PostgresqlPublicationArtifactKind,
            MaximumPostgresqlIdentifierLength
        );
        string logicalSlotName = TruncateProviderArtifactName(
            $"{providerPrefix}_slot",
            PostgresqlLogicalSlotArtifactKind,
            MaximumPostgresqlIdentifierLength
        );

        return new(
            deploymentKey,
            topicPrefix,
            instanceKey,
            generation,
            CdcProvider.Postgresql,
            connectorName,
            topicName,
            progressTopicName,
            null,
            publicationName,
            logicalSlotName,
            null,
            null,
            null,
            null,
            [
                new(CdcGovernedArtifactKind.KafkaConnectConnector, connectorName),
                new(CdcGovernedArtifactKind.ConnectSourceOffsets, connectorName),
                new(CdcGovernedArtifactKind.PublicTopic, topicName),
                new(CdcGovernedArtifactKind.ProgressTopic, progressTopicName),
                new(CdcGovernedArtifactKind.PublicTopicAcls, topicName),
                new(CdcGovernedArtifactKind.ProgressTopicAcls, progressTopicName),
                new(CdcGovernedArtifactKind.PostgresqlPublication, publicationName),
                new(CdcGovernedArtifactKind.PostgresqlLogicalSlot, logicalSlotName),
            ]
        );
    }

    private static CdcArtifactInventory CreateSqlServerInventory(
        string deploymentKey,
        string topicPrefix,
        string instanceKey,
        long generation,
        string connectorName,
        string topicName,
        string progressTopicName,
        string schemaHistoryTopicName
    )
    {
        string providerPrefix = CreateProviderArtifactPrefix(deploymentKey, instanceKey, generation);
        string cdcGatingRoleName = TruncateProviderArtifactName(
            $"{providerPrefix}_cdc_reader",
            SqlServerCdcGatingRoleArtifactKind,
            MaximumSqlServerGatingRoleLength
        );
        string documentCaptureInstanceName = TruncateProviderArtifactName(
            $"{providerPrefix}_document",
            SqlServerCaptureInstanceDocumentArtifactKind,
            MaximumSqlServerCaptureInstanceLength
        );
        string documentCacheCaptureInstanceName = TruncateProviderArtifactName(
            $"{providerPrefix}_documentcache",
            SqlServerCaptureInstanceDocumentCacheArtifactKind,
            MaximumSqlServerCaptureInstanceLength
        );
        string cdcHeartbeatCaptureInstanceName = TruncateProviderArtifactName(
            $"{providerPrefix}_cdcheartbeat",
            SqlServerCaptureInstanceCdcHeartbeatArtifactKind,
            MaximumSqlServerCaptureInstanceLength
        );

        return new(
            deploymentKey,
            topicPrefix,
            instanceKey,
            generation,
            CdcProvider.SqlServer,
            connectorName,
            topicName,
            progressTopicName,
            schemaHistoryTopicName,
            null,
            null,
            cdcGatingRoleName,
            documentCaptureInstanceName,
            documentCacheCaptureInstanceName,
            cdcHeartbeatCaptureInstanceName,
            [
                new(CdcGovernedArtifactKind.KafkaConnectConnector, connectorName),
                new(CdcGovernedArtifactKind.ConnectSourceOffsets, connectorName),
                new(CdcGovernedArtifactKind.PublicTopic, topicName),
                new(CdcGovernedArtifactKind.ProgressTopic, progressTopicName),
                new(CdcGovernedArtifactKind.PublicTopicAcls, topicName),
                new(CdcGovernedArtifactKind.ProgressTopicAcls, progressTopicName),
                new(CdcGovernedArtifactKind.SqlServerCdcGatingRole, cdcGatingRoleName),
                new(CdcGovernedArtifactKind.SqlServerCaptureInstanceDocument, documentCaptureInstanceName),
                new(
                    CdcGovernedArtifactKind.SqlServerCaptureInstanceDocumentCache,
                    documentCacheCaptureInstanceName
                ),
                new(
                    CdcGovernedArtifactKind.SqlServerCaptureInstanceCdcHeartbeat,
                    cdcHeartbeatCaptureInstanceName
                ),
                new(CdcGovernedArtifactKind.SchemaHistoryTopic, schemaHistoryTopicName),
                new(CdcGovernedArtifactKind.SchemaHistoryTopicAcls, schemaHistoryTopicName),
            ]
        );
    }

    private static string CreateProviderArtifactPrefix(
        string deploymentKey,
        string instanceKey,
        long generation
    )
    {
        string providerDeploymentKey = ToProviderSafeToken(deploymentKey);
        string providerInstanceKey = ToProviderSafeToken(instanceKey);
        string providerDiscriminator = ComputeProviderArtifactDiscriminator(
            deploymentKey,
            instanceKey,
            generation
        );

        return $"edfi_dms_{providerDeploymentKey}_{providerInstanceKey}_g{generation.ToString(CultureInfo.InvariantCulture)}_{providerDiscriminator}";
    }

    private static string ToProviderSafeToken(string value) => value.Replace('.', '_').Replace('-', '_');

    private static string ComputeProviderArtifactDiscriminator(
        string deploymentKey,
        string instanceKey,
        long generation
    )
    {
        byte[] payload = Encoding.UTF8.GetBytes(
            $"{deploymentKey}\0{instanceKey}\0{generation.ToString(CultureInfo.InvariantCulture)}"
        );
        byte[] hash = SHA256.HashData(payload);

        return Convert.ToHexString(hash).ToLowerInvariant()[..ProviderArtifactDiscriminatorLength];
    }

    private static string TruncateProviderArtifactName(string name, string artifactKind, int maximumLength)
    {
        if (name.Length <= maximumLength)
        {
            return name;
        }

        string suffix = $"_{ComputeProviderArtifactHash(artifactKind, name)}";
        return $"{name[..(maximumLength - suffix.Length)]}{suffix}";
    }

    private static string ComputeProviderArtifactHash(string artifactKind, string untruncatedName)
    {
        byte[] payload = Encoding.UTF8.GetBytes($"{artifactKind}\0{untruncatedName}");
        byte[] hash = SHA256.HashData(payload);

        return Convert.ToHexString(hash).ToLowerInvariant()[..ProviderArtifactHashLength];
    }

    private static void ValidateGeneration(long generation, CdcDiagnosticCollector diagnostics)
    {
        if (generation <= 0)
        {
            diagnostics.MalformedPayload("$.generation", "CDC generation must be positive.");
        }
    }

    private static void ValidateProvider(CdcProvider provider, CdcDiagnosticCollector diagnostics)
    {
        if (!Enum.IsDefined(provider))
        {
            diagnostics.InvalidEnumValue("$.provider", "CDC provider must be `postgresql` or `sqlServer`.");
        }
    }

    private static void ValidateKafkaOrConnectNameLength(
        string renderedName,
        string path,
        string artifactName,
        CdcDiagnosticCollector diagnostics
    )
    {
        if (renderedName.Length > MaximumKafkaOrConnectNameLength)
        {
            diagnostics.MalformedPayload(
                path,
                $"CDC {artifactName} must not exceed {MaximumKafkaOrConnectNameLength} characters."
            );
        }
    }
}
