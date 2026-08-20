// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Performance.Harness.Results;
using FluentAssertions;

namespace EdFi.DataManagementService.Performance.Harness.Tests.Unit.Results;

[TestFixture]
public class Given_A_Serialized_Results_Document
{
    private PerfResultsDocument _document = null!;
    private string _json = null!;

    [SetUp]
    public void Setup()
    {
        _document = PerfResultsDocument.Create([ResultSamples.Postgresql(), ResultSamples.Mssql()]);
        _json = PerfArtifactJson.Serialize(_document);
    }

    [Test]
    public void It_round_trips()
    {
        PerfArtifactJson
            .Deserialize<PerfResultsDocument>(_json)
            .Should()
            .BeEquivalentTo(_document, options => options.WithStrictOrdering());
    }

    [Test]
    public void It_uses_camel_case_names()
    {
        _json.Should().Contain("\"schemaVersion\": \"1.0.0\"");
        _json.Should().Contain("\"p50Ms\"");
        _json.Should().Contain("\"pageSelectionSqlSha256\"");
    }

    [Test]
    public void It_omits_inapplicable_provider_metrics()
    {
        // The PostgreSQL row has null SQL Server metrics and vice versa; omitted, not null.
        _json.Should().NotContain("null");
    }

    [Test]
    public void It_uses_lf_only_newlines()
    {
        _json.Should().NotContain("\r");
    }
}

[TestFixture]
public class Given_A_Serialized_Run_Manifest
{
    private PerfRunManifest _manifest = null!;
    private string _json = null!;

    [SetUp]
    public void Setup()
    {
        _manifest = ResultSamples.Manifest();
        _json = PerfArtifactJson.Serialize(_manifest);
    }

    [Test]
    public void It_round_trips()
    {
        PerfArtifactJson
            .Deserialize<PerfRunManifest>(_json)
            .Should()
            .BeEquivalentTo(_manifest, options => options.WithStrictOrdering());
    }

    [Test]
    public void It_stamps_the_schema_version()
    {
        _manifest.SchemaVersion.Should().Be("1.0.0");
    }

    [Test]
    public void It_sorts_server_settings_by_name()
    {
        _manifest
            .Environment.Server.Settings.Select(setting => setting.Name)
            .Should()
            .Equal("shared_buffers", "work_mem");
    }

    [Test]
    public void It_records_both_commit_roles()
    {
        _manifest.Commits.RunnerCommit.Should().Be(ResultSamples.RunnerCommit);
        _manifest.Commits.SubjectCommit.Should().Be(ResultSamples.SubjectCommit);
    }
}
