// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Core.ApiSchema.Model;
using EdFi.DataManagementService.Core.Validation;
using FluentAssertions;
using NUnit.Framework;

namespace EdFi.DataManagementService.Core.Tests.Unit.Validation;

/// <summary>
/// Resource-property filter validation, shared by the collection GET pipeline and by any other
/// operation that filters the same candidate set. The rules asserted here were previously private to
/// ValidateQueryMiddleware; they are asserted directly so a second consumer cannot acquire filter
/// behavior of its own.
/// </summary>
[TestFixture]
public class ResourceQueryFilterValidatorTests
{
    private static QueryField Field(string queryFieldName, string type, params string[] jsonPaths)
    {
        string[] paths = jsonPaths.Length == 0 ? [$"$.{queryFieldName}"] : jsonPaths;

        return new QueryField(queryFieldName, [.. paths.Select(path => new JsonPathAndType(path, type))]);
    }

    private static QueryField[] Fields() =>
        [
            Field("schoolId", "number"),
            Field("nameOfInstitution", "string"),
            Field("isActive", "boolean"),
            Field("beginDate", "date"),
            Field("createDate", "date-time"),
            Field("classPeriodStart", "time"),
        ];

    [TestFixture]
    [Parallelizable]
    public class Given_Only_Recognized_Filters : ResourceQueryFilterValidatorTests
    {
        private ResourceQueryFilterResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = ResourceQueryFilterValidator.Validate(
                new Dictionary<string, string> { ["schoolId"] = "255901", ["isActive"] = "TRUE" },
                Fields(),
                ordinalExcludedNames: [],
                ignoreCaseExcludedNames: []
            );
        }

        [Test]
        public void It_accepts_the_request()
        {
            _result.Should().BeOfType<ResourceQueryFilterResult.Valid>();
        }

        [Test]
        public void It_returns_one_query_element_per_supplied_filter_in_request_order()
        {
            ((ResourceQueryFilterResult.Valid)_result)
                .QueryElements.Select(element => element.QueryFieldName)
                .Should()
                .Equal("schoolId", "isActive");
        }

        [Test]
        public void It_carries_the_document_paths_and_type_from_the_matched_query_field()
        {
            var element = ((ResourceQueryFilterResult.Valid)_result).QueryElements[0];

            element.Type.Should().Be("number");
            element.DocumentPaths.Select(path => path.Value).Should().Equal("$.schoolId");
        }

