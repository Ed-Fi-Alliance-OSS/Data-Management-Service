// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json.Nodes;
using EdFi.DataManagementService.Core.DocumentCache.Cdc;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.DocumentCache.Cdc;

[TestFixture]
[Parallelizable]
[Category("CdcBindingExactMatch")]
public class Given_CdcBindingExactMatch
{
    private static CdcBinding SampleBinding =>
        new(
            1,
            "dms-local",
            "default",
            "1",
            "data-store-1",
            1,
            CdcProvider.Postgresql,
            "sha256:8caa6b0ad6db6f60d8d7ce6e78d1e76094e2241678c6f241670319ab60810851",
            "dms-local-data-store-1-g1",
            "edfi.dms.instance.data-store-1-g1.documents.v1",
            1,
            "kafka-murmur2-v1",
            CdcJsonContract.CurrentContractVersion
        );

    [Test]
    public void It_accepts_a_persisted_binding_only_when_every_v1_field_matches()
    {
        string json = CdcJsonContract.Serialize(SampleBinding);

        CdcBindingExactMatchResult result = CdcBindingExactMatch.Compare(SampleBinding, json);

        result.Succeeded.Should().BeTrue();
        result.PersistedBinding.Should().Be(SampleBinding);
        result.Differences.Should().BeEmpty();
        result.Diagnostics.Should().BeEmpty();
    }

    [TestCase("version", 2)]
    [TestCase("deploymentKey", "dms-other")]
    [TestCase("tenantKey", "tenant-a")]
    [TestCase("dataStoreId", "2")]
    [TestCase("instanceKey", "data-store-2")]
    [TestCase("generation", 2L)]
    [TestCase("provider", "sqlServer")]
    [TestCase(
        "physicalSourceFingerprint",
        "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
    )]
    [TestCase("connectorName", "dms-local-data-store-2-g1")]
    [TestCase("topicName", "edfi.dms.instance.data-store-2-g1.documents.v1")]
    [TestCase("partitionCount", 2)]
    [TestCase("partitionerAlgorithm", "other-partitioner")]
    [TestCase("contractVersion", 2)]
    public void It_reports_differently_valued_immutable_fields(string fieldName, object changedValue)
    {
        string json = SetField(fieldName, changedValue);

        CdcBindingExactMatchResult result = CdcBindingExactMatch.Compare(SampleBinding, json);

        result.Succeeded.Should().BeFalse();
        result
            .Differences.Should()
            .ContainSingle(difference =>
                difference.Kind == CdcBindingFieldDifferenceKind.DifferentValue
                && difference.FieldName == fieldName
            );
    }

    [Test]
    public void It_reports_missing_and_extra_persisted_fields()
    {
        string missingFieldJson = RemoveField("topicName");
        string extraFieldJson = SetField("connectionString", "Server=localhost;Password=secret;");

        CdcBindingExactMatchResult missingFieldResult = CdcBindingExactMatch.Compare(
            SampleBinding,
            missingFieldJson
        );
        CdcBindingExactMatchResult extraFieldResult = CdcBindingExactMatch.Compare(
            SampleBinding,
            extraFieldJson
        );

        missingFieldResult.Succeeded.Should().BeFalse();
        missingFieldResult
            .Differences.Should()
            .ContainSingle(difference =>
                difference.Kind == CdcBindingFieldDifferenceKind.MissingField
                && difference.FieldName == "topicName"
            );
        extraFieldResult.Succeeded.Should().BeFalse();
        extraFieldResult
            .Differences.Should()
            .ContainSingle(difference =>
                difference.Kind == CdcBindingFieldDifferenceKind.ExtraField
                && difference.FieldName == "connectionString"
            );
    }

    [Test]
    public void It_reports_duplicate_persisted_fields_before_deserialization_can_hide_them()
    {
        string json = CdcJsonContract.Serialize(SampleBinding);
        string duplicateTopicNameJson = json.Replace(
            "\"topicName\":\"edfi.dms.instance.data-store-1-g1.documents.v1\"",
            "\"topicName\":\"unexpected.topic\",\"topicName\":\"edfi.dms.instance.data-store-1-g1.documents.v1\"",
            StringComparison.Ordinal
        );

        CdcBindingExactMatchResult result = CdcBindingExactMatch.Compare(
            SampleBinding,
            duplicateTopicNameJson
        );

        result.Succeeded.Should().BeFalse();
        result
            .Differences.Should()
            .ContainSingle(difference =>
                difference.Kind == CdcBindingFieldDifferenceKind.DuplicateField
                && difference.FieldName == "topicName"
            );
    }

    private static string SetField(string fieldName, object value)
    {
        JsonObject root = JsonNode.Parse(CdcJsonContract.Serialize(SampleBinding))!.AsObject();
        root[fieldName] = JsonValue.Create(value);

        return root.ToJsonString(CdcJsonContract.SerializerOptions);
    }

    private static string RemoveField(string fieldName)
    {
        JsonObject root = JsonNode.Parse(CdcJsonContract.Serialize(SampleBinding))!.AsObject();
        root.Remove(fieldName);

        return root.ToJsonString(CdcJsonContract.SerializerOptions);
    }
}
