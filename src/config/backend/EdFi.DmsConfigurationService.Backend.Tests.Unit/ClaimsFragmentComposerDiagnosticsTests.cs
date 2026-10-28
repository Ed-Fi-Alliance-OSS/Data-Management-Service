// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text.Json;
using System.Text.Json.Nodes;
using EdFi.DmsConfigurationService.Backend.Claims;
using EdFi.DmsConfigurationService.Backend.Claims.Models;
using EdFi.DmsConfigurationService.Claims.Composition;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using CmsComposer = EdFi.DmsConfigurationService.Backend.Claims.ClaimsFragmentComposer;
using SharedComposer = EdFi.DmsConfigurationService.Claims.Composition.ClaimsFragmentComposer;

namespace EdFi.DmsConfigurationService.Backend.Tests.Unit;

public abstract class ClaimsFragmentComposerDiagnosticsTests
{
    protected string DirectoryPath = null!;
    protected ClaimsDocument BaseDocument = null!;
    internal TestCompositionFileSystem FileSystem = null!;

    [SetUp]
    public void SetupDirectory()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), $"claims-diagnostics-{Guid.NewGuid():N}");
        Directory.CreateDirectory(DirectoryPath);
        string source = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "ClaimsComposition",
            "base.json"
        );
        File.Copy(source, Path.Combine(DirectoryPath, "Claims.json"));
        JsonNode document = JsonNode.Parse(File.ReadAllText(source))!;
        BaseDocument = new(document["claimSets"]!, document["claimsHierarchy"]!);
        FileSystem = new();
    }

    [TearDown]
    public void TearDown() => Directory.Delete(DirectoryPath, true);

    protected ClaimsProvider Provider(CmsComposer composer) =>
        new(
            NullLogger<ClaimsProvider>.Instance,
            Options.Create(
                new ClaimsOptions { ClaimsSource = ClaimsSource.Filesystem, ClaimsDirectory = DirectoryPath }
            ),
            new ClaimsValidator(NullLogger<ClaimsValidator>.Instance),
            composer
        );

    protected static void AssertDocument(ClaimsDocument actual, ClaimsDocument expected)
    {
        JsonNode.DeepEquals(actual.ClaimSetsNode, expected.ClaimSetsNode).Should().BeTrue();
        JsonNode.DeepEquals(actual.ClaimsHierarchyNode, expected.ClaimsHierarchyNode).Should().BeTrue();
    }

    internal sealed class TestCompositionFileSystem : IClaimsCompositionFileSystem
    {
        public Func<string, bool> Exists = Directory.Exists;
        public Func<string, string, string[]> Discover = (path, pattern) =>
            Directory.GetFiles(path, pattern, SearchOption.AllDirectories);
        public Func<string, string> Read = File.ReadAllText;

        public bool DirectoryExists(string path) => Exists(path);

        public string[] GetFiles(string path, string pattern) => Discover(path, pattern);

        public string ReadAllText(string path) => Read(path);
    }
}

