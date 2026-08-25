// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.CommandLine;
using System.Text;
using System.Text.RegularExpressions;
using EdFi.DataManagementService.DocumentCacheAdmin;
using FluentAssertions;

namespace EdFi.DataManagementService.DocumentCacheAdmin.Tests.Unit;

[TestFixture]
[Parallelizable]
[Category("Help")]
public sealed class Given_DocumentCacheAdminHelpAndDocumentation
{
    private const string Fingerprint =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string RepositoryBlobUrlPrefix =
        "https://github.com/Ed-Fi-Alliance-OSS/Data-Management-Service/blob/main/";

    private static readonly Regex MarkdownLinkPattern = new(
        @"\[[^\]]+\]\((?<href>[^)#]+\.md)(?<anchor>#[^)]+)?\)",
        RegexOptions.Compiled
    );

    private string _readme = null!;

    [SetUp]
    public void Setup()
    {
        _readme = File.ReadAllText(ReadmePath());
    }

    [Test]
    public void It_documents_the_package_and_tool_identity()
    {
        _readme.Should().Contain(DocumentCacheAdminCliConstants.PackageId);
        _readme.Should().Contain(DocumentCacheAdminCliConstants.ToolCommandName);
        _readme.Should().Contain($"dotnet tool install --global {DocumentCacheAdminCliConstants.PackageId}");
        _readme.Should().Contain($"{DocumentCacheAdminCliConstants.ToolCommandName} --help");
    }

    [Test]
    public void It_documents_every_command_and_supported_option_name()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        foreach (Command command in rootCommand.Subcommands)
        {
            _readme.Should().Contain($"`{command.Name}`");
            DocumentedToolCommandLines()
                .Should()
                .Contain(
                    line =>
                        line.StartsWith(
                            $"{DocumentCacheAdminCliConstants.ToolCommandName} {command.Name} ",
                            StringComparison.Ordinal
                        ),
                    command.Name
                );
        }

