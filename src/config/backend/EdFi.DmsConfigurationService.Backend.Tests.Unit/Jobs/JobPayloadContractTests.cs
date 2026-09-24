// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EdFi.DmsConfigurationService.Backend.Jobs;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit.Jobs;

public enum ProbeMode
{
    First = 1,
    Second = 2,
}

public sealed record ProbeItem([property: JobIdentifier(32)] string Code, int Rank);

public sealed record ProbePayload(
    [property: JobIdentifier(64)] string DataStoreId,
    int Count,
    long Total,
    short Small,
    Guid Correlation,
    bool Force,
    ProbeMode Mode,
    [property: JobIdentifier(16)] IReadOnlyList<string> Tags,
    IReadOnlyList<int> Numbers,
    ProbeItem Item,
    IReadOnlyList<ProbeItem> Items
);

public sealed record ObjectPayload(object Value);

public sealed record JsonElementPayload(JsonElement Value);

public sealed record JsonNodePayload(JsonNode Value);

public sealed record JsonObjectPayload(JsonObject Value);

public sealed record DictionaryPayload(Dictionary<string, int> Values);

public sealed record ReadOnlyDictionaryPayload(IReadOnlyDictionary<string, int> Values);

public sealed record DateTimePayload(DateTime When);

public sealed record DateTimeOffsetPayload(DateTimeOffset When);

public sealed record DateOnlyPayload(DateOnly When);

public sealed record DoublePayload(double Value);

public sealed record FloatPayload(float Value);

public sealed record DecimalPayload(decimal Value);

public sealed record UnannotatedStringPayload(string Name);

public sealed record UnannotatedStringListPayload(IReadOnlyList<string> Names);

public sealed record NullableIntPayload(int? Count);

public sealed record MutableListPayload(List<int> Values);

public sealed record ArrayPayload(int[] Values);

public sealed record EnumerablePayload(IEnumerable<int> Values);

public sealed record ListOfListsPayload(IReadOnlyList<IReadOnlyList<int>> Values);

public sealed record CharPayload(char Value);

public sealed record IdentifierOnIntPayload([property: JobIdentifier(10)] int Count);

public sealed record IdentifierTooLongPayload([property: JobIdentifier(257)] string Id);

public sealed record IdentifierZeroLengthPayload([property: JobIdentifier(0)] string Id);

public sealed record InvalidNested(double Value);

public sealed record NestedInvalidPayload(InvalidNested Nested);

public sealed record RecursiveNode(int Value, IReadOnlyList<RecursiveNode> Children);

public sealed record FrameworkTypePayload(Version Version);

public record UnsealedPayload(int Count);

public readonly record struct StructPayload(int Count);

public sealed record GenericPayload<T>(T Value);

public sealed class PublicFieldPayload
{
#pragma warning disable S1104 // The public field is the contract violation under test.
    public int Count;
#pragma warning restore S1104
}

public sealed class ExtensionDataPayload
{
    public int Count { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

[JsonPolymorphic]
public sealed record PolymorphicPayload(int Count);

public sealed record ConverterPayload(
    [property: JsonConverter(typeof(JsonStringEnumConverter))] ProbeMode Mode
);

public sealed record Level8(int Value);

public sealed record Level7(Level8 Next);

public sealed record Level6(Level7 Next);

public sealed record Level5(Level6 Next);

public sealed record Level4(Level5 Next);

public sealed record Level3(Level4 Next);

public sealed record Level2(Level3 Next);

public sealed record EightLevelPayload(Level2 Next);

public sealed record NineLevelPayload(EightLevelPayload Next);

public sealed record ListOverTheDepthLimitPayload(Level3 Next, IReadOnlyList<Level2> Deep);

#pragma warning disable S1144, S4487, CS0414, IDE0051, IDE0052 // The unused private members are the violations under test.
public sealed record NonPublicIncludedPropertyPayload(int Count)
{
    [JsonInclude]
    private string Secret { get; set; } = "hidden";
}

public sealed class NonPublicIncludedFieldPayload
{
    public int Count { get; set; }

    [JsonInclude]
    private int _hidden = 1;
}
#pragma warning restore S1144, S4487, CS0414, IDE0051, IDE0052

public abstract record IncludedBase
{
    [JsonInclude]
    internal int Hidden { get; set; }
}

public sealed record DerivedFromIncludedBasePayload(int Count) : IncludedBase;

[JsonConverter(typeof(JsonStringEnumConverter<ConvertedMode>))]
public enum ConvertedMode
{
    First = 1,
}

public sealed record EnumConverterPayload(ConvertedMode Mode);

public enum RenamedMode
{
    [JsonStringEnumMemberName("one")]
    First = 1,
}

public sealed record EnumMemberNamePayload(RenamedMode Mode);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Skip)]
public sealed record SkipsUnexpectedMembersPayload(int Count);