[TestFixture("exists", "access")]
[TestFixture("current", "access")]
[TestFixture("current", "io")]
[TestFixture("current", "missing")]
[TestFixture("retired", "access")]
[TestFixture("retired", "io")]
[TestFixture("retired", "missing")]
public class Given_ClaimsFragmentComposer_discovery_failure(string stage, string error)
    : ClaimsFragmentComposerDiagnosticsTests
{
    private ClaimsCompositionResult _result = null!;
    private ClaimsLoadResult _cmsResult = null!;
    private ClaimsDocument _providerResult = null!;

    [SetUp]
    public void Setup()
    {
        Exception exception = error switch
        {
            "access" => new UnauthorizedAccessException("denied"),
            "missing" => new DirectoryNotFoundException("disappeared"),
            _ => new IOException("unreadable"),
        };
        if (stage == "exists")
        {
            FileSystem.Exists = _ => throw exception;
        }
        else
        {
            string failingPattern = stage == "current" ? "*-claims.json" : "*-claimset.json";
            FileSystem.Discover = (_, pattern) =>
                pattern == failingPattern
                    ? throw exception
                    : [Path.Combine(DirectoryPath, "available-claims.json")];
        }
        SharedComposer shared = new(NullLogger.Instance, FileSystem);
        _result = shared.ComposeClaimsFromFragments(BaseDocument, DirectoryPath);
        CmsComposer cms = new(shared);
        _cmsResult = cms.ComposeClaimsFromFragments(BaseDocument, DirectoryPath);
        _providerResult = Provider(cms).GetClaimsDocumentNodes();
    }

    [Test]
    public void It_reports_incomplete_discovery_with_original_directory_context()
    {
        _result.IsComplete.Should().BeFalse();
        _result
            .Diagnostics.Should()
            .Equal(
                new ClaimsCompositionDiagnostic(
                    ClaimsCompositionDiagnosticCode.DirectoryDiscoveryFailed,
                    DirectoryPath
                )
            );
    }

    [Test]
    public void It_preserves_successful_base_only_CMS_loading()
    {
        _result.LoadResult.Failures.Should().BeEmpty();
        _cmsResult.Failures.Should().BeEmpty();
        AssertDocument(_cmsResult.Nodes!, BaseDocument);
        AssertDocument(_providerResult, BaseDocument);
    }
}

[TestFixture("missing", "FileNotFound")]
[TestFixture("directory", "DirectoryNotFound")]
[TestFixture("io", "IOError")]
[TestFixture("access", "UnauthorizedAccess")]
[TestFixture("malformed", "JsonError")]
public class Given_ClaimsFragmentComposer_read_failure(string error, string failureType)
    : ClaimsFragmentComposerDiagnosticsTests
{
    private ClaimsCompositionResult _result = null!;
    private CmsComposer _cms = null!;
    private string _badFile = null!;

    [SetUp]
    public void Setup()
    {
        string goodFile = Path.Combine(DirectoryPath, "001-good-claims.json");
        _badFile = Path.Combine(DirectoryPath, "002-bad-claims.json");
        FileSystem.Discover = (_, pattern) => pattern == "*-claims.json" ? [_badFile, goodFile] : [];
        FileSystem.Read = path =>
            path == goodFile
                ? """{"name":"AppliedFirst","resourceClaims":[{"name":"edfi/student","authorizationStrategyOverridesForCRUD":[{"actionName":"Create"}]}]}"""
                : error switch
                {
                    "missing" => throw new FileNotFoundException("disappeared", _badFile),
                    "directory" => throw new DirectoryNotFoundException("disappeared"),
                    "access" => throw new UnauthorizedAccessException("denied"),
                    "io" => throw new IOException("unreadable"),
                    _ => """{"resourceClaims": false}""",
                };
        SharedComposer shared = new(NullLogger.Instance, FileSystem);
        _result = shared.ComposeClaimsFromFragments(BaseDocument, DirectoryPath);
        _cms = new(shared);
    }

    [Test]
    public void It_identifies_the_unreadable_or_malformed_fragment()
    {
        _result.IsComplete.Should().BeFalse();
        _result.LoadResult.Nodes.Should().BeNull();
        _result.LoadResult.Failures.Select(f => f.FailureType).Should().Equal(failureType);
        _result
            .Diagnostics.Should()
            .Equal(
                new ClaimsCompositionDiagnostic(
                    error == "malformed"
                        ? ClaimsCompositionDiagnosticCode.MalformedFragment
                        : ClaimsCompositionDiagnosticCode.FragmentReadFailed,
                    _badFile,
                    JsonPath: error == "malformed" ? "$.resourceClaims" : ""
                )
            );
    }

    [Test]
    public void It_preserves_the_original_CMS_failure_and_fallback()
    {
        if (error == "access")
        {
            Action compose = () => _cms.ComposeClaimsFromFragments(BaseDocument, DirectoryPath);
            Action load = () => Provider(_cms).GetClaimsDocumentNodes();
            compose.Should().Throw<UnauthorizedAccessException>().WithMessage("denied");
            load.Should().Throw<UnauthorizedAccessException>();
            return;
        }
        ClaimsLoadResult result = _cms.ComposeClaimsFromFragments(BaseDocument, DirectoryPath);
        result.Nodes.Should().BeNull();
        result.Failures.Select(f => f.FailureType).Should().Equal(failureType);
        AssertDocument(Provider(_cms).GetClaimsDocumentNodes(), BaseDocument);
    }
}

