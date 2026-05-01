// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using NUnit.Framework;

namespace EdFi.DataManagementService.Backend.Ddl.Tests.Unit;

/// <summary>
/// Shared base for integration fixture golden tests that live as a sibling of the DDL test
/// project. Resolves fixture directories relative to the IntegrationFixtures sibling project and
/// trims trailing blank lines from generated SQL files so dialect output matches the checked-in
/// goldens deterministically.
/// </summary>
public abstract class IntegrationFixtureGoldenTestBase : DdlGoldenFixtureTestBase
{
    private static readonly UTF8Encoding _utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    protected abstract string FixtureName { get; }

    protected sealed override string ResolveFixtureDirectory(string projectRoot) =>
        Path.Combine(
            projectRoot,
            "..",
            "EdFi.DataManagementService.Backend.IntegrationFixtures",
            FixtureName
        );

    protected sealed override void NormalizeActualOutput(string actualDir)
    {
        foreach (var path in Directory.GetFiles(actualDir, "*.sql"))
        {
            var content = File.ReadAllText(path);
            while (content.EndsWith("\n\n", StringComparison.Ordinal))
            {
                content = content[..^1];
            }

            File.WriteAllText(path, content, _utf8NoBom);
        }
    }
}

[TestFixture]
public class Given_FixtureRunner_With_Profile_Collection_Aligned_Extension_Fixture
    : IntegrationFixtureGoldenTestBase
{
    protected override string FixtureName => "profile-collection-aligned-extension";
}

[TestFixture]
public class Given_FixtureRunner_With_Profile_Collection_Aligned_Extension_Hidden_Descendant_Fixture
    : IntegrationFixtureGoldenTestBase
{
    protected override string FixtureName => "profile-collection-aligned-extension-hidden-descendant";
}

[TestFixture]
public class Given_FixtureRunner_With_Profile_Nested_And_Root_Extension_Children_Fixture
    : IntegrationFixtureGoldenTestBase
{
    protected override string FixtureName => "profile-nested-and-root-extension-children";
}