public sealed record NestedSkipsUnexpectedMembersPayload(SkipsUnexpectedMembersPayload Inner);

[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public sealed record TypeNumberHandlingPayload(int Count);

public sealed record PropertyNumberHandlingPayload(
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] int Count
);

public sealed record RenamedPropertyPayload([property: JsonPropertyName("$type")] int Count);

public sealed record IgnoredPropertyPayload(int Count)
{
    [JsonIgnore]
    public int Extra { get; init; }
}

public sealed record ComputedPropertyPayload(int Count)
{
    public int Doubled => Count * 2;
}

public class JobPayloadContractTests
{
    private static ProbePayload ValidPayload() =>
        new(
            DataStoreId: "ds-42:primary",
            Count: 3,
            Total: 9_000_000_000,
            Small: 7,
            Correlation: Guid.Parse("3f2a9c1e-7b5d-4e6f-8a0b-1c2d3e4f5a6b"),
            Force: true,
            Mode: ProbeMode.Second,
            Tags: ["alpha", "beta.1"],
            Numbers: [1, 2, 3],
            Item: new ProbeItem("item-1", 1),
            Items: [new ProbeItem("item-2", 2), new ProbeItem("item-3", 3)]
        );

    [TestFixture]
    public class Given_payload_types_that_break_the_structural_contract
    {
        private static readonly (Type Type, string Expected)[] _cases =
        [
            (typeof(ObjectPayload), "an untyped value"),
            (typeof(JsonElementPayload), "a JSON document value"),
            (typeof(JsonNodePayload), "a JSON document value"),
            (typeof(JsonObjectPayload), "a JSON document value"),
            (typeof(DictionaryPayload), "a dictionary"),
            (typeof(ReadOnlyDictionaryPayload), "a dictionary"),
            (typeof(DateTimePayload), "a date or time value"),
            (typeof(DateTimeOffsetPayload), "a date or time value"),
            (typeof(DateOnlyPayload), "a date or time value"),
            (typeof(DoublePayload), "a floating-point value"),
            (typeof(FloatPayload), "a floating-point value"),
            (typeof(DecimalPayload), "a floating-point value"),
            (typeof(UnannotatedStringPayload), "a string must be annotated with [JobIdentifier]"),
            (typeof(UnannotatedStringListPayload), "a string must be annotated with [JobIdentifier]"),
            (typeof(NullableIntPayload), "a nullable value"),
            (typeof(MutableListPayload), "a collection other than IReadOnlyList<>"),
            (typeof(ArrayPayload), "a collection other than IReadOnlyList<>"),
            (typeof(EnumerablePayload), "a collection other than IReadOnlyList<>"),
            (typeof(ListOfListsPayload), "a list of lists is not allowed"),
            (typeof(CharPayload), "is not an allowed payload type"),
            (typeof(IdentifierOnIntPayload), "[JobIdentifier] applies only to a string"),
            (typeof(IdentifierTooLongPayload), "[JobIdentifier] maximum length must be between 1 and 256"),
            (typeof(IdentifierZeroLengthPayload), "[JobIdentifier] maximum length must be between 1 and 256"),
            (typeof(NestedInvalidPayload), "NestedInvalidPayload.Nested.Value: 'Double' is not allowed"),
            (typeof(RecursiveNode), "may not be recursive"),
            (typeof(FrameworkTypePayload), "is not an allowed payload type"),
            (typeof(UnsealedPayload), "must be a sealed, non-generic record or class"),
            (typeof(StructPayload), "must be a sealed, non-generic record or class"),
            (typeof(GenericPayload<int>), "must be a sealed, non-generic record or class"),
            (typeof(PublicFieldPayload), "public fields are not allowed"),
            (typeof(ExtensionDataPayload), "extension data is not allowed"),
            (typeof(PolymorphicPayload), "declares polymorphism"),
            (typeof(ConverterPayload), "a custom JSON converter is not allowed"),
            (typeof(NineLevelPayload), "nests deeper than 8 JSON levels"),
            (typeof(ListOverTheDepthLimitPayload), "nests deeper than 8 JSON levels"),
            (
                typeof(NonPublicIncludedPropertyPayload),
                "NonPublicIncludedPropertyPayload.Secret: a non-public member carries [JsonInclude]"
            ),
            (
                typeof(NonPublicIncludedFieldPayload),
                "NonPublicIncludedFieldPayload._hidden: a non-public member carries [JsonInclude]"
            ),
            (typeof(DerivedFromIncludedBasePayload), "must derive directly from object"),
            (
                typeof(DerivedFromIncludedBasePayload),
                "DerivedFromIncludedBasePayload.Hidden: a non-public member carries [JsonInclude]"
            ),
            (typeof(EnumConverterPayload), "enum 'ConvertedMode' carries [JsonConverter]"),
            (
                typeof(EnumMemberNamePayload),
                "enum member 'RenamedMode.First' carries [JsonStringEnumMemberName]"
            ),
            (typeof(SkipsUnexpectedMembersPayload), "carries [JsonUnmappedMemberHandling]"),
            (
                typeof(NestedSkipsUnexpectedMembersPayload),
                "NestedSkipsUnexpectedMembersPayload.Inner: 'SkipsUnexpectedMembersPayload' carries [JsonUnmappedMemberHandling]"
            ),
            (typeof(TypeNumberHandlingPayload), "'TypeNumberHandlingPayload' carries [JsonNumberHandling]"),
            (
                typeof(PropertyNumberHandlingPayload),
                "PropertyNumberHandlingPayload.Count: carries [JsonNumberHandling]"
            ),
            (typeof(RenamedPropertyPayload), "RenamedPropertyPayload.Count: carries [JsonPropertyName]"),
            (typeof(IgnoredPropertyPayload), "IgnoredPropertyPayload.Extra: carries [JsonIgnore]"),
            (
                typeof(ComputedPropertyPayload),
                "ComputedPropertyPayload.Doubled: is read-only, so the serializer would write it but never read it back"
            ),
        ];

