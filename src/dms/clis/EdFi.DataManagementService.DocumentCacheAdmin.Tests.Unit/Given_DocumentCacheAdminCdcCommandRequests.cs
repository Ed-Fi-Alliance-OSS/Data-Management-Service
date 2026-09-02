// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using EdFi.DataManagementService.Core.Configuration;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

/// <summary>
/// Covers the parse-to-request hop for every cdc verb. The dispatcher tests construct requests
/// directly, so without these the builder that turns an operator's command line into one is never
/// exercised.
/// </summary>
[TestFixture]
[Parallelizable]
[Category("CommandSurface")]
public sealed class Given_DocumentCacheAdminCdcCommandRequests
{
    private const string BindingJsonPath = "binding.json";
    private const string BindingJsonContent = "{\"deploymentKey\":\"deployment\"}";

    [Test]
    public void It_builds_the_enable_request_from_its_provisioning_evidence()
    {
        DocumentCacheAdminCdcCommandRequest request = Build(
            DocumentCacheAdminCommandSurface.CdcEnableVerbName
        );

        request.VerbName.Should().Be(DocumentCacheAdminCommandSurface.CdcEnableVerbName);
        request.TargetKey.Should().Be(DocumentCacheTargetKey.Create(string.Empty, 1));
        request
            .DatabaseCreationMode.Should()
            .Be(
                DocumentCacheAdminCommandSurface.DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue
            );
        request
            .WriteAdmission.Should()
            .Be(DocumentCacheAdminCommandSurface.WriteAdmissionClosedNeverOpenedOptionValue);
        request.PreviousGeneration.Should().BeNull();
        request.BindingJson.Should().BeNull();
        request.ConnectorAlreadyAbsent.Should().BeFalse();
    }

    [Test]
    public void It_builds_the_replace_source_request_with_the_generation_being_replaced()
    {
        DocumentCacheAdminCdcCommandRequest request = Build(
            DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName
        );

        request.PreviousGeneration.Should().Be(3);
        request
            .DatabaseCreationMode.Should()
            .Be(
                DocumentCacheAdminCommandSurface.DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue
            );
        request.ConnectorAlreadyAbsent.Should().BeFalse();
    }

    [Test]
    public void It_builds_the_retire_request_carrying_the_connector_absence_assertion()
    {
        DocumentCacheAdminCdcCommandRequest request = Build(
            DocumentCacheAdminCommandSurface.CdcRetireVerbName,
            DocumentCacheAdminCommandSurface.ConnectorAlreadyAbsentOptionName
        );

        request.ConnectorAlreadyAbsent.Should().BeTrue();
        request.PreviousGeneration.Should().BeNull();
        request.DatabaseCreationMode.Should().BeNull();
    }

    [Test]
    public void It_leaves_the_connector_absence_assertion_unmade_when_retire_omits_the_flag()
    {
        Build(DocumentCacheAdminCommandSurface.CdcRetireVerbName).ConnectorAlreadyAbsent.Should().BeFalse();
    }

    [Test]
    public void It_builds_the_adopt_request_from_the_loaded_binding_record()
    {
        DocumentCacheAdminCdcCommandRequest request = Build(
            DocumentCacheAdminCommandSurface.CdcAdoptVerbName
        );

        request.BindingJson.Should().Be(BindingJsonContent);
    }

    /// <summary>
    /// The builder reads the previous-generation and connector-already-absent options by name for
    /// every verb, but the surface adds each to one verb only. System.CommandLine returns the default
    /// for an out-of-scope name today, while its own documentation for that overload says it throws.
    /// These cases pin the behavior the builder depends on, so an SDK update that reinstates the
    /// documented contract fails here rather than at an operator's command line.
    /// </summary>
    [TestCaseSource(nameof(VerbsWithoutTheScopedOptions))]
    public void It_reads_the_verb_scoped_options_on_a_verb_that_does_not_declare_them(string verbName)
    {
        DocumentCacheAdminCdcCommandRequest request = Build(verbName);

        request.PreviousGeneration.Should().BeNull();
        request.ConnectorAlreadyAbsent.Should().BeFalse();
    }