        foreach (string optionName in DocumentedOptionNames())
        {
            _readme.Should().Contain(optionName);
        }
    }

    [Test]
    public void It_documents_confirmation_tokens_timeouts_and_exit_codes()
    {
        foreach (Command command in MutatingCommands())
        {
            _readme
                .Should()
                .Contain(DocumentCacheAdminTestCommandContracts.ExpectedConfirmationJsonValue(command.Name));
        }

        _readme
            .Should()
            .Contain(DocumentCacheAdminCommandSurface.OfflineWriterAdmissionClosedAndDrainedOptionValue);
        _readme.Should().Contain(DocumentCacheAdminCommandSurface.DefaultCommandTimeoutSeconds);
        _readme.Should().Contain(DocumentCacheAdminCommandSurface.DefaultStatusObservationTimeoutSeconds);
        _readme.Should().Contain(DocumentCacheAdminCommandSurface.DefaultStatusTimeoutSeconds);

        foreach (int exitCode in DocumentedExitCodes())
        {
            _readme.Should().Contain($"| {exitCode} |");
        }
    }

    [Test]
    public void It_documents_json_request_shapes_and_lower_camel_tokens()
    {
        _readme.Should().Contain("\"targetKey\"");
        _readme.Should().Contain("\"tenantKey\"");
        _readme.Should().Contain("\"dataStoreId\"");
        _readme.Should().Contain("\"confirmation\": \"onlineCacheRebuild\"");
        _readme.Should().Contain("\"confirmation\": \"offlineActivation\"");
        _readme.Should().Contain("\"expectedPhysicalSourceFingerprint\"");
        _readme.Should().Contain("\"offlineWriterAdmission\"");
        _readme.Should().Contain("\"offlineWriterAdmission\": \"closedAndDrained\"");
        _readme.Should().NotContain("\"confirmation\": \"offlineActivationWritersClosedAndDrained\"");
        _readme.Should().Contain(Fingerprint);
    }

    [Test]
    public void It_keeps_documented_command_examples_parseable_against_the_real_help_surface()
    {
        RootCommand rootCommand = DocumentCacheAdminCommandSurface.CreateRootCommand();

        foreach (string commandLine in DocumentedToolCommandLines())
        {
            string[] tokens = SplitCommandLine(commandLine);
            tokens[0].Should().Be(DocumentCacheAdminCliConstants.ToolCommandName, commandLine);

            ParseResult parseResult = rootCommand.Parse(tokens[1..]);

            parseResult.Errors.Should().BeEmpty(commandLine);
        }
    }

    [Test]
    public void It_keeps_command_examples_secret_safe()
    {
        foreach (string commandLine in DocumentedToolCommandLines())
        {
            commandLine.Should().NotContain("--connection-string");
            commandLine.Should().NotContain("--client-secret");
            commandLine.Should().NotContain("--password");
            commandLine.Should().NotContain("--secret");
            commandLine.Should().NotContain("Password=");
        }
    }

    [Test]
    public void It_links_the_required_runbook_material()
    {
        _readme.Should().Contain("#guarded-new-empty-activation");
        _readme.Should().Contain("#offline-read-acceleration-activation");
        _readme.Should().Contain("#offline-deactivation");
        _readme.Should().Contain("#online-cache-rebuild");
        _readme.Should().Contain("#baseline-rebuild-deactivation-and-scrub");
        _readme.Should().Contain("#explicit-integrity-scrub");
        _readme.Should().Contain("#freshness-and-reconciliation");
        _readme.Should().Contain("#projection-health-and-deployment-owned-cdc-readiness");
        _readme.Should().Contain("#cache-ahead-invariant-recovery");
        _readme.Should().Contain("#contract-change-and-repair-operations");
        _readme.Should().Contain("18-document-cache/07-documentcache-integration-tests-and-runbooks.md");
        _readme.Should().Contain("19-cdc-kafka/07-ops-docs-runbooks.md");
        _readme.Should().Contain("persistent poison");
    }

    [Test]
    public void It_documents_out_of_scope_boundaries()
    {
        string readme = NormalizeWhitespace(_readme);

        readme.Should().Contain("Kafka connector setup");
        readme.Should().Contain("connector teardown");
        readme.Should().Contain("source replacement");
        readme.Should().Contain("binding retirement");
        readme.Should().Contain("topic management");
        readme.Should().Contain("CDC bootstrap orchestration");
        readme.Should().Contain("release pipeline work");
    }

    [Test]
    public void It_resolves_all_packaged_repository_markdown_links_and_anchors()
    {
        MatchCollection matches = MarkdownLinkPattern.Matches(_readme);
        matches.Should().NotBeEmpty();

        foreach (Match match in matches)
        {
            string href = match.Groups["href"].Value;
            href.Should()
                .StartWith(
                    RepositoryBlobUrlPrefix,
                    $"README link '{href}' should be usable from the installed package"
                );

            string linkedPath = RepositoryLocalPathFromUrl(href);

            File.Exists(linkedPath).Should().BeTrue($"README link '{href}' should resolve");

            if (!match.Groups["anchor"].Success)
            {
                continue;
            }

            string expectedAnchor = match.Groups["anchor"].Value[1..];
            MarkdownAnchors(linkedPath).Should().Contain(expectedAnchor, href);
        }
    }

    private static IEnumerable<string> DocumentedToolCommandLines() =>
        File.ReadLines(ReadmePath())
            .Select(line => line.Trim())
            .Where(line =>
                line.StartsWith(
                    $"{DocumentCacheAdminCliConstants.ToolCommandName} ",
                    StringComparison.Ordinal
                )
            );

    private static IEnumerable<string> DocumentedOptionNames() =>
        [
            DocumentCacheAdminCommandSurface.JsonOptionName,
            DocumentCacheAdminCommandSurface.VerboseOptionName,
            DocumentCacheAdminCommandSurface.SettingsOptionName,
            DocumentCacheAdminCommandSurface.EnvironmentOptionName,
            DocumentCacheAdminCommandSurface.DatastoreOptionName,
            DocumentCacheAdminCommandSurface.DataStoreIdOptionName,
            DocumentCacheAdminCommandSurface.TenantKeyOptionName,
            DocumentCacheAdminCommandSurface.RequestJsonOptionName,
            DocumentCacheAdminCommandSurface.StatusObservationTimeoutSecondsOptionName,
            DocumentCacheAdminCommandSurface.StatusTimeoutSecondsOptionName,
            DocumentCacheAdminCommandSurface.ConfirmOptionName,
            DocumentCacheAdminCommandSurface.ExpectedPhysicalSourceFingerprintOptionName,
            DocumentCacheAdminCommandSurface.CommandTimeoutSecondsOptionName,
            DocumentCacheAdminCommandSurface.OfflineWriterAdmissionOptionName,
        ];

    private static IEnumerable<int> DocumentedExitCodes() =>
        [
            DocumentCacheAdminExitCodes.Success,
            DocumentCacheAdminExitCodes.UnexpectedFailure,
            DocumentCacheAdminExitCodes.RejectedNoMutation,
            DocumentCacheAdminExitCodes.FailedNoMutation,
            DocumentCacheAdminExitCodes.IncompleteRetryable,
            DocumentCacheAdminExitCodes.ArgumentError,
            DocumentCacheAdminExitCodes.ConfigurationError,
        ];

    private static IEnumerable<Command> MutatingCommands() =>
        DocumentCacheAdminCommandSurface
            .CreateRootCommand()
            .Subcommands.Where(command => DocumentCacheAdminCommandSurface.IsMutatingCommand(command.Name));

    private static string[] SplitCommandLine(string commandLine)
    {
        List<string> tokens = [];
        StringBuilder currentToken = new();
        bool inDoubleQuotes = false;

        foreach (char character in commandLine)
        {
            if (character == '"')
            {
                inDoubleQuotes = !inDoubleQuotes;
                continue;
            }

            if (char.IsWhiteSpace(character) && !inDoubleQuotes)
            {
                AddCurrentToken();
                continue;
            }

            currentToken.Append(character);
        }

        inDoubleQuotes.Should().BeFalse(commandLine);
        AddCurrentToken();

        return [.. tokens];

        void AddCurrentToken()
        {
            if (currentToken.Length == 0)
            {
                return;
            }

            tokens.Add(currentToken.ToString());
            currentToken.Clear();
        }
    }

    private static IEnumerable<string> MarkdownAnchors(string path) =>
        File.ReadLines(path)
            .Where(line => line.StartsWith('#'))
            .Select(ToMarkdownAnchor)
            .Where(anchor => anchor.Length > 0);

    private static string RepositoryLocalPathFromUrl(string href)
    {
        string repositoryRelativePath = href[RepositoryBlobUrlPrefix.Length..];
        repositoryRelativePath.Should().NotContain("..", href);

        string repositoryRoot = RepositoryRoot();
        string linkedPath = Path.GetFullPath(
            Path.Combine(repositoryRoot, repositoryRelativePath.Replace('/', Path.DirectorySeparatorChar))
        );

        linkedPath
            .Should()
            .StartWith(
                repositoryRoot + Path.DirectorySeparatorChar,
                $"README link '{href}' should stay inside the repository"
            );

        return linkedPath;
    }

    private static string NormalizeWhitespace(string value) => Regex.Replace(value, @"\s+", " ");

    private static string ToMarkdownAnchor(string headingLine)
    {
        int headingTextIndex = headingLine.IndexOf(' ');
        if (headingTextIndex < 0)
        {
            return string.Empty;
        }

        string heading = headingLine[(headingTextIndex + 1)..].Trim();
        StringBuilder anchor = new();
        bool previousWasHyphen = false;

        foreach (char character in heading.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                anchor.Append(character);
                previousWasHyphen = false;
                continue;
            }

            if (char.IsWhiteSpace(character) || character == '-')
            {
                if (anchor.Length == 0 || previousWasHyphen)
                {
                    continue;
                }

                anchor.Append('-');
                previousWasHyphen = true;
            }
        }

        return anchor.ToString().TrimEnd('-');
    }

    private static string ReadmePath() => Path.Combine(ReadmeDirectory(), "README.md");

    private static string ReadmeDirectory() =>
        Path.Combine(RepositoryRoot(), "src", "dms", "clis", "EdFi.DataManagementService.DocumentCacheAdmin");

    private static string RepositoryRoot()
    {
        DirectoryInfo? currentDirectory = new(AppContext.BaseDirectory);

        while (currentDirectory is not null)
        {
            string solutionPath = Path.Combine(
                currentDirectory.FullName,
                "src",
                "dms",
                "EdFi.DataManagementService.sln"
            );

            if (File.Exists(solutionPath))
            {
                return currentDirectory.FullName;
            }

            currentDirectory = currentDirectory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root.");
    }
}