        private readonly Dictionary<Type, IReadOnlyList<string>> _violations = [];
        private InvalidOperationException? _registrationFailure;

        [SetUp]
        public void Setup()
        {
            foreach ((Type type, string _) in _cases)
            {
                _violations[type] = JobPayloadContract.Violations(type);
            }

            try
            {
                JobPayloadContract.EnsureValid(typeof(UnannotatedStringPayload));
            }
            catch (InvalidOperationException exception)
            {
                _registrationFailure = exception;
            }
        }

        [Test]
        public void It_rejects_each_disallowed_member_kind_with_a_specific_violation()
        {
            foreach ((Type type, string expected) in _cases)
            {
                _violations[type].Should().Contain(violation => violation.Contains(expected), type.Name);
            }
        }

        [Test]
        public void It_fails_registration_naming_the_type_and_the_property()
        {
            _registrationFailure.Should().NotBeNull();
            _registrationFailure!
                .Message.Should()
                .Contain(typeof(UnannotatedStringPayload).FullName!)
                .And.Contain("UnannotatedStringPayload.Name");
        }
    }

    [TestFixture]
    public class Given_framework_types_as_payload_roots
    {
        private readonly Dictionary<Type, IReadOnlyList<string>> _violations = [];

        [SetUp]
        public void Setup()
        {
            foreach (
                Type type in new[]
                {
                    typeof(Version),
                    typeof(JsonDocument),
                    typeof(StringBuilder),
                    typeof(string),
                }
            )
            {
                _violations[type] = JobPayloadContract.Violations(type);
            }
        }