[TestFixture("empty")]
[TestFixture("missing")]
[TestFixture("retired")]
[TestFixture("null")]
[TestFixture("no-resource-claims")]
[TestFixture("unmatched")]
[TestFixture("invalid-name")]
[TestFixture("invalid-final-newline")]
[TestFixture("registered")]
[TestFixture("filename-name")]
public class Given_ClaimsFragmentComposer_diagnostic_context(string scenario)
    : ClaimsFragmentComposerDiagnosticsTests
{
    private ClaimsCompositionResult _result = null!;
    private ClaimsCompositionDiagnostic[] _expected = null!;

    [SetUp]
    public void Setup()
    {
        string file = Path.Combine(DirectoryPath, "input-claims.json");
        string source = DirectoryPath;
        string name = scenario switch
        {
            "invalid-name" => "Bad Name",
            "invalid-final-newline" => "Policy\n",
            _ => "Policy",
        };
        string claim = scenario == "unmatched" ? "edfi/missing" : "edfi/student";
        switch (scenario)
        {
            case "empty":
                File.WriteAllText(Path.Combine(DirectoryPath, "unrelated.json"), "invalid JSON ignored");
                _expected = [new(ClaimsCompositionDiagnosticCode.EmptyDirectory, source)];
                break;
            case "missing":
                source = Path.Combine(DirectoryPath, "absent");
                _expected = [new(ClaimsCompositionDiagnosticCode.MissingDirectory, source)];
                break;
            case "retired":
                string nested = Path.Combine(DirectoryPath, "nested");
                Directory.CreateDirectory(nested);
                string retired = Path.Combine(nested, "old-claimset.json");
                File.WriteAllText(retired, "invalid JSON ignored");
                _expected = [new(ClaimsCompositionDiagnosticCode.RetiredFragment, retired)];
                break;
            case "null":
            case "no-resource-claims":
                File.WriteAllText(
                    file,
                    scenario == "null" ? "null" : """{"name":"SkippedPolicy","resourceClaims":null}"""
                );
                _expected =
                [
                    new(
                        ClaimsCompositionDiagnosticCode.NoResourceClaims,
                        file,
                        ClaimSetName: scenario == "null" ? "" : "SkippedPolicy",
                        JsonPath: "$.resourceClaims"
                    ),
                ];
                break;
            default:
                JsonObject input = new()
                {
                    ["resourceClaims"] = new JsonArray(new JsonObject { ["name"] = claim }),
                };
                if (scenario != "filename-name")
                {
                    input["name"] = name;
                }
                File.WriteAllText(file, input.ToJsonString());
                string namePath = scenario == "filename-name" ? "" : "$.name";
                if (scenario == "filename-name")
                {
                    name = "input-claims";
                }
                List<ClaimsCompositionDiagnostic> expected = [];
                if (scenario == "unmatched")
                {
                    expected.Add(
                        new(
                            ClaimsCompositionDiagnosticCode.UnmatchedGrantTarget,
                            file,
                            claim,
                            name,
                            "$.resourceClaims[0].name"
                        )
                    );
                }
                expected.Add(
                    new(
                        scenario is "invalid-name" or "invalid-final-newline"
                            ? ClaimsCompositionDiagnosticCode.InvalidClaimSetRegistration
                            : ClaimsCompositionDiagnosticCode.ClaimSetRegistered,
                        file,
                        ClaimSetName: name,
                        JsonPath: namePath
                    )
                );
                _expected = [.. expected];
                break;
        }
        _result = new SharedComposer(NullLogger.Instance).ComposeClaimsFromFragments(BaseDocument, source);
    }

    [Test]
    public void It_returns_exact_codes_and_available_context()
    {
        _result.Diagnostics.Should().Equal(_expected);
        _result.IsComplete.Should().Be(scenario is "empty" or "registered" or "filename-name");
        _result.LoadResult.Failures.Should().BeEmpty();
    }
}