    [Test]
    public void It_refuses_a_binding_record_the_loader_cannot_read()
    {
        bool built = DocumentCacheAdminCdcCommandRequestBuilder.TryBuild(
            Parse(VerbArgs(DocumentCacheAdminCommandSurface.CdcAdoptVerbName)),
            DocumentCacheAdminCommandSurface.CdcAdoptVerbName,
            InvocationTarget,
            _ => throw new IOException("disk gone"),
            out DocumentCacheAdminCdcCommandRequest? request,
            out string? failure
        );

        built.Should().BeFalse();
        request.Should().BeNull();
        failure.Should().Contain(DocumentCacheAdminCommandSurface.BindingJsonOptionName);
        failure.Should().Contain("disk gone");
    }

    [TestCase("")]
    [TestCase("   ")]
    public void It_refuses_an_empty_binding_record(string bindingJson)
    {
        bool built = DocumentCacheAdminCdcCommandRequestBuilder.TryBuild(
            Parse(VerbArgs(DocumentCacheAdminCommandSurface.CdcAdoptVerbName)),
            DocumentCacheAdminCommandSurface.CdcAdoptVerbName,
            InvocationTarget,
            _ => bindingJson,
            out DocumentCacheAdminCdcCommandRequest? request,
            out string? failure
        );

        built.Should().BeFalse();
        request.Should().BeNull();
        failure.Should().Contain("adoption requires the complete binding record");
    }

    private static IEnumerable<string> VerbsWithoutTheScopedOptions() =>
        DocumentCacheAdminCommandSurface.CdcVerbNames.Where(verbName =>
            verbName != DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName
            && verbName != DocumentCacheAdminCommandSurface.CdcRetireVerbName
        );

    private static DocumentCacheAdminInvocationTarget InvocationTarget =>
        new(DocumentCacheTargetKey.Create(string.Empty, 1));

    private static DocumentCacheAdminCdcCommandRequest Build(string verbName, params string[] additionalArgs)
    {
        ParseResult parseResult = Parse(VerbArgs(verbName, additionalArgs));
        parseResult.Errors.Should().BeEmpty();

        bool built = DocumentCacheAdminCdcCommandRequestBuilder.TryBuild(
            parseResult,
            verbName,
            InvocationTarget,
            _ => BindingJsonContent,
            out DocumentCacheAdminCdcCommandRequest? request,
            out string? failure
        );

        built.Should().BeTrue(failure);
        return request!;
    }

    private static string[] VerbArgs(string verbName, params string[] additionalArgs)
    {
        List<string> args =
        [
            DocumentCacheAdminCommandSurface.CdcCommandName,
            verbName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            "1",
        ];

        if (DocumentCacheAdminCommandSurface.RequiresCdcProvisioningEvidence(verbName))
        {
            args.AddRange([
                DocumentCacheAdminCommandSurface.DatabaseCreationModeOptionName,
                DocumentCacheAdminCommandSurface.DatabaseCreationModeCreatedForInitialCdcProvisioningOptionValue,
                DocumentCacheAdminCommandSurface.WriteAdmissionOptionName,
                DocumentCacheAdminCommandSurface.WriteAdmissionClosedNeverOpenedOptionValue,
            ]);
        }

        if (verbName == DocumentCacheAdminCommandSurface.CdcReplaceSourceVerbName)
        {
            args.AddRange([
                DocumentCacheAdminCommandSurface.PreviousGenerationOptionName,
                "3",
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                DocumentCacheAdminCommandSurface.CdcSourceReplacementConfirmationOptionValue,
            ]);
        }

        if (verbName == DocumentCacheAdminCommandSurface.CdcRetireVerbName)
        {
            args.AddRange([
                DocumentCacheAdminCommandSurface.ConfirmOptionName,
                DocumentCacheAdminCommandSurface.CdcBindingRetirementConfirmationOptionValue,
            ]);
        }

        if (verbName == DocumentCacheAdminCommandSurface.CdcAdoptVerbName)
        {
            args.AddRange([DocumentCacheAdminCommandSurface.BindingJsonOptionName, BindingJsonPath]);
        }

        args.AddRange(additionalArgs);
        return [.. args];
    }

    private static ParseResult Parse(string[] args) =>
        DocumentCacheAdminCommandSurface.CreateRootCommand().Parse(args);
}