        [Test]
        public void It_rejects_a_framework_type_even_when_its_members_would_be_allowed()
        {
            foreach ((Type type, IReadOnlyList<string> violations) in _violations)
            {
                violations
                    .Should()
                    .ContainSingle(type.Name)
                    .Which.Should()
                    .Be($"{type.Name}: '{type.Name}' is not an allowed payload type (a framework type)");
            }
        }
    }

    [TestFixture]
    public class Given_payload_types_that_meet_the_contract
    {
        private IReadOnlyList<string> _probeViolations = [];
        private IReadOnlyList<string> _eightLevelViolations = [];

        [SetUp]
        public void Setup()
        {
            _probeViolations = JobPayloadContract.Violations(typeof(ProbePayload));
            _eightLevelViolations = JobPayloadContract.Violations(typeof(EightLevelPayload));
        }

        [Test]
        public void It_accepts_every_allowed_member_kind() => _probeViolations.Should().BeEmpty();

        [Test]
        public void It_accepts_nesting_at_the_depth_limit() => _eightLevelViolations.Should().BeEmpty();
    }

    [TestFixture]
    public class Given_a_valid_payload
    {
        private JobPayloadWriteResult _written = null!;
        private JobPayloadReadResult<ProbePayload> _read = null!;

        [SetUp]
        public void Setup()
        {
            _written = JobPayloadSerializer.Serialize(ValidPayload());
            _read = JobPayloadSerializer.Deserialize<ProbePayload>(
                ((JobPayloadWriteResult.Success)_written).Json
            );
        }

        [Test]
        public void It_serializes_with_camel_case_names_within_the_size_limit()
        {
            string json = _written.Should().BeOfType<JobPayloadWriteResult.Success>().Subject.Json;
            json.Should().StartWith("{\"dataStoreId\":\"ds-42:primary\",\"count\":3,");
            json.Length.Should().BeLessThanOrEqualTo(JobPayloadContract.MaxPayloadLength);
        }

        [Test]
        public void It_round_trips() =>
            _read
                .Should()
                .BeOfType<JobPayloadReadResult<ProbePayload>.Success>()
                .Which.Payload.Should()
                .BeEquivalentTo(ValidPayload());
    }

    [TestFixture]
    public class Given_payload_texts_that_break_the_rules
    {
        private static string ValidJson() =>
            ((JobPayloadWriteResult.Success)JobPayloadSerializer.Serialize(ValidPayload())).Json;

        private static string? ReasonFor(string json) =>
            JobPayloadSerializer.Deserialize<ProbePayload>(json)
                is JobPayloadReadResult<ProbePayload>.Failure failure
                ? failure.ReasonCode
                : null;

        private readonly Dictionary<string, string?> _reasons = [];