[TestFixture("empty-object", "", false)]
[TestFixture("named-omission", "Skipped\nPolicy", false)]
[TestFixture("complete-document", "", false)]
[TestFixture("null", "SkippedPolicy", false)]
[TestFixture("empty", "SkippedPolicy", true)]
[TestFixture("mixed-case", "SkippedPolicy", true)]
public class Given_ClaimsFragmentComposer_resource_claims_presence(
    string scenario,
    string claimSetName,
    bool complete
) : ClaimsFragmentComposerDiagnosticsTests
{
    private ClaimsCompositionResult _result = null!;
    private ClaimsLoadResult _cmsResult = null!;
    private ClaimsDocument _providerResult = null!;
    private ClaimsDocument _expected = null!;
    private string _file = null!;

    [SetUp]
    public void Setup()
    {
        string fixtures = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "ClaimsComposition"
        );
        string siblings = Path.Combine(fixtures, "first-wins", "fragments");
        foreach (string source in Directory.GetFiles(siblings, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(DirectoryPath, Path.GetRelativePath(siblings, source));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            // Nonempty mixed-case collections must still apply their grants.
            File.WriteAllText(target, File.ReadAllText(source).Replace("resourceClaims", "ReSoUrCeClAiMs"));
        }
        _file = Path.Combine(DirectoryPath, "000-input-claims.json");
        JsonObject input = new();
        if (claimSetName.Length > 0)
        {
            input["name"] = claimSetName;
        }
        if (scenario is "empty" or "mixed-case" or "null")
        {
            input[scenario == "mixed-case" ? "ReSoUrCeClAiMs" : "resourceClaims"] =
                scenario == "null" ? null : new JsonArray();
        }
        File.WriteAllText(
            _file,
            scenario == "complete-document"
                ? File.ReadAllText(Path.Combine(fixtures, "base.json"))
                : input.ToJsonString()
        );
        JsonNode expected = JsonNode.Parse(
            File.ReadAllText(Path.Combine(fixtures, "first-wins", "expected.json"))
        )!;
        _expected = new(expected["claimSets"]!, expected["claimsHierarchy"]!);
        SharedComposer shared = new(NullLogger.Instance);
        _result = shared.ComposeClaimsFromFragments(BaseDocument, DirectoryPath);
        CmsComposer cms = new(shared);
        _cmsResult = cms.ComposeClaimsFromFragments(BaseDocument, DirectoryPath);
        _providerResult = Provider(cms).GetClaimsDocumentNodes();
    }

    [Test]
    public void It_distinguishes_omitted_null_and_empty_collections_with_original_context()
    {
        _result.IsComplete.Should().Be(complete);
        _result
            .Diagnostics.Where(d => d.IsIncomplete)
            .Should()
            .Equal(
                complete
                    ? []
                    : new[]
                    {
                        new ClaimsCompositionDiagnostic(
                            ClaimsCompositionDiagnosticCode.NoResourceClaims,
                            _file,
                            ClaimSetName: claimSetName,
                            JsonPath: "$.resourceClaims"
                        ),
                    }
            );
    }

    [Test]
    public void It_preserves_complete_policy_and_CMS_continuation_through_valid_siblings()
    {
        _result.LoadResult.Failures.Should().BeEmpty();
        _cmsResult.Failures.Should().BeEmpty();
        AssertDocument(_result.LoadResult.Nodes!, _expected);
        AssertDocument(_cmsResult.Nodes!, _expected);
        AssertDocument(_providerResult, _expected);
    }
}

