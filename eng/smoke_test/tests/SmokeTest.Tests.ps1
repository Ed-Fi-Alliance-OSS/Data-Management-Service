# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

Describe "Get-SmokeTestCredential" {
    BeforeAll {
        Import-Module "$PSScriptRoot/../modules/SmokeTest.psm1" -Force

        Mock Add-CmsClient -ModuleName SmokeTest { }
        Mock Get-CmsToken -ModuleName SmokeTest { "test-token" }
        Mock Get-DataStore -ModuleName SmokeTest { @([pscustomobject]@{ id = 1 }) }
        Mock Find-CmsVendorByCompany -ModuleName SmokeTest { $null }
        Mock Add-Vendor -ModuleName SmokeTest { 42 }
        Mock Add-Application -ModuleName SmokeTest { @{ Key = "test-key"; Secret = "test-secret" } }
    }

    It "creates the vendor when the tenant has none with this company" {
        Get-SmokeTestCredential -ConfigServiceUrl "http://localhost:8081" -Tenant "tenant-a" | Out-Null

        Should -Invoke Find-CmsVendorByCompany -ModuleName SmokeTest -Times 1 -Exactly -ParameterFilter {
            $Company -eq "Smoke Test Vendor" -and $Tenant -eq "tenant-a"
        }
        Should -Invoke Add-Vendor -ModuleName SmokeTest -Times 1 -Exactly
        Should -Invoke Add-Application -ModuleName SmokeTest -Times 1 -Exactly -ParameterFilter { $VendorId -eq 42 }
    }

    It "reuses an existing vendor instead of POSTing the company again" {
        # POST /v3/vendors is create-only, so a second POST for the same company is rejected with 400.
        Mock Find-CmsVendorByCompany -ModuleName SmokeTest { [pscustomobject]@{ id = 99; company = "Smoke Test Vendor" } }

        $credentials = Get-SmokeTestCredential -ConfigServiceUrl "http://localhost:8081" -ApplicationName "Second Smoke Application"

        $credentials.VendorId | Should -Be 99
        Should -Invoke Add-Vendor -ModuleName SmokeTest -Times 0 -Exactly
        Should -Invoke Add-Application -ModuleName SmokeTest -Times 1 -Exactly -ParameterFilter {
            $VendorId -eq 99 -and $ApplicationName -eq "Second Smoke Application"
        }
    }

    It "defaults -EducationOrganizationIds to the TPDM-inclusive envelope (5, 6, 7, 255901, 19255901, 100000, 200000, 300000)" {
        Get-SmokeTestCredential -ConfigServiceUrl "http://localhost:8081" | Out-Null

        Should -Invoke Add-Application -ModuleName SmokeTest -Times 1 -Exactly -ParameterFilter {
            ($EducationOrganizationIds -join ',') -eq '5,6,7,255901,19255901,100000,200000,300000'
        } -Because "removing 5, 6, 7 from the default re-breaks TPDM smoke coverage with 403s on educatorPreparationProgram (whose claim defaults to RelationshipsWithEdOrgsOnly)"
    }

    It "forwards an explicit -EducationOrganizationIds without merging it with the default envelope" {
        Get-SmokeTestCredential -ConfigServiceUrl "http://localhost:8081" -EducationOrganizationIds @(255901) | Out-Null

        Should -Invoke Add-Application -ModuleName SmokeTest -Times 1 -Exactly -ParameterFilter {
            ($EducationOrganizationIds -join ',') -eq '255901'
        }
    }

    It "returns every route context of the data store it selects, <Case>" -ForEach @(
        @{ Case = "districtId only"; Contexts = @(@{ contextKey = 'districtId'; contextValue = '255901' }) }
        @{ Case = "districtId and schoolYear"; Contexts = @(@{ contextKey = 'districtId'; contextValue = '255901' }, @{ contextKey = 'schoolYear'; contextValue = '2024' }) }
    ) {
        $script:selectedContexts = @($Contexts | ForEach-Object { [pscustomobject]$_ })
        Mock Get-DataStore -ModuleName SmokeTest { @([pscustomobject]@{ id = 77; dataStoreContexts = $script:selectedContexts }, [pscustomobject]@{ id = 78 }) }

        $credentials = Get-SmokeTestCredential -ConfigServiceUrl "http://localhost:8081"

        $actual = @($credentials.RouteContexts | ForEach-Object { "$($_.DataStoreId)|$($_.ContextKey)|$($_.ContextValue)" })
        $actual | Should -Be @($Contexts | ForEach-Object { "77|$($_.contextKey)|$($_.contextValue)" })
    }

    Context "credential values returned by the Configuration Service" {
        BeforeAll {
            # Synthetic values: '$' is a character CMS-generated secrets can contain, so the secret
            # must come back unexpanded.
            $script:syntheticKey = 'SYNTH-KEY-1292'
            $script:syntheticSecret = 'SYNTH$SECRET-1292'
            $script:recordTypes = @(
                [System.Management.Automation.InformationRecord],
                [System.Management.Automation.WarningRecord],
                [System.Management.Automation.ErrorRecord],
                [System.Management.Automation.VerboseRecord],
                [System.Management.Automation.DebugRecord]
            )

            function script:Test-IsStreamRecord {
                param($Item)
                foreach ($type in $script:recordTypes) {
                    if ($Item -is $type) { return $true }
                }
                return $false
            }
        }

        It "returns the exact key, secret, and application name the Configuration Service issued" {
            Mock Add-Application -ModuleName SmokeTest { @{ Key = 'SYNTH-KEY-1292'; Secret = 'SYNTH$SECRET-1292' } }

            $credentials = Get-SmokeTestCredential -ConfigServiceUrl "http://localhost:8081" -DataStoreIds @(7)

            $credentials.Key | Should -BeExactly $script:syntheticKey
            $credentials.Secret | Should -BeExactly $script:syntheticSecret
            $credentials.ApplicationName | Should -BeExactly "Smoke Test Application"
        }

        It "keeps the credential values off every non-success stream" {
            Mock Add-Application -ModuleName SmokeTest { @{ Key = 'SYNTH-KEY-1292'; Secret = 'SYNTH$SECRET-1292' } }

            # *>&1 only captures streams that are enabled; -Verbose -Debug turn those two on so a future
            # write of either value on them is observed.
            $all = @(& { Get-SmokeTestCredential -ConfigServiceUrl "http://localhost:8081" -DataStoreIds @(7) -Verbose -Debug } *>&1)
            $records = @($all | Where-Object { Test-IsStreamRecord $_ })
            $results = @($all | Where-Object { -not (Test-IsStreamRecord $_) })

            $records.Count | Should -BeGreaterThan 0 -Because "the real progress messages must have been captured, or the leak check below proves nothing"
            $results.Count | Should -Be 1 -Because "the credential hashtable is the only success-stream output"
            $results[0].Secret | Should -BeExactly $script:syntheticSecret
            foreach ($record in $records) {
                $text = $record | Out-String
                $text | Should -Not -Match ([regex]::Escape($script:syntheticKey))
                $text | Should -Not -Match ([regex]::Escape($script:syntheticSecret))
            }
        }

        It "fails without exposing values when the response <Case>" -ForEach @(
            @{ Case = "has no Key"; Field = "Key"; Response = @{ Secret = 'SYNTH$SECRET-1292' } }
            @{ Case = "has a null Secret"; Field = "Secret"; Response = @{ Key = 'SYNTH-KEY-1292'; Secret = $null } }
            @{ Case = "has a blank Secret"; Field = "Secret"; Response = @{ Key = 'SYNTH-KEY-1292'; Secret = '   ' } }
            @{ Case = "has a non-string Secret"; Field = "Secret"; Response = @{ Key = 'SYNTH-KEY-1292'; Secret = 1292 } }
            @{ Case = "has a control character in the Key"; Field = "Key"; Response = @{ Key = "SYNTH-KEY`n1292"; Secret = 'SYNTH$SECRET-1292' } }
            @{ Case = "has a control character in the Secret"; Field = "Secret"; Response = @{ Key = 'SYNTH-KEY-1292'; Secret = "SYNTH`tSECRET-1292" } }
        ) {
            $script:cannedResponse = $Response
            Mock Add-Application -ModuleName SmokeTest { $script:cannedResponse }

            $records = @(& {
                    try { Get-SmokeTestCredential -ConfigServiceUrl "http://localhost:8081" -DataStoreIds @(7) -Verbose -Debug }
                    catch { $script:caughtFailure = $_ }
                } *>&1)
            $caught = $script:caughtFailure
            $script:caughtFailure = $null

            $caught | Should -Not -BeNullOrEmpty -Because "an unusable credential must never be returned as a success"
            $caught.Exception.Message | Should -BeLike "*did not contain a usable $Field*"
            $records | Where-Object { -not (Test-IsStreamRecord $_) } | Should -BeNullOrEmpty -Because "no credential object may reach the success stream"
            foreach ($text in @(($caught | Out-String), $caught.Exception.ToString()) + @($records | ForEach-Object { $_ | Out-String })) {
                $text | Should -Not -Match 'SYNTH' -Because "diagnostics must name the field, never its value"
            }
        }
    }
}