        [SetUp]
        public void Setup()
        {
            string valid = ValidJson();
            string body = valid[1..^1];

            _reasons["unexpected member"] = ReasonFor("{" + body + ",\"extra\":1}");
            _reasons["$type"] = ReasonFor("{\"$type\":\"ProbePayload\"," + body + "}");
            _reasons["4001 units"] = ReasonFor(valid.PadRight(JobPayloadContract.MaxPayloadLength + 1));
            _reasons["4000 units"] = ReasonFor(valid.PadRight(JobPayloadContract.MaxPayloadLength));
            _reasons["lone high surrogate"] = ReasonFor(valid.Replace("\"alpha\"", "\"al\uD800pha\""));
            _reasons["lone low surrogate"] = ReasonFor(valid.Replace("\"alpha\"", "\"al\uDC00pha\""));
            _reasons["escaped lone surrogate"] = ReasonFor(valid.Replace("\"alpha\"", "\"al\\ud800pha\""));
            _reasons["array root"] = ReasonFor("[" + valid + "]");
            _reasons["number root"] = ReasonFor("1");
            _reasons["string root"] = ReasonFor("\"x\"");
            _reasons["null root"] = ReasonFor("null");
            _reasons["empty text"] = ReasonFor("");
            _reasons["non-JSON whitespace prefix"] = ReasonFor(" " + valid);
            _reasons["JSON whitespace prefix"] = ReasonFor(" \t\r\n" + valid);
            _reasons["trailing comma"] = ReasonFor("{" + body + ",}");
            _reasons["comment"] = ReasonFor("{/* note */" + body + "}");
            _reasons["duplicate member"] = ReasonFor("{" + body + ",\"count\":4}");
            _reasons["number in a string"] = ReasonFor(valid.Replace("\"count\":3", "\"count\":\"3\""));
            _reasons["name in another case"] = ReasonFor(valid.Replace("\"dataStoreId\"", "\"DataStoreId\""));
            _reasons["missing member"] = ReasonFor(valid.Replace(",\"force\":true", ""));
            _reasons["null identifier"] = ReasonFor(
                valid.Replace("\"dataStoreId\":\"ds-42:primary\"", "\"dataStoreId\":null")
            );
            _reasons["null list element"] = ReasonFor(valid.Replace("[\"alpha\",", "[null,"));
            _reasons["identifier with a space"] = ReasonFor(valid.Replace("ds-42:primary", "ds 42"));
            _reasons["identifier with a leading dash"] = ReasonFor(valid.Replace("ds-42:primary", "-ds42"));
            _reasons["identifier with a trailing newline"] = ReasonFor(
                valid.Replace("ds-42:primary", "ds42\\n")
            );
            _reasons["identifier over its maximum"] = ReasonFor(
                valid.Replace("ds-42:primary", new string('a', 65))
            );
            _reasons["identifier at its maximum"] = ReasonFor(
                valid.Replace("ds-42:primary", new string('a', 64))
            );
            _reasons["undefined enum value"] = ReasonFor(valid.Replace("\"mode\":2", "\"mode\":99"));
        }

        [Test]
        public void It_rejects_unexpected_members_and_type_metadata()
        {
            _reasons["unexpected member"].Should().Be(JobPayloadFailureReasons.InvalidJson);
            _reasons["$type"].Should().Be(JobPayloadFailureReasons.InvalidJson);
        }

        [Test]
        public void It_rejects_more_than_4000_utf16_code_units()
        {
            _reasons["4001 units"].Should().Be(JobPayloadFailureReasons.PayloadTooLarge);
            _reasons["4000 units"].Should().BeNull();
        }

        [Test]
        public void It_rejects_unpaired_surrogates()
        {
            _reasons["lone high surrogate"].Should().Be(JobPayloadFailureReasons.UnpairedSurrogate);
            _reasons["lone low surrogate"].Should().Be(JobPayloadFailureReasons.UnpairedSurrogate);
            _reasons["escaped lone surrogate"].Should().NotBeNull();
        }

        [Test]
        public void It_rejects_a_root_that_is_not_an_object()
        {
            foreach (
                string name in new[]
                {
                    "array root",
                    "number root",
                    "string root",
                    "null root",
                    "empty text",
                    "non-JSON whitespace prefix",
                }
            )
            {
                _reasons[name].Should().Be(JobPayloadFailureReasons.NotAnObject, name);
            }
        }

        [Test]
        public void It_accepts_an_object_after_json_whitespace() =>
            _reasons["JSON whitespace prefix"].Should().BeNull();

        [Test]
        public void It_rejects_lenient_json()
        {
            foreach (
                string name in new[]
                {
                    "trailing comma",
                    "comment",
                    "duplicate member",
                    "number in a string",
                    "name in another case",
                    "missing member",
                    "null identifier",
                }
            )
            {
                _reasons[name].Should().Be(JobPayloadFailureReasons.InvalidJson, name);
            }
        }

        [Test]
        public void It_rejects_a_missing_list_element() =>
            _reasons["null list element"].Should().Be(JobPayloadFailureReasons.MissingValue);