[TestFixture("crlf", false)]
[TestFixture("tab", false)]
[TestFixture("line-separator", false)]
[TestFixture("paragraph-separator", false)]
[TestFixture("next-line", false)]
[TestFixture("control", false)]
[TestFixture("crlf", true)]
[TestFixture("line-separator", true)]
public class Given_ClaimsFragmentComposer_logging_sanitization(string separatorName, bool readFailure)
    : ClaimsFragmentComposerDiagnosticsTests
{
    private readonly string _separator = separatorName switch
    {
        "crlf" => "\r\n",
        "tab" => "\t",
        "line-separator" => "\u2028",
        "paragraph-separator" => "\u2029",
        "next-line" => "\u0085",
        _ => "\u0001",
    };
    private RecordingLogger _sharedLog = null!;
    private RecordingLogger _cmsLog = null!;
    private ClaimsCompositionResult _result = null!;
    private ClaimsLoadResult _cmsResult = null!;
    private string _file = null!;
    private string _claim = null!;
    private string _claimSet = null!;

    [SetUp]
    public void Setup()
    {
        _file = Path.Combine(DirectoryPath, $"fragment{_separator}name-claims.json");
        _claim = $"edfi/missing{_separator}claim";
        _claimSet = $"Policy{_separator}Name";
        FileSystem.Discover = (_, pattern) => pattern == "*-claims.json" ? [_file] : [];
        FileSystem.Read = _ =>
            readFailure
                ? throw new IOException($"Cannot read {_file}")
                : JsonSerializer.Serialize(
                    new { name = _claimSet, resourceClaims = new[] { new { name = _claim } } }
                );
        _sharedLog = new();
        _cmsLog = new();
        _result = new SharedComposer(_sharedLog, FileSystem).ComposeClaimsFromFragments(
            BaseDocument,
            DirectoryPath
        );
        CmsComposer cms = new(new SharedComposer(_cmsLog, FileSystem));
        _cmsResult = cms.ComposeClaimsFromFragments(BaseDocument, DirectoryPath);
        // Read failures fall back to base. Unmatched grants never mutate the hierarchy.
        ClaimsDocument loaded = Provider(cms).GetClaimsDocumentNodes();
        JsonNode.DeepEquals(loaded.ClaimsHierarchyNode, BaseDocument.ClaimsHierarchyNode).Should().BeTrue();
    }

    [Test]
    public void It_logs_exact_sanitized_values_at_the_original_levels_through_both_composers()
    {
        foreach (RecordingLogger log in new[] { _sharedLog, _cmsLog })
        {
            string safeFile = Path.Combine(DirectoryPath, "fragmentname-claims.json");
            log.Entries.Where(e => e.Template == "Applying fragment: {FragmentFile}")
                .Should()
                .OnlyContain(e =>
                    e.Level == LogLevel.Information && (string)e.Values["FragmentFile"] == safeFile
                );
            log.Entries.Should().Contain(e => e.Template == "Applying fragment: {FragmentFile}");
            LogEntry entry = log.Entries.First(e =>
                e.Level == (readFailure ? LogLevel.Error : LogLevel.Warning)
            );
            if (readFailure)
            {
                entry.Values["Message"].Should().Be("Failed to read fragment files");
                ((string)entry.Values["Exception"])
                    .Should()
                    .StartWith($"System.IO.IOException: Cannot read {safeFile}");
            }
            else
            {
                entry.Values["FragmentFile"].Should().Be(safeFile);
                entry.Values["ClaimSetName"].Should().Be("PolicyName");
                entry.Values["ResourceClaimName"].Should().Be("edfi/missingclaim");
                if (_separator == "\u0001")
                {
                    log.Entries.Should()
                        .Contain(e =>
                            e.Level == LogLevel.Information
                            && e.Template
                                == "Registered claim set {ClaimSetName} defined by a claims fragment"
                            && (string)e.Values["ClaimSetName"] == "PolicyName"
                        );
                }
                else
                {
                    log.Entries.Should()
                        .Contain(e =>
                            e.Level == LogLevel.Error
                            && e.Template.StartsWith("Fragment {FragmentFile} defines a claim set name")
                            && (string)e.Values["FragmentFile"] == safeFile
                        );
                }
            }
            log.Entries.Should().AllSatisfy(e => e.Exception.Should().BeNull());
            log.Entries.SelectMany(e => e.Values.Values)
                .OfType<string>()
                .Should()
                .OnlyContain(value => !value.Contains(_separator));
        }
    }

    [Test]
    public void It_keeps_original_diagnostic_values_and_CMS_results()
    {
        _result.IsComplete.Should().BeFalse();
        ClaimsCompositionDiagnostic diagnostic = _result.Diagnostics[0];
        diagnostic.SourcePath.Should().Be(_file);
        if (readFailure)
        {
            diagnostic.Code.Should().Be(ClaimsCompositionDiagnosticCode.FragmentReadFailed);
            _result.LoadResult.Failures.Single().Exception!.Message.Should().Be($"Cannot read {_file}");
            _cmsResult.Nodes.Should().BeNull();
            _cmsResult.Failures.Select(f => f.FailureType).Should().Equal("IOError");
        }
        else
        {
            diagnostic.Code.Should().Be(ClaimsCompositionDiagnosticCode.UnmatchedGrantTarget);
            diagnostic.ClaimName.Should().Be(_claim);
            diagnostic.ClaimSetName.Should().Be(_claimSet);
            diagnostic.JsonPath.Should().Be("$.resourceClaims[0].name");
            _cmsResult.Failures.Should().BeEmpty();
            AssertDocument(_cmsResult.Nodes!, _result.LoadResult.Nodes!);
            JsonArray expectedCatalog = (JsonArray)BaseDocument.ClaimSetsNode.DeepClone();
            if (_separator == "\u0001")
            {
                expectedCatalog.Add(
                    new JsonObject { ["claimSetName"] = _claimSet, ["isSystemReserved"] = true }
                );
            }
            JsonNode.DeepEquals(_cmsResult.Nodes!.ClaimSetsNode, expectedCatalog).Should().BeTrue();
        }
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Template,
        Dictionary<string, object> Values,
        Exception? Exception
    );

    private sealed class RecordingLogger : ILogger
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullLogger.Instance.BeginScope(state)!;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            Dictionary<string, object> values = (
                (IEnumerable<KeyValuePair<string, object>>)state!
            ).ToDictionary();
            string template = (string)values["{OriginalFormat}"];
            values.Remove("{OriginalFormat}");
            Entries.Add(new(logLevel, template, values, exception));
        }
    }
}

