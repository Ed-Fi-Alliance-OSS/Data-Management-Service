// SPDX-License-Identifier: Apache-2.0
// Licensed to the Ed-Fi Alliance under one or more agreements.
// The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
// See the LICENSE and NOTICES files in the project root for more information.

using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;

namespace EdFi.DmsConfigurationService.SecurityMetadataTools.Tests.Unit;

[TestFixture("iso-8859-1")]
[TestFixture("utf-8")]
[TestFixture("utf-16le")]
[TestFixture("utf-16be")]
public class Given_an_encoded_xml_conversion_command(string encodingName)
{
    private CommandWorkspace _workspace = null!;
    private CommandResult _result = null!;

    [SetUp]
    public async Task Setup()
    {
        _workspace = new();
        Encoding encoding = encodingName switch
        {
            "iso-8859-1" => Encoding.Latin1,
            "utf-8" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            "utf-16le" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode,
            _ => throw new InvalidOperationException($"Unknown encoding: {encodingName}"),
        };
        // UTF-16 uses its BOM alone; other encodings are declared in the byte stream.
        string declaration = encodingName.StartsWith("utf-16", StringComparison.Ordinal)
            ? string.Empty
            : $"<?xml version=\"1.0\" encoding=\"{encodingName}\"?>";
        string xml =
            declaration
            + """
                <SecurityMetadata>
                  <Claims>
                    <Claim name="http://ed-fi.org/ods/identity/claims/students">
                      <DefaultAuthorization>
                        <Action name="Read">
                          <AuthorizationStrategies><AuthorizationStrategy name="Défaut" /></AuthorizationStrategies>
                        </Action>
                      </DefaultAuthorization>
                      <ClaimSets>
                        <ClaimSet name="Café-Readers">
                          <Actions>
                            <Action name="Read">
                              <AuthorizationStrategyOverrides><AuthorizationStrategy name="Réservé" /></AuthorizationStrategyOverrides>
                            </Action>
                          </Actions>
                        </ClaimSet>
                      </ClaimSets>
                    </Claim>
                  </Claims>
                </SecurityMetadata>
                """;
        await File.WriteAllBytesAsync(
            _workspace.InputPath,
            [.. encoding.GetPreamble(), .. encoding.GetBytes(xml)]
        );
        _result = await _workspace.ConvertAsync();
    }

    [TearDown]
    public void Teardown() => _workspace.Dispose();

    [Test]
    public void It_succeeds_without_console_output()
    {
        _result.ExitCode.Should().Be(0, _result.Error);
        _result.Output.Should().BeEmpty();
        _result.Error.Should().BeEmpty();
    }

    [Test]
    public void It_preserves_exact_policy_names_through_conversion()
    {
        JsonNode expected = JsonNode.Parse(
            """
            {
              "claimSets": [{ "claimSetName": "CaféReaders", "isSystemReserved": false }],
              "claimsHierarchy": [{
                "name": "http://ed-fi.org/identity/claims/ed-fi/students",
                "defaultAuthorization": {
                  "actions": [{ "name": "Read", "authorizationStrategies": [{ "name": "Défaut" }] }]
                },
                "claimSets": [{
                  "name": "CaféReaders",
                  "actions": [{ "name": "Read", "authorizationStrategyOverrides": [{ "name": "Réservé" }] }]
                }]
              }]
            }
            """
        )!;
        JsonNode actual = JsonNode.Parse(File.ReadAllText(_workspace.OutputPath))!;
        JsonNode.DeepEquals(actual, expected).Should().BeTrue(actual.ToJsonString());
    }
}