        [Test]
        public void It_enforces_the_identifier_pattern_and_declared_length()
        {
            foreach (
                string name in new[]
                {
                    "identifier with a space",
                    "identifier with a leading dash",
                    "identifier with a trailing newline",
                    "identifier over its maximum",
                }
            )
            {
                _reasons[name].Should().Be(JobPayloadFailureReasons.InvalidIdentifier, name);
            }
            _reasons["identifier at its maximum"].Should().BeNull();
        }

        [Test]
        public void It_rejects_an_undefined_enum_value() =>
            _reasons["undefined enum value"].Should().Be(JobPayloadFailureReasons.UndefinedEnumValue);
    }

    [TestFixture]
    public class Given_payload_values_that_break_the_rules_on_serialization
    {
        private readonly Dictionary<string, JobPayloadWriteResult> _results = [];
        private InvalidOperationException? _invalidType;

        [SetUp]
        public void Setup()
        {
            ProbePayload valid = ValidPayload();
            _results["invalid identifier"] = JobPayloadSerializer.Serialize(
                valid with
                {
                    DataStoreId = "ds 42",
                }
            );
            _results["null identifier"] = JobPayloadSerializer.Serialize(valid with { DataStoreId = null! });
            _results["null nested object"] = JobPayloadSerializer.Serialize(valid with { Item = null! });
            _results["undefined enum value"] = JobPayloadSerializer.Serialize(
                valid with
                {
                    Mode = (ProbeMode)99,
                }
            );
            _results["oversize"] = JobPayloadSerializer.Serialize(
                valid with
                {
                    Numbers = [.. Enumerable.Repeat(1_000_000_000, 400)],
                }
            );

            try
            {
                JobPayloadSerializer.Serialize(new UnannotatedStringPayload("name"));
            }
            catch (InvalidOperationException exception)
            {
                _invalidType = exception;
            }
        }

        [Test]
        public void It_rejects_each_value_with_its_reason()
        {
            ReasonOf(_results["invalid identifier"]).Should().Be(JobPayloadFailureReasons.InvalidIdentifier);
            ReasonOf(_results["null identifier"]).Should().Be(JobPayloadFailureReasons.MissingValue);
            ReasonOf(_results["null nested object"]).Should().Be(JobPayloadFailureReasons.MissingValue);
            ReasonOf(_results["undefined enum value"])
                .Should()
                .Be(JobPayloadFailureReasons.UndefinedEnumValue);
            ReasonOf(_results["oversize"]).Should().Be(JobPayloadFailureReasons.PayloadTooLarge);
        }

        [Test]
        public void It_refuses_a_type_that_breaks_the_contract() => _invalidType.Should().NotBeNull();

        private static string? ReasonOf(JobPayloadWriteResult result) =>
            result is JobPayloadWriteResult.Failure failure ? failure.ReasonCode : null;
    }

    [TestFixture]
    public class Given_job_and_schedule_keys
    {
        private static readonly string[] _valid =
        [
            "DataStore.RefreshEducationOrganizations",
            "a",
            "A-b_c.d9",
            "k" + new string('x', 99),
        ];

        private static readonly string?[] _invalid =
        [
            null,
            "",
            "1abc",
            ".abc",
            "_abc",
            "has space",
            "trailing ",
            " leading",
            "trailing\n",
            "tab\tinside",
            "k" + new string('x', 100),
            "café",
            "a/b",
            "a:b",
        ];

        private readonly Dictionary<string, bool> _validResults = [];
        private readonly List<(string? Key, bool Result)> _invalidResults = [];

        [SetUp]
        public void Setup()
        {
            foreach (string key in _valid)
            {
                _validResults[key] = JobKeySyntax.IsValid(key);
            }

            foreach (string? key in _invalid)
            {
                _invalidResults.Add((key, JobKeySyntax.IsValid(key)));
            }
        }

        [Test]
        public void It_accepts_keys_of_up_to_100_characters_starting_with_a_letter() =>
            _validResults.Values.Should().OnlyContain(result => result);

        [Test]
        public void It_rejects_everything_else_including_trailing_whitespace() =>
            _invalidResults.Should().OnlyContain(result => !result.Result);
    }
}