Describe "Get-SmokeTestCredentialSummary" {
    BeforeAll {
        Import-Module "$PSScriptRoot/../modules/SmokeTest.psm1" -Force

        $script:smokeTestDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../../eng/smoke_test"))
        $script:credential = [pscustomobject]@{ Key = 'SYNTH-KEY-1292'; Secret = 'SYNTH$SECRET-1292'; ApplicationName = 'Smoke Test Application' }
        $script:credentialLines = @(
            "Application: Smoke Test Application",
            "Key:         SYNTH-KEY-1292",
            'Secret:      SYNTH$SECRET-1292',
            "The Configuration Service returns this secret only once. These bootstrap scripts do not write it to logs or files; your terminal, transcripts, or host logging may still retain it. Store it now."
        )
        $script:routeNote = "Tenant- or route-qualified smoke runs have not been validated with the smoke console; pass the matching DMS base as -BaseUrl and the token URL as -OAuthUrl."
        $script:schoolYearContexts = @(
            [pscustomobject]@{ DataStoreId = [long]101; ContextKey = 'schoolYear'; ContextValue = '2024' },
            [pscustomobject]@{ DataStoreId = [long]102; ContextKey = 'schoolYear'; ContextValue = '2025' }
        )

        # Parses one emitted command line and returns its parameters' values exactly as PowerShell
        # would bind them, so the test checks the quoting by execution semantics, not by text.
        function script:Get-CommandArgument {
            param([string]$Line)

            $tokens = $null
            $errors = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseInput($Line.Trim(), [ref]$tokens, [ref]$errors)
            $errors.Count | Should -Be 0 -Because "the emitted command must parse"
            $command = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true)
            $arguments = @{}
            $elements = @($command.CommandElements)
            for ($i = 1; $i -lt $elements.Count - 1; $i++) {
                if ($elements[$i] -is [System.Management.Automation.Language.CommandParameterAst]) {
                    $valueAst = $elements[$i + 1]
                    $valueAst | Should -BeOfType [System.Management.Automation.Language.StringConstantExpressionAst]
                    $valueAst.StringConstantType | Should -Be 'SingleQuoted' -Because "no value may be subject to expansion"
                    $arguments[$elements[$i].ParameterName] = $valueAst.Value
                }
            }
            return $arguments
        }
    }

    It "builds the exact summary and copyable command for a known Docker DMS base URL" {
        $lines = @(Get-SmokeTestCredentialSummary -Credential $script:credential -DmsBaseUrl 'http://localhost:8080/')

        $lines | Should -Be (@("--- Smoke test credentials ---") + $script:credentialLines + @(
                "Run the non-destructive API smoke tests:",
                "  Set-Location -LiteralPath '$($script:smokeTestDirectory)'",
                "  ./Invoke-NonDestructiveApiTests.ps1 -BaseUrl 'http://localhost:8080' -OAuthUrl 'http://localhost:8080/oauth/token' -Key 'SYNTH-KEY-1292' -Secret 'SYNTH`$SECRET-1292'"
            ))
    }

    It "points Set-Location at exactly the repository's eng/smoke_test directory" {
        $lines = @(Get-SmokeTestCredentialSummary -Credential $script:credential -DmsBaseUrl 'http://localhost:8080')
        $setLocation = Get-CommandArgument -Line ($lines | Where-Object { $_ -like "*Set-Location*" })

        $setLocation["LiteralPath"] | Should -BeExactly $script:smokeTestDirectory
        Test-Path -LiteralPath (Join-Path $setLocation["LiteralPath"] "Invoke-NonDestructiveApiTests.ps1") | Should -BeTrue
    }

    It "quotes every command value so PowerShell binds it unchanged" {
        $tricky = @{ Key = "K'ey`$1"; Secret = "a'b`$c$([char]0x2018)d``e`"f"; ApplicationName = 'Smoke Test Application' }
        $baseUrl = "http://localhost:8080/o'k`$x"

        $lines = @(Get-SmokeTestCredentialSummary -Credential $tricky -DmsBaseUrl $baseUrl)
        $arguments = Get-CommandArgument -Line ($lines | Where-Object { $_ -like "*Invoke-NonDestructiveApiTests.ps1*" })

        $arguments["Key"] | Should -BeExactly $tricky.Key
        $arguments["Secret"] | Should -BeExactly $tricky.Secret
        $arguments["BaseUrl"] | Should -BeExactly $baseUrl
        $arguments["OAuthUrl"] | Should -BeExactly "$baseUrl/oauth/token"
    }

    It "uses a placeholder base URL when the DMS URL is not known yet" {
        $lines = @(Get-SmokeTestCredentialSummary -Credential $script:credential)

        $lines | Should -Be (@("--- Smoke test credentials ---") + $script:credentialLines + @(
                "Run the non-destructive API smoke tests:",
                "  Set-Location -LiteralPath '$($script:smokeTestDirectory)'",
                "  ./Invoke-NonDestructiveApiTests.ps1 -BaseUrl '<dms-base-url>' -OAuthUrl '<dms-base-url>/oauth/token' -Key 'SYNTH-KEY-1292' -Secret 'SYNTH`$SECRET-1292'",
                "Replace <dms-base-url> with the IDE-hosted DMS URL once it is healthy."
            ))
    }

    It "prints tenant-qualified base and token URLs, without a command or data store label, for a tenant-only result" {
        $lines = @(Get-SmokeTestCredentialSummary -Credential $script:credential -DmsBaseUrl 'http://localhost:8080' -Tenant 'tenant-a' -RouteContexts @())

        $lines | Should -Be (@("--- Smoke test credentials ---") + $script:credentialLines + @(
                "Tenant:      tenant-a",
                "DMS base:    http://localhost:8080/tenant-a",
                "Token URL:   http://localhost:8080/tenant-a/oauth/token",
                $script:routeNote
            ))
    }

    It "keeps the placeholder inside tenant-only lines, with the replacement instruction, when the DMS URL is not known yet" {
        $lines = @(Get-SmokeTestCredentialSummary -Credential $script:credential -Tenant 'tenant-a')

        $lines | Should -Be (@("--- Smoke test credentials ---") + $script:credentialLines + @(
                "Tenant:      tenant-a",
                "DMS base:    <dms-base-url>/tenant-a",
                "Token URL:   <dms-base-url>/tenant-a/oauth/token",
                $script:routeNote,
                "Replace <dms-base-url> with the IDE-hosted DMS URL once it is healthy."
            ))
    }

    It "keeps the placeholder inside per-store school-year lines, with exactly one replacement instruction, when the DMS URL is not known yet" {
        $lines = @(Get-SmokeTestCredentialSummary -Credential $script:credential -RouteContexts $script:schoolYearContexts)

        $lines | Should -Be (@("--- Smoke test credentials ---") + $script:credentialLines + @(
                "Data store 101 (school year 2024): DMS base <dms-base-url>/2024  token <dms-base-url>/2024/oauth/token",
                "Data store 102 (school year 2025): DMS base <dms-base-url>/2025  token <dms-base-url>/2025/oauth/token",
                $script:routeNote,
                "Replace <dms-base-url> with the IDE-hosted DMS URL once it is healthy."
            ))
        @($lines | Where-Object { $_ -like "*Invoke-NonDestructiveApiTests.ps1*" }) | Should -BeNullOrEmpty
    }

    It "prints one line per school-year data store, without a command, <Case>" -ForEach @(
        @{ Case = "without a tenant"; Tenant = ""; Expected = @(
                "Data store 101 (school year 2024): DMS base http://localhost:8080/2024  token http://localhost:8080/2024/oauth/token",
                "Data store 102 (school year 2025): DMS base http://localhost:8080/2025  token http://localhost:8080/2025/oauth/token"
            ) }
        @{ Case = "with a tenant"; Tenant = "tenant-a"; Expected = @(
                "Tenant:      tenant-a",
                "Data store 101 (school year 2024): DMS base http://localhost:8080/tenant-a/2024  token http://localhost:8080/tenant-a/2024/oauth/token",
                "Data store 102 (school year 2025): DMS base http://localhost:8080/tenant-a/2025  token http://localhost:8080/tenant-a/2025/oauth/token"
            ) }
    ) {
        $lines = @(Get-SmokeTestCredentialSummary -Credential $script:credential -DmsBaseUrl 'http://localhost:8080' -Tenant $Tenant -RouteContexts $script:schoolYearContexts)

        $lines | Should -Be (@("--- Smoke test credentials ---") + $script:credentialLines + $Expected + @($script:routeNote))
    }

    It "lists other route contexts without any command or composed URL, <Case>" -ForEach @(
        @{ Case = "districtId only"; Tenant = ""; Contexts = @(
                [pscustomobject]@{ DataStoreId = [long]77; ContextKey = 'districtId'; ContextValue = '255901' }
            ); Expected = @(
                "Data store 77 route contexts: districtId=255901",
                "No DMS base or token URL is printed for these contexts: the DMS base is http://localhost:8080 followed by the context values in the order ROUTE_QUALIFIER_SEGMENTS lists their keys, and the token URL is that base plus /oauth/token."
            ) }
        @{ Case = "districtId and schoolYear on one store, with a tenant"; Tenant = "tenant-a"; Contexts = @(
                [pscustomobject]@{ DataStoreId = [long]77; ContextKey = 'districtId'; ContextValue = '255901' },
                [pscustomobject]@{ DataStoreId = [long]77; ContextKey = 'schoolYear'; ContextValue = '2024' }
            ); Expected = @(
                "Tenant:      tenant-a",
                "Data store 77 route contexts: districtId=255901, schoolYear=2024",
                "No DMS base or token URL is printed for these contexts: the DMS base is http://localhost:8080/tenant-a followed by the context values in the order ROUTE_QUALIFIER_SEGMENTS lists their keys, and the token URL is that base plus /oauth/token."
            ) }
    ) {
        $lines = @(Get-SmokeTestCredentialSummary -Credential $script:credential -DmsBaseUrl 'http://localhost:8080' -Tenant $Tenant -RouteContexts $Contexts)

        $lines | Should -Be (@("--- Smoke test credentials ---") + $script:credentialLines + $Expected + @($script:routeNote))
        @($lines | Where-Object { $_ -like "*Invoke-NonDestructiveApiTests.ps1*" -or $_ -like "*school year*" -or $_ -like "*/255901*" -or $_ -like "*/2024*" }) | Should -BeNullOrEmpty
    }

    It "prints <Case> escaped, composes no URL, and keeps the credential exact" -ForEach @(
        @{ Case = "a tenant with a line break"; Tenant = "tenant-a`nKey:         FORGED"; Contexts = @(); BaseUrl = 'http://localhost:8080'; Expected = @(
                "Tenant:      tenant-a\u000AKey:         FORGED"
            ) }
        @{ Case = "a school-year value with a terminal escape"; Tenant = ""; Contexts = @(
                [pscustomobject]@{ DataStoreId = [long]101; ContextKey = 'schoolYear'; ContextValue = "2024$([char]27)[2K" }
            ); BaseUrl = 'http://localhost:8080'; Expected = @(
                "Data store 101 route contexts: schoolYear=2024\u001B[2K"
            ) }
        @{ Case = "a context key with a C1 control and a tenant, before the DMS URL is known"; Tenant = "tenant-a"; Contexts = @(
                [pscustomobject]@{ DataStoreId = [long]77; ContextKey = "district$([char]0x9B)Id"; ContextValue = "255901$([char]0x2028)" }
            ); BaseUrl = ''; Expected = @(
                "Tenant:      tenant-a",
                "Data store 77 route contexts: district\u009BId=255901\u2028"
            ) }
    ) {
        $credential = [pscustomobject]@{ Key = "K'ey`$1"; Secret = "a'b`$c``d`"e"; ApplicationName = 'Smoke Test Application' }

        $lines = @(Get-SmokeTestCredentialSummary -Credential $credential -DmsBaseUrl $BaseUrl -Tenant $Tenant -RouteContexts $Contexts)

        $lines | Should -Be (@(
                "--- Smoke test credentials ---",
                "Application: Smoke Test Application",
                "Key:         $($credential.Key)",
                "Secret:      $($credential.Secret)",
                $script:credentialLines[3]
            ) + $Expected + @(
                "No DMS base or token URL is printed: the tenant or a route context contains control or line-break characters, shown escaped above. Correct the Configuration Service tenant or data-store contexts.",
                $script:routeNote
            ))
        @($lines | Where-Object { $_ -match '[\p{Cc}\p{Zl}\p{Zp}]' }) | Should -BeNullOrEmpty
        @($lines | Where-Object { $_ -like "*http*" -or $_ -like "*<dms-base-url>*" -or $_ -like "*Invoke-NonDestructiveApiTests.ps1*" }) | Should -BeNullOrEmpty
    }

    It "adds the token-limit prerequisite for self-contained identity" {
        $lines = @(Get-SmokeTestCredentialSummary -Credential @{ Key = 'SYNTH-KEY-1292'; Secret = 'SYNTH$SECRET-1292'; ApplicationName = 'Smoke Test Application' } -DmsBaseUrl 'http://localhost:8080' -SelfContainedIdentity)

        $lines[-1] | Should -BeExactly "Self-contained identity: set CONFIGURATION_SERVICE_IDENTITY_BEARER_TOKEN_PER_CLIENT_LIMIT=-1 before starting the stack (see eng/smoke_test/SMOKETEST.md); the default limit stops the smoke run."
        $lines.Count | Should -Be 9
    }

    It "replaces the run instructions with failure guidance when the run did not complete" {
        $lines = @(Get-SmokeTestCredentialSummary -Credential $script:credential -DmsBaseUrl 'http://localhost:8080' -Tenant 'tenant-a' -SelfContainedIdentity -Incomplete)

        $lines | Should -Be (@("--- Smoke test credentials (created before the failure; the stack is NOT ready) ---") + $script:credentialLines + @(
                "Fix the failure; CMS will reject a second 'Smoke Test Application' for this vendor (see README)."
            ))
    }

    It "rejects <Case> without exposing any value" -ForEach @(
        @{ Case = "a missing credential"; Credential = $null; DmsBaseUrl = ""; Field = "Key" }
        @{ Case = "a missing ApplicationName"; Credential = @{ Key = 'SYNTH-KEY-1292'; Secret = 'SYNTH$SECRET-1292' }; DmsBaseUrl = ""; Field = "ApplicationName" }
        @{ Case = "a null Key"; Credential = @{ Key = $null; Secret = 'SYNTH$SECRET-1292'; ApplicationName = 'App' }; DmsBaseUrl = ""; Field = "Key" }
        @{ Case = "a blank Secret"; Credential = @{ Key = 'SYNTH-KEY-1292'; Secret = ' '; ApplicationName = 'App' }; DmsBaseUrl = ""; Field = "Secret" }
        @{ Case = "a non-string Secret"; Credential = @{ Key = 'SYNTH-KEY-1292'; Secret = 1292; ApplicationName = 'App' }; DmsBaseUrl = ""; Field = "Secret" }
        @{ Case = "a control character in the Key"; Credential = @{ Key = "SYNTH`r-KEY"; Secret = 'SYNTH$SECRET-1292'; ApplicationName = 'App' }; DmsBaseUrl = ""; Field = "Key" }
        @{ Case = "a control character in the DMS base URL"; Credential = @{ Key = 'SYNTH-KEY-1292'; Secret = 'SYNTH$SECRET-1292'; ApplicationName = 'App' }; DmsBaseUrl = "http://SYNTH`n:8080"; Field = "DMS base URL" }
    ) {
        $failure = { Get-SmokeTestCredentialSummary -Credential $Credential -DmsBaseUrl $DmsBaseUrl } | Should -Throw -PassThru

        $failure.Exception.Message | Should -BeLike "*$Field*"
        ($failure | Out-String) + $failure.Exception.ToString() | Should -Not -Match 'SYNTH'
    }

    It "returns only strings, with no host or diagnostic records, even with Verbose and Debug enabled" {
        $all = @(& { Get-SmokeTestCredentialSummary -Credential $script:credential -DmsBaseUrl 'http://localhost:8080' -SelfContainedIdentity -Verbose -Debug } *>&1)

        $all.Count | Should -Be 9
        $all | ForEach-Object { $_ | Should -BeOfType [string] }
    }
}

Describe "Invoke-SmokeTestUtility" {
    BeforeAll {
        Import-Module "$PSScriptRoot/../modules/SmokeTest.psm1" -Force
    }

    It "redacts the key and secret from the echoed invocation instead of printing them in the clear" {
        $capturedOutput = $null
        $resolveFailure = $null

        try {
            Invoke-SmokeTestUtility -BaseUrl 'http://localhost:8080' -Key 'TESTKEY_abc123' -Secret 'TESTSECRET_s3cr3t' -ToolPath '/nonexistent/tool/path' -TestSet 'NonDestructiveApi' -OutVariable capturedOutput | Out-Null
        }
        catch {
            # Resolve-Path throws for this nonexistent -ToolPath, but only after the echo has
            # already written the key and secret to the success stream. -OutVariable captures
            # that echo regardless of the later terminating error, which is exactly how the
            # leak was reproduced. The error is kept so the assertions below can prove the
            # intended path was the one exercised.
            $resolveFailure = $_
        }

        $echoedText = $capturedOutput | Out-String

        $resolveFailure | Should -Not -BeNullOrEmpty -Because "this test only exercises the real leak if the run got past the echo and then died on the unresolvable tool path; a clean return would mean it is asserting against something else entirely"
        $echoedText | Should -Not -BeNullOrEmpty -Because "the echo must run before Resolve-Path fails, or this test is not exercising the real leak"
        $echoedText | Should -Not -Match 'TESTSECRET_s3cr3t' -Because "printing the raw secret to console output defeats the purpose of a machine-issued smoke-test credential that operators may paste into shared logs or terminals"
        $echoedText | Should -Not -Match 'TESTKEY_abc123' -Because "the key identifies the smoke-test application and should not be echoed in the clear alongside its secret"
        ([regex]::Matches($echoedText, '\*\*\*')).Count | Should -Be 2 -Because "both the key and the secret should be replaced with the same redaction placeholder"
    }

    It "still passes the real key and secret to dotnet, proving the redacted echo is a separate array and not an alias of the real argument list" {
        $toolRoot = Join-Path $TestDrive "tool"
        New-Item -ItemType Directory -Path (Join-Path $toolRoot "tools/net10.0/any") -Force | Out-Null
        New-Item -ItemType File -Path (Join-Path $toolRoot "tools/net10.0/any/EdFi.SmokeTest.Console.dll") -Force | Out-Null

        $calls = [System.Collections.Generic.List[object]]::new()
        Mock dotnet -ModuleName SmokeTest {
            # Pester mocks a native command with a function, and function argument
            # binding (unlike native-command binding) does not flatten the $options
            # array into $path's sibling positional argument, so $args here is
            # @($path, $options) with $options still nested one level deep. The
            # += below relies on PowerShell's array-concatenation semantics to
            # flatten that one level back out.
            $flatArgs = @()
            foreach ($item in $args) { $flatArgs += $item }
            $calls.Add($flatArgs)
        }

        Invoke-SmokeTestUtility -BaseUrl 'http://localhost:8080' -Key 'TESTKEY_abc123' -Secret 'TESTSECRET_s3cr3t' -ToolPath $toolRoot -TestSet 'NonDestructiveApi' | Out-Null

        $calls.Count | Should -Be 1 -Because "the mocked dotnet invocation should have been reached now that Resolve-Path succeeds against the staged tool tree"

        $realArgs = $calls[0] -join ' '

        $realArgs | Should -Match 'TESTKEY_abc123' -Because "if this were the same array reference as the redacted echo, the key dotnet receives would already have been overwritten with ***, and authentication would fail on every smoke-test leg"
        $realArgs | Should -Match 'TESTSECRET_s3cr3t' -Because "if this were the same array reference as the redacted echo, the secret dotnet receives would already have been overwritten with ***, and authentication would fail on every smoke-test leg"
        $realArgs | Should -Not -Match '\*\*\*' -Because "the argument list handed to dotnet must never be the redacted display copy"
    }
}