[TestFixture("resource-claim", "", "$.resourceClaims[1]")]
[TestFixture("children", "edfi/parent\u2028name", "$.resourceClaims[1].children")]
[TestFixture("child", "edfi/parent\u2028name", "$.resourceClaims[1].children[1]")]
[TestFixture("missing-name", "", "$.resourceClaims[1].name")]
[TestFixture("null-name", "", "$.resourceClaims[1].name")]
[TestFixture(
    "grant-strategy",
    "edfi/student",
    "$.resourceClaims[1].authorizationStrategyOverridesForCRUD[1].authorizationStrategies[1]"
)]
[TestFixture(
    "default-strategy",
    "edfi/parent\u2028name",
    "$.resourceClaims[1]._defaultAuthorizationStrategiesForCrud[1].authorizationStrategies[1]"
)]
[TestFixture("claim-set", "edfi/parent\u2028name", "$.resourceClaims[1].claimSets[0]")]
[TestFixture("claim-set-action", "edfi/parent\u2028name", "$.resourceClaims[1].claimSets[0].actions[0]")]
[TestFixture(
    "claim-set-strategy",
    "edfi/parent\u2028name",
    "$.resourceClaims[1].claimSets[0].actions[0].authorizationStrategyOverrides[1]"
)]
public class Given_ClaimsFragmentComposer_null_structure(string scenario, string claimName, string jsonPath)
    : ClaimsFragmentComposerDiagnosticsTests
{
    private ClaimsCompositionResult _result = null!;
    private CmsComposer _cms = null!;
    private string _file = null!;

    [SetUp]
    public void Setup()
    {
        string fixtures = Path.Combine(
            TestContext.CurrentContext.TestDirectory,
            "Fixtures",
            "ClaimsComposition"
        );
        // Apply a valid fragment first so fatal loading cannot silently return partial policy.
        File.Copy(
            Path.Combine(fixtures, "first-wins", "fragments", "a-claims.json"),
            Path.Combine(DirectoryPath, "001-valid-claims.json")
        );
        _file = Path.Combine(DirectoryPath, "002-invalid-claims.json");
        File.Copy(Path.Combine(fixtures, "null-structures", "fragments", $"{scenario}-claims.json"), _file);
        SharedComposer shared = new(NullLogger.Instance);
        _cms = new(shared);
        _result = shared.ComposeClaimsFromFragments(BaseDocument, DirectoryPath);
    }

    [Test]
    public void It_returns_a_source_aware_malformed_failure_without_partial_policy()
    {
        _result.IsComplete.Should().BeFalse();
        _result.LoadResult.Nodes.Should().BeNull();
        _result.LoadResult.Failures.Should().ContainSingle().Which.Path.Should().Be(_file);
        _result
            .Diagnostics.Where(d => d.IsIncomplete)
            .Should()
            .Equal(
                new ClaimsCompositionDiagnostic(
                    ClaimsCompositionDiagnosticCode.MalformedFragment,
                    _file,
                    claimName,
                    "Policy\nName",
                    jsonPath
                )
            );
    }

    [Test]
    public void It_keeps_CMS_composition_fatal()
    {
        Action compose = () => _cms.ComposeClaimsFromFragments(BaseDocument, DirectoryPath);
        compose.Should().ThrowExactly<InvalidOperationException>();
    }

    [Test]
    public void It_keeps_CMS_loading_fatal_instead_of_returning_base_or_partial_policy()
    {
        ClaimsProvider provider = Provider(_cms);
        Action load = () => provider.GetClaimsDocumentNodes();
        load.Should().ThrowExactly<InvalidOperationException>();
        provider.IsClaimsValid.Should().BeFalse();
        ClaimsFailure failure = provider.ClaimsFailures.Should().ContainSingle().Which;
        failure.FailureType.Should().Be("OperationError");
        failure.Exception.Should().BeOfType<InvalidOperationException>();
    }
}

