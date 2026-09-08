// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using Confluent.Kafka;
using Confluent.Kafka.Admin;
using CoreCdc = EdFi.DataManagementService.Core.DocumentCache.Cdc;

namespace EdFi.DataManagementService.Backend.Cdc;

public sealed partial class CdcKafkaAdminAdapter
{
    /// <summary>
    /// Deletes only recovered binding topics or exact policy-approved literal topic grants.
    /// Never deletes principals, group grants, shared worker storage, wildcard or prefixed ACLs.
    /// Unexpected grants on the retiring topic reject; inherited grants require their own owner.
    /// </summary>
    public Task<CdcTransportResult<CoreCdc.CdcGovernedArtifact>> DeleteAsync(
        CdcArtifactCleanupScope scope,
        CoreCdc.CdcGovernedArtifactKind kind,
        CancellationToken cancellationToken
    ) =>
        CdcArtifactCleanup.GuardAsync(
            scope,
            CdcDeploymentComponent.Kafka,
            async token =>
            {
                bool topic =
                    kind
                    is CoreCdc.CdcGovernedArtifactKind.PublicTopic
                        or CoreCdc.CdcGovernedArtifactKind.ProgressTopic
                        or CoreCdc.CdcGovernedArtifactKind.SchemaHistoryTopic;
                bool acls =
                    kind
                    is CoreCdc.CdcGovernedArtifactKind.PublicTopicAcls
                        or CoreCdc.CdcGovernedArtifactKind.ProgressTopicAcls
                        or CoreCdc.CdcGovernedArtifactKind.SchemaHistoryTopicAcls;
                if ((!topic && !acls) || !scope.Inventory.Any(item => item.Kind == kind))
                {
                    return CdcArtifactCleanup.Failure(
                        CdcDeploymentComponent.Kafka,
                        CdcDeploymentFailure.InvalidInput
                    );
                }

                var artifact = scope.Artifact(kind);
                var request = scope.Request;
                if (topic)
                {
                    var before = await InspectTopicAsync(request, artifact.Name, token);
                    if (before is CdcTransportResult<CdcKafkaTopicEvidence>.Absent)
                    {
                        return CdcArtifactCleanup.Removed(artifact, false);
                    }

                    if (before is not CdcTransportResult<CdcKafkaTopicEvidence>.Observed)
                    {
                        return CdcArtifactCleanup.Failure(
                            CdcDeploymentComponent.Kafka,
                            before.Diagnostics.FirstOrDefault()?.Failure ?? CdcDeploymentFailure.Unavailable
                        );
                    }

                    await CdcArtifactCleanup.AttemptAsync(
                        _ =>
                            _client.DeleteTopicsAsync(
                                [artifact.Name],
                                new()
                                {
                                    RequestTimeout = request.Timing.CallTimeout,
                                    OperationTimeout = request.Timing.CallTimeout,
                                }
                            ),
                        request,
                        token
                    );
                    while (true)
                    {
                        var after = await InspectTopicAsync(request, artifact.Name, token);
                        if (after is CdcTransportResult<CdcKafkaTopicEvidence>.Absent)
                        {
                            return CdcArtifactCleanup.Removed(artifact, true);
                        }

                        if (after is not CdcTransportResult<CdcKafkaTopicEvidence>.Observed)
                        {
                            return CdcArtifactCleanup.Failure(
                                CdcDeploymentComponent.Kafka,
                                after.Diagnostics.FirstOrDefault()?.Failure
                                    ?? CdcDeploymentFailure.Unavailable
                            );
                        }

                        await Task.Delay(request.Timing.PollInterval, token);
                    }
                }
                var allowed = CdcDeploymentKafkaPolicy
                    .Build(request)
                    .BindingGrants.Where(grant =>
                        grant.ResourceType == CdcKafkaAclResourceType.Topic
                        && grant.ResourceName == artifact.Name
                    )
                    .ToHashSet();
                var inspection = await InspectAclsAsync(request, token);
                if (inspection is not CdcTransportResult<CdcKafkaAclEvidence>.Observed evidence)
                {
                    return CdcArtifactCleanup.Failure(
                        CdcDeploymentComponent.Kafka,
                        inspection.Diagnostics.FirstOrDefault()?.Failure ?? CdcDeploymentFailure.Unavailable
                    );
                }

                var grants = Applicable(evidence.Value, artifact.Name);
                if (Array.Exists(grants, grant => !allowed.Contains(grant)))
                {
                    return CdcArtifactCleanup.Failure(
                        CdcDeploymentComponent.Kafka,
                        CdcDeploymentFailure.ValidationFailed
                    );
                }

                if (grants.Length == 0)
                {
                    return CdcArtifactCleanup.Removed(artifact, false);
                }

                await CdcArtifactCleanup.AttemptAsync(
                    async _ =>
                    {
                        await _client.DeleteAclsAsync(
                            grants.Select(grant => new AclBindingFilter
                            {
                                PatternFilter = new()
                                {
                                    Type = ResourceType.Topic,
                                    Name = grant.ResourceName,
                                    ResourcePatternType = ResourcePatternType.Literal,
                                },
                                EntryFilter = new()
                                {
                                    Principal = grant.Principal,
                                    Host = grant.Host,
                                    Operation = ParseEnum<AclOperation>(grant.Operation.ToString()),
                                    PermissionType = AclPermissionType.Allow,
                                },
                            }),
                            new() { RequestTimeout = request.Timing.CallTimeout }
                        );
                    },
                    request,
                    token
                );
                var final = await InspectAclsAsync(request, token);
                return
                    final is CdcTransportResult<CdcKafkaAclEvidence>.Observed verified
                    && Applicable(verified.Value, artifact.Name).Length == 0
                    ? CdcArtifactCleanup.Removed(artifact, true)
                    : CdcArtifactCleanup.Failure(
                        CdcDeploymentComponent.Kafka,
                        final.Diagnostics.FirstOrDefault()?.Failure ?? CdcDeploymentFailure.Unavailable
                    );
            },
            cancellationToken
        );

    private static CdcKafkaAclGrant[] Applicable(CdcKafkaAclEvidence evidence, string topic) =>
        evidence
            .Grants.Where(grant =>
                grant.ResourceType == CdcKafkaAclResourceType.Topic
                && (
                    grant.ResourceName == topic
                    || grant.ResourceName == "*"
                    || grant.Pattern == CdcKafkaAclPattern.Prefixed
                        && topic.StartsWith(grant.ResourceName, StringComparison.Ordinal)
                )
            )
            .ToArray();
}