        [Test]
        public void It_canonicalizes_a_boolean_value()
        {
            ((ResourceQueryFilterResult.Valid)_result).QueryElements[1].Value.Should().Be("true");
        }
    }

    /// <summary>
    /// Boolean filters reach the candidate query as canonical protocol text derived from the parsed
    /// value, not as a folded copy of what the client typed. The two differ on every value
    /// <c>bool.TryParse</c> accepts but does not spell canonically: it ignores surrounding whitespace,
    /// so folding the supplied text leaves padding on the value the filter is compared against, and
    /// that comparison then matches nothing while the request is answered as a success.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Boolean_Filter_Spelled_Uncanonically : ResourceQueryFilterValidatorTests
    {
        private static string ValueOf(string supplied) =>
            (
                (ResourceQueryFilterResult.Valid)
                    ResourceQueryFilterValidator.Validate(
                        new Dictionary<string, string> { ["isActive"] = supplied },
                        Fields(),
                        ordinalExcludedNames: [],
                        ignoreCaseExcludedNames: []
                    )
            )
                .QueryElements[0]
                .Value;

        [TestCase("true")]
        [TestCase("TRUE")]
        [TestCase("tRuE")]
        [TestCase(" true ")]
        [TestCase("  TrUe\t")]
        public void It_canonicalizes_an_accepted_true_to_exactly_true(string supplied)
        {
            ValueOf(supplied).Should().Be("true");
        }

        [TestCase("false")]
        [TestCase("FALSE")]
        [TestCase("fAlSe")]
        [TestCase(" false ")]
        [TestCase("\tFaLsE  ")]
        public void It_canonicalizes_an_accepted_false_to_exactly_false(string supplied)
        {
            ValueOf(supplied).Should().Be("false");
        }
    }

    /// <summary>
    /// A value that does not parse is still reported exactly as the client supplied it, so the
    /// canonicalization above cannot reach the message a rejected request answers with.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Boolean_Filter_That_Does_Not_Parse : ResourceQueryFilterValidatorTests
    {
        private ResourceQueryFilterResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = ResourceQueryFilterValidator.Validate(
                new Dictionary<string, string> { ["isActive"] = " TrUthY " },
                Fields(),
                ordinalExcludedNames: [],
                ignoreCaseExcludedNames: []
            );
        }

        [Test]
        public void It_reports_the_value_the_client_supplied()
        {
            ((ResourceQueryFilterResult.InvalidValues)_result)
                .ValidationErrors["$.isActive"]
                .Should()
                .Equal("The value ' TrUthY ' is not valid for isActive.");
        }

        [Test]
        public void It_emits_no_query_element_that_could_execute()
        {
            _result.Should().BeOfType<ResourceQueryFilterResult.InvalidValues>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Query_Field_Name_In_A_Different_Case : ResourceQueryFilterValidatorTests
    {
        private ResourceQueryFilterResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = ResourceQueryFilterValidator.Validate(
                new Dictionary<string, string> { ["SCHOOLID"] = "255901" },
                Fields(),
                ordinalExcludedNames: [],
                ignoreCaseExcludedNames: []
            );
        }

        [Test]
        public void It_matches_the_query_field_case_insensitively()
        {
            _result.Should().BeOfType<ResourceQueryFilterResult.Valid>();
        }

        [Test]
        public void It_keeps_the_client_spelling_on_the_query_element()
        {
            ((ResourceQueryFilterResult.Valid)_result)
                .QueryElements[0]
                .QueryFieldName.Should()
                .Be("SCHOOLID");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Date_Time_Filter : ResourceQueryFilterValidatorTests
    {
        [Test]
        public void It_normalizes_the_value_to_utc_seconds_precision()
        {
            var result = ResourceQueryFilterValidator.Validate(
                new Dictionary<string, string> { ["createDate"] = "2024-03-04T05:06:07.888Z" },
                Fields(),
                ordinalExcludedNames: [],
                ignoreCaseExcludedNames: []
            );

            ((ResourceQueryFilterResult.Valid)result)
                .QueryElements[0]
                .Value.Should()
                .Be("2024-03-04T05:06:07Z");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Date_Filter_Carrying_A_Time : ResourceQueryFilterValidatorTests
    {
        [Test]
        public void It_passes_only_the_date_portion_downstream()
        {
            var result = ResourceQueryFilterValidator.Validate(
                new Dictionary<string, string> { ["beginDate"] = "2024-03-04T05:06:07" },
                Fields(),
                ordinalExcludedNames: [],
                ignoreCaseExcludedNames: []
            );

            ((ResourceQueryFilterResult.Valid)result).QueryElements[0].Value.Should().Be("2024-03-04");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Unrecognized_Query_Field : ResourceQueryFilterValidatorTests
    {
        [Test]
        public void It_skips_the_name_and_keeps_the_recognized_filters()
        {
            var result = ResourceQueryFilterValidator.Validate(
                new Dictionary<string, string>
                {
                    ["notAField"] = "x",
                    ["schoolId"] = "1",
                    ["alsoNotAField"] = "y",
                },
                Fields(),
                ordinalExcludedNames: [],
                ignoreCaseExcludedNames: []
            );

            ((ResourceQueryFilterResult.Valid)result)
                .QueryElements.Select(element => (element.QueryFieldName, element.Value))
                .Should()
                .Equal(("schoolId", "1"));
        }

        [Test]
        public void It_still_reports_a_faulty_value_that_follows_it()
        {
            var result = ResourceQueryFilterValidator.Validate(
                new Dictionary<string, string> { ["notAField"] = "x", ["schoolId"] = "abc" },
                Fields(),
                ordinalExcludedNames: [],
                ignoreCaseExcludedNames: []
            );

            ((ResourceQueryFilterResult.InvalidValues)result)
                .ValidationErrors["$.schoolId"]
                .Should()
                .Equal("The value 'abc' is not valid for schoolId.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Name_To_Match_Against_The_Query_Fields : ResourceQueryFilterValidatorTests
    {
        [Test]
        public void It_matches_a_query_field_case_insensitively()
        {
            ResourceQueryFilterValidator.MatchesQueryField("SCHOOLID", Fields()).Should().BeTrue();
        }

        [Test]
        public void It_does_not_match_a_name_that_is_not_a_query_field()
        {
            ResourceQueryFilterValidator.MatchesQueryField("studentUniqueld", Fields()).Should().BeFalse();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Filters_Whose_Values_Do_Not_Match_Their_Types : ResourceQueryFilterValidatorTests
    {
        private ResourceQueryFilterResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = ResourceQueryFilterValidator.Validate(
                new Dictionary<string, string>
                {
                    ["schoolId"] = "abc",
                    ["isActive"] = "maybe",
                    ["classPeriodStart"] = "noon",
                },
                Fields(),
                ordinalExcludedNames: [],
                ignoreCaseExcludedNames: []
            );
        }

        [Test]
        public void It_reports_every_faulty_value_rather_than_the_first()
        {
            ((ResourceQueryFilterResult.InvalidValues)_result)
                .ValidationErrors.Keys.Should()
                .BeEquivalentTo("$.schoolId", "$.isActive", "$.classPeriodStart");
        }

        [Test]
        public void It_keys_each_error_by_json_path_and_names_the_client_field()
        {
            ((ResourceQueryFilterResult.InvalidValues)_result)
                .ValidationErrors["$.schoolId"]
                .Should()
                .Equal("The value 'abc' is not valid for schoolId.");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_One_Json_Path_Carrying_Two_Faulty_Filters : ResourceQueryFilterValidatorTests
    {
        [Test]
        public void It_accumulates_both_messages_under_that_path()
        {
            QueryField[] fields =
            [
                Field("firstAlias", "number", "$.shared"),
                Field("secondAlias", "number", "$.shared"),
            ];

            var result = ResourceQueryFilterValidator.Validate(
                new Dictionary<string, string> { ["firstAlias"] = "a", ["secondAlias"] = "b" },
                fields,
                ordinalExcludedNames: [],
                ignoreCaseExcludedNames: []
            );

            ((ResourceQueryFilterResult.InvalidValues)result)
                .ValidationErrors["$.shared"]
                .Should()
                .Equal(
                    "The value 'a' is not valid for firstAlias.",
                    "The value 'b' is not valid for secondAlias."
                );
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_Excluded_Parameter_Names : ResourceQueryFilterValidatorTests
    {
        [Test]
        public void It_ignores_an_ordinal_excluded_name_rather_than_reporting_it_as_a_field()
        {
            var result = ResourceQueryFilterValidator.Validate(
                new Dictionary<string, string> { ["limit"] = "10", ["schoolId"] = "1" },
                Fields(),
                ordinalExcludedNames: ["limit"],
                ignoreCaseExcludedNames: []
            );

            ((ResourceQueryFilterResult.Valid)result)
                .QueryElements.Select(element => element.QueryFieldName)
                .Should()
                .Equal("schoolId");
        }

        [Test]
        public void It_does_not_exclude_a_case_variant_of_an_ordinal_excluded_name()
        {
            var result = ResourceQueryFilterValidator.Validate(
                new Dictionary<string, string> { ["SCHOOLID"] = "1" },
                Fields(),
                ordinalExcludedNames: ["schoolId"],
                ignoreCaseExcludedNames: []
            );

            ((ResourceQueryFilterResult.Valid)result)
                .QueryElements.Select(element => element.QueryFieldName)
                .Should()
                .Equal("SCHOOLID");
        }

        [Test]
        public void It_excludes_a_case_variant_of_an_ignore_case_excluded_name()
        {
            var result = ResourceQueryFilterValidator.Validate(
                new Dictionary<string, string> { ["MINCHANGEVERSION"] = "5", ["schoolId"] = "1" },
                Fields(),
                ordinalExcludedNames: [],
                ignoreCaseExcludedNames: ["minChangeVersion"]
            );

            ((ResourceQueryFilterResult.Valid)result)
                .QueryElements.Select(element => element.QueryFieldName)
                .Should()
                .Equal("schoolId");
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_No_Query_Parameters : ResourceQueryFilterValidatorTests
    {
        [Test]
        public void It_accepts_the_request_with_no_query_elements()
        {
            var result = ResourceQueryFilterValidator.Validate(
                new Dictionary<string, string>(),
                Fields(),
                ordinalExcludedNames: [],
                ignoreCaseExcludedNames: []
            );

            ((ResourceQueryFilterResult.Valid)result).QueryElements.Should().BeEmpty();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_Query_Field_With_An_Unsupported_Type : ResourceQueryFilterValidatorTests
    {
        [Test]
        public void It_throws_rather_than_silently_accepting_the_value()
        {
            QueryField[] fields = [Field("oddball", "duration")];

            Action validate = () =>
                ResourceQueryFilterValidator.Validate(
                    new Dictionary<string, string> { ["oddball"] = "P1D" },
                    fields,
                    ordinalExcludedNames: [],
                    ignoreCaseExcludedNames: []
                );

            validate.Should().Throw<InvalidOperationException>().WithMessage("*duration*");
        }
    }

    private static QueryField BoundedField(string queryFieldName, int? maxLength) =>
        new(queryFieldName, [new JsonPathAndType($"$.{queryFieldName}", "string", maxLength)]);

    private static ResourceQueryFilterResult ValidateOne(QueryField field, string value) =>
        ResourceQueryFilterValidator.Validate(
            new Dictionary<string, string> { [field.QueryFieldName] = value },
            [field],
            ordinalExcludedNames: [],
            ignoreCaseExcludedNames: []
        );

    /// <summary>
    /// A string filter longer than its field's declared maxLength is rejected as an invalid filter value,
    /// the same way a value of the wrong type is.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_String_Filter_With_A_Declared_MaxLength : ResourceQueryFilterValidatorTests
    {
        private static readonly QueryField _field = BoundedField("studentUniqueId", 5);

        [TestCase("")]
        [TestCase("abcd")]
        [TestCase("abcde")]
        public void It_accepts_a_value_at_or_under_the_bound(string value)
        {
            ValidateOne(_field, value).Should().BeOfType<ResourceQueryFilterResult.Valid>();
        }

        [Test]
        public void It_passes_an_accepted_value_through_unchanged()
        {
            ((ResourceQueryFilterResult.Valid)ValidateOne(_field, "abcde"))
                .QueryElements[0]
                .Value.Should()
                .Be("abcde");
        }

        [TestCase("abcdef")]
        [TestCase("abcde ")]
        public void It_rejects_a_value_one_over_the_bound_including_trailing_space_overflow(string value)
        {
            ((ResourceQueryFilterResult.InvalidValues)ValidateOne(_field, value))
                .ValidationErrors["$.studentUniqueId"]
                .Should()
                .Equal($"The value '{value}' is not valid for studentUniqueId.");
        }
    }

    /// <summary>
    /// Filter length is counted in text elements, the unit JsonSchema.Net's maxLength uses for request
    /// bodies. Each case is checked against JsonSchema.Net itself so the two units cannot drift apart.
    /// </summary>
    [TestFixture]
    [Parallelizable]
    public class Given_A_Bounded_String_Filter_With_Unicode_Content : ResourceQueryFilterValidatorTests
    {
        private const int Bound = 3;
        private const string Supplementary = "\U0001F600";
        private const string Combining = "é";
        private static readonly string _heavyCombining = "e" + new string('́', 50);

        private static IEnumerable<TestCaseData> Cases()
        {
            yield return new TestCaseData("ascii-at-bound", "abc");
            yield return new TestCaseData("ascii-over-bound", "abcd");
            yield return new TestCaseData(
                "supplementary-at-bound",
                Supplementary + Supplementary + Supplementary
            );
            yield return new TestCaseData(
                "supplementary-over-bound",
                Supplementary + Supplementary + Supplementary + Supplementary
            );
            yield return new TestCaseData("combining-at-bound", Combining + Combining + Combining);
            yield return new TestCaseData(
                "combining-over-bound",
                Combining + Combining + Combining + Combining
            );
            yield return new TestCaseData(
                "heavy-combining-at-bound",
                _heavyCombining + _heavyCombining + _heavyCombining
            );
        }

        private static bool JsonSchemaAccepts(string value) =>
            Json
                .Schema.JsonSchema.FromText($$"""{"type":"string","maxLength":{{Bound}}}""")
                .Evaluate(System.Text.Json.Nodes.JsonValue.Create(value))
                .IsValid;

        [TestCaseSource(nameof(Cases))]
        public void It_agrees_with_json_schema_max_length(string caseName, string value)
        {
            bool filterAccepted =
                ValidateOne(BoundedField("name", Bound), value) is ResourceQueryFilterResult.Valid;

            filterAccepted.Should().Be(JsonSchemaAccepts(value), caseName);
        }

        [Test]
        public void It_accepts_three_text_elements_spanning_far_more_than_three_utf16_units()
        {
            string value = _heavyCombining + _heavyCombining + _heavyCombining;

            value.Length.Should().Be(153);
            ValidateOne(BoundedField("name", Bound), value)
                .Should()
                .BeOfType<ResourceQueryFilterResult.Valid>();
        }

        [Test]
        public void It_rejects_four_supplementary_characters()
        {
            ValidateOne(
                    BoundedField("name", Bound),
                    Supplementary + Supplementary + Supplementary + Supplementary
                )
                .Should()
                .BeOfType<ResourceQueryFilterResult.InvalidValues>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_A_String_Filter_Without_A_Declared_MaxLength : ResourceQueryFilterValidatorTests
    {
        [Test]
        public void It_invents_no_bound()
        {
            ValidateOne(BoundedField("duration", null), new string('x', 10_000))
                .Should()
                .BeOfType<ResourceQueryFilterResult.Valid>();
        }
    }

    [TestFixture]
    [Parallelizable]
    public class Given_An_Over_Long_String_Filter_Alongside_A_Mistyped_Filter
        : ResourceQueryFilterValidatorTests
    {
        private ResourceQueryFilterResult _result = null!;

        [SetUp]
        public void Setup()
        {
            _result = ResourceQueryFilterValidator.Validate(
                new Dictionary<string, string> { ["studentUniqueId"] = "abcdef", ["schoolId"] = "abc" },
                [BoundedField("studentUniqueId", 5), Field("schoolId", "number")],
                ordinalExcludedNames: [],
                ignoreCaseExcludedNames: []
            );
        }

        [Test]
        public void It_reports_both_faults()
        {
            ((ResourceQueryFilterResult.InvalidValues)_result)
                .ValidationErrors.Keys.Should()
                .BeEquivalentTo("$.studentUniqueId", "$.schoolId");
        }
    }
}
