// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EdFi.DmsConfigurationService.Backend.DataStoreProvisioning;
using EdFi.DmsConfigurationService.Backend.ManagedDataStores;
using EdFi.DmsConfigurationService.DataModel.Model;
using EdFi.DmsConfigurationService.DataModel.Model.DataStoreManage;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.ManagedDataStores;

[TestFixture]
public class Given_Managed_Data_Store_Contracts
{
    private static JsonElement Corpus()
    {
        using var stream = typeof(Given_Managed_Data_Store_Contracts).Assembly.GetManifestResourceStream(
            "DMS1439.ConformanceFixtures.json"
        )!;
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }

    [Test]
    public void It_validates_and_formats_the_frozen_naming_corpus()
    {
        var validator = new DataStoreManageInsertCommand.Validator();
        foreach (var vector in Corpus().GetProperty("naming").EnumerateArray())
        {
            string name = vector.GetProperty("name").GetString()!;
            string template = vector.GetProperty("template").GetString()!;
            var request = new DataStoreManageInsertCommand { Name = name, DatabaseTemplate = template };
            validator.Validate(request).IsValid.Should().BeTrue();
            request.Name.Should().Be(CatalogName.Trim(name));
            DmsDatabaseTemplate.TryFromWireValue(template, out var selected).Should().BeTrue();
            ManagedDataStoreNameFormatter
                .Format(name, selected!, ProtectedDatabaseNames.Create("cms", []))
                .Value.Should()
                .Be(vector.GetProperty("databaseName").GetString());
        }
        foreach (var value in Corpus().GetProperty("invalidNames").EnumerateArray())
        {
            validator
                .Validate(
                    new DataStoreManageInsertCommand
                    {
                        Name = value.GetString(),
                        DatabaseTemplate = "Minimal",
                    }
                )
                .IsValid.Should()
                .BeFalse();
            Action format = () =>
                ManagedDataStoreNameFormatter.Format(
                    value.GetString()!,
                    DmsDatabaseTemplate.Minimal,
                    ProtectedDatabaseNames.Create("cms", [])
                );
            format.Should().Throw<ArgumentException>();
        }
        Action protectedName = () =>
            ManagedDataStoreNameFormatter.Format(
                "school",
                DmsDatabaseTemplate.Minimal,
                ProtectedDatabaseNames.Create("cms", ["EdFi_Ods_school_Minimal"])
            );
        protectedName.Should().Throw<ArgumentException>();
    }

