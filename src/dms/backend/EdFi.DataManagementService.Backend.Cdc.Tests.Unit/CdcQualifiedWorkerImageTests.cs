// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Backend.Ddl;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Cdc.Tests.Unit;

[TestFixture]
public sealed class Given_SqlServer_Native_DocumentCache_Worker_Qualification
{
    private const string QualifiedDigest =
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherDigest =
        "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [TestCase(false, QualifiedDigest, false)]
    [TestCase(true, OtherDigest, false)]
    [TestCase(true, QualifiedDigest, true)]
    public void It_only_admits_the_exact_qualified_native_worker(
        bool supportsNative,
        string requestedDigest,
        bool expected
    )
    {
        var sources = CdcConnectorTemplateTestData
            .BuildRequiredSourceTableInventory(CdcProvider.SqlServer)
            .Select(table =>
                table.TableKind == CdcSourceTableKind.DocumentCache
                    ? new CdcSourceTableInventory(
                        table.TableKind,
                        table.TableName,
                        table.EmittedQuotedTableName,
                        [
                            .. table.Columns,
                            CdcConnectorTemplateTestData.BuildColumn(
                                CdcProvider.SqlServer,
                                "DocumentJson",
                                2,
                                "json"
                            ),
                        ]
                    )
                    : table
            )
            .ToArray();
        var request = CdcDeploymentRequestTestData.Request(
            CdcProvider.SqlServer,
            worker: CdcDeploymentRequestTestData.Worker(digest: requestedDigest),
            sourceInventory: sources
        );

        CdcQualifiedWorkerImage.CanServe(request, supportsNative, QualifiedDigest).Should().Be(expected);
    }

    [TestCase(CdcProvider.Postgresql)]
    [TestCase(CdcProvider.SqlServer)]
    public void It_does_not_apply_the_native_worker_requirement_to_other_source_shapes(CdcProvider provider)
    {
        var request = CdcDeploymentRequestTestData.Request(
            provider,
            worker: CdcDeploymentRequestTestData.Worker(digest: OtherDigest)
        );

        CdcQualifiedWorkerImage.CanServe(request, false, QualifiedDigest).Should().BeTrue();
    }
}