[TestFixture("omitted")]
[TestFixture("empty")]
[TestFixture("name-only")]
public class Given_ClaimsFragmentComposer_supported_children(string scenario)
    : ClaimsFragmentComposerDiagnosticsTests
{
    private ClaimsCompositionResult _result = null!;
    private ClaimsLoadResult _cmsResult = null!;
    private ClaimsDocument _providerResult = null!;
    private ClaimsDocument _expected = null!;

    [SetUp]
    public void Setup()
    {
        JsonObject parent = new() { ["name"] = "edfi/domain", ["isParent"] = true };
        if (scenario != "omitted")
        {
            parent["ChIlDrEn"] =
                scenario == "empty"
                    ? new JsonArray()
                    : new JsonArray(new JsonObject { ["name"] = "edfi/added" });
        }
        File.WriteAllText(
            Path.Combine(DirectoryPath, "valid-claims.json"),
            new JsonObject { ["resourceClaims"] = new JsonArray(parent) }.ToJsonString()
        );
        _expected = new(BaseDocument.ClaimSetsNode.DeepClone(), BaseDocument.ClaimsHierarchyNode.DeepClone());
        if (scenario == "name-only")
        {
            _expected.ClaimsHierarchyNode[0]!["claims"]!
                .AsArray()
                .Add(new JsonObject { ["name"] = "edfi/added" });
        }
        SharedComposer shared = new(NullLogger.Instance);
        _result = shared.ComposeClaimsFromFragments(BaseDocument, DirectoryPath);
        CmsComposer cms = new(shared);
        _cmsResult = cms.ComposeClaimsFromFragments(BaseDocument, DirectoryPath);
        _providerResult = Provider(cms).GetClaimsDocumentNodes();
    }

    [Test]
    public void It_preserves_exact_shared_and_CMS_composition()
    {
        _result.IsComplete.Should().BeTrue();
        _result.Diagnostics.Should().BeEmpty();
        _cmsResult.Failures.Should().BeEmpty();
        AssertDocument(_result.LoadResult.Nodes!, _expected);
        AssertDocument(_cmsResult.Nodes!, _expected);
        AssertDocument(_providerResult, _expected);
    }
}
