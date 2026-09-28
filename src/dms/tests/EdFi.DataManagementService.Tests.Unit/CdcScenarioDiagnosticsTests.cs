// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using EdFi.DataManagementService.Tests.E2E.Cdc;
using FluentAssertions;

namespace EdFi.DataManagementService.Tests.Unit;

[TestFixture]
public class Given_CdcScenarioDiagnostics
{
    private string _directory = "";
    private string _path = "";
    private string _invocation = "";
    private CdcScenarioDiagnostics _diagnostics = null!;

    [SetUp]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "cdc-diagnostics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "scenario.json");
        _invocation = Guid.NewGuid().ToString("D");
        _diagnostics = new(_path, _invocation);
    }

    [TearDown]
    public void Teardown() => Directory.Delete(_directory, recursive: true);

    [Test]
    public async Task It_persists_each_checkpoint_without_waiting_for_finalization_or_writing_outcomes()
    {
        await _diagnostics.WriteAsync("CDC-E2E-06:old-runtime-disposed");
        (await File.ReadAllLinesAsync(_path + ".checkpoints"))
            .Should()
            .Equal(_invocation, "CDC-E2E-06:old-runtime-disposed");
        File.Exists(_path).Should().BeFalse();
    }

    [Test]
    public void It_rejects_reusing_an_invocation_journal()
    {
        Action create = () => _ = new CdcScenarioDiagnostics(_path, _invocation);
        create.Should().Throw<IOException>();
    }

    [TestCase("embedded\nline")]
    [TestCase("embedded\rline")]
    public async Task It_rejects_multiline_checkpoints(string checkpoint)
    {
        Func<Task> write = () => _diagnostics.WriteAsync(checkpoint);
        await write.Should().ThrowAsync<InvalidOperationException>();
        (await File.ReadAllLinesAsync(_path + ".checkpoints")).Should().Equal(_invocation);
    }

    [Test]
    public async Task It_rejects_oversized_checkpoints()
    {
        Func<Task> write = () => _diagnostics.WriteAsync(new string('x', 513));
        await write.Should().ThrowAsync<InvalidOperationException>();
        (await File.ReadAllLinesAsync(_path + ".checkpoints")).Should().Equal(_invocation);
    }

    [Test]
    public async Task It_bounds_the_journal_and_preserves_completed_checkpoints_on_overflow()
    {
        for (int index = 0; index < CdcScenarioDiagnostics.MaximumCheckpoints; index++)
        {
            await _diagnostics.WriteAsync("CDC-E2E-06:work-page: size=2 selected=2");
        }
        Func<Task> write = () => _diagnostics.WriteAsync("CDC-E2E-06:old-runtime-disposed");
        await write.Should().ThrowAsync<InvalidOperationException>().WithMessage("CDC_API_DIAGNOSTICS_LIMIT");
        (await File.ReadAllLinesAsync(_path + ".checkpoints")).Should().HaveCount(2049);
    }
}