    [Test]
    public void It_rejects_every_frozen_invalid_request_without_changing_other_dtos()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        foreach (var vector in Corpus().GetProperty("invalidRequests").EnumerateArray())
        {
            string json = vector.GetProperty("body").GetRawText();
            if (vector.GetProperty("case").GetString() == "unknown property")
            {
                Action read = () => JsonSerializer.Deserialize<DataStoreManageInsertCommand>(json, options);
                read.Should().Throw<JsonException>();
            }
            else
            {
                var request = JsonSerializer.Deserialize<DataStoreManageInsertCommand>(json, options)!;
                new DataStoreManageInsertCommand.Validator().Validate(request).IsValid.Should().BeFalse();
            }
        }
        options.UnmappedMemberHandling.Should().Be(JsonUnmappedMemberHandling.Skip);
    }

    [Test]
    public void It_implements_the_fixed_whitespace_and_ascii_only_canonical_policy()
    {
        var corpus = Corpus().GetProperty("canonicalNames");
        foreach (var vector in corpus.GetProperty("vectors").EnumerateArray())
        {
            CatalogName
                .Canonicalize(vector.GetProperty("input").GetString()!)
                .Should()
                .Be(vector.GetProperty("canonical").GetString());
        }
        foreach (var pair in corpus.GetProperty("pairs").EnumerateArray())
        {
            string.Equals(
                    CatalogName.Canonicalize(pair.GetProperty("left").GetString()!),
                    CatalogName.Canonicalize(pair.GetProperty("right").GetString()!),
                    StringComparison.Ordinal
                )
                .Should()
                .Be(pair.GetProperty("equal").GetBoolean());
        }
        foreach (var codePoint in corpus.GetProperty("trimCodePoints").EnumerateArray())
        {
            char space = (char)codePoint.GetInt32();
            CatalogName.Canonicalize($"{space}A{space}").Should().Be("a");
            CatalogName.Canonicalize($"A{space}B").Should().Be($"a{space}b");
        }
        foreach (int length in new[] { 256, 257, 4096 })
        {
            CatalogName.Canonicalize(new string('A', length)).Should().Be(new string('a', length));
        }
    }

    [TestCase("aa", "z")]
    [TestCase("cafe", "café")]
    [TestCase("cafe\u0301", "café")]
    [TestCase("\ue000", "\ud800\udc00")]
    public void It_keeps_ordinary_utf8_order_vectors_independent_of_equality_key_encoding(
        string first,
        string second
    )
    {
        // Contract vectors only: provider SQL ordering will be implemented and tested in step 1.3b1.
        Encoding
            .UTF8.GetBytes(CatalogName.Canonicalize(first))
            .AsSpan()
            .SequenceCompareTo(Encoding.UTF8.GetBytes(CatalogName.Canonicalize(second)))
            .Should()
            .BeNegative();
    }

    [TestCase("SQL", "sql")]
    [TestCase("SQL", "SQL ")]
    [TestCase("SQL", " SQL")]
    public void It_keeps_ordinary_type_equality_exact_and_untrimmed(string left, string right)
    {
        Encoding.UTF8.GetBytes(left).Should().NotEqual(Encoding.UTF8.GetBytes(right));
    }

    [Test]
    public void It_preserves_managed_defaults_int32_boundaries_and_exact_sort_whitelist()
    {
        var query = new DataStoreManageQuery();
        query.Offset.Should().Be(0);
        query.Limit.Should().Be(25);
        query.OrderBy.Should().Be("id");
        query.Direction.Should().Be("Descending");
        var validator = new DataStoreManageQuery.Validator();
        query.Offset = query.Limit = int.MaxValue;
        query.Id = int.MinValue;
        validator.Validate(query).IsValid.Should().BeTrue();
        foreach (var field in Corpus().GetProperty("managedQuery").GetProperty("orderBy").EnumerateArray())
        {
            query.OrderBy = field.GetString()!;
            validator.Validate(query).IsValid.Should().BeTrue();
        }
        foreach (string field in new[] { " ", "Name", "id;DROP", "dataStoreType" })
        {
            query.OrderBy = field;
            validator.Validate(query).IsValid.Should().BeFalse();
        }
        query.OrderBy = "";
        query.OrderBy.Should().Be("id");
        foreach (string direction in new[] { "", "ASC", "descending", " Ascending" })
        {
            query.Direction = direction;
            validator.Validate(query).IsValid.Should().BeFalse();
        }
        query.Direction = "Ascending";
        validator.Validate(query).IsValid.Should().BeTrue();
        query.Offset = -1;
        validator.Validate(query).IsValid.Should().BeFalse();
        query.Offset = 0;
        query.Limit = 0;
        validator.Validate(query).IsValid.Should().BeFalse();
        new PagingQuery().BuildPagingClause().Should().BeEmpty();
        new PagingQuery().IsDescending.Should().BeFalse();
    }

    [Test]
    public void It_serializes_exactly_nine_public_fields_with_explicit_nulls_even_when_nulls_are_globally_ignored()
    {
        foreach (var model in Corpus().GetProperty("models").EnumerateArray())
        {
            var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            };
            var response = JsonSerializer.Deserialize<DataStoreManageResponse>(model, options)!;
            var serialized = JsonSerializer.SerializeToElement(response, options);
            serialized
                .EnumerateObject()
                .Select(p => p.Name)
                .Should()
                .Equal(model.EnumerateObject().Select(p => p.Name));
            foreach (string field in new[] { "dataStoreId", "dataStoreName", "databaseName" })
            {
                serialized.GetProperty(field).GetRawText().Should().Be(model.GetProperty(field).GetRawText());
            }
        }
    }
}
