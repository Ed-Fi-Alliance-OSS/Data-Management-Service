# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

param()

Describe "DMS effective schema hash parser" {
    BeforeAll {
        $script:modulePath = [System.IO.Path]::GetFullPath(
            (Join-Path $PSScriptRoot "../effective-schema-hash.psm1")
        )
        Import-Module $script:modulePath -Force
        $script:hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"
    }

    It "parses the legacy plain-text log line" {
        $output = @(
            "some unrelated startup line"
            "Effective schema hash: $($script:hash)"
        )

        Get-EffectiveSchemaHashFromOutput -Output $output | Should -Be $script:hash
    }

    It "parses the json console log line" {
        $output = @(
            '{"Timestamp":"2026-06-29T22:00:00.0000000Z","Level":"Information","MessageTemplate":"Effective schema hash: {Hash}","RenderedMessage":"Effective schema hash: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef","Hash":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}'
        )

        Get-EffectiveSchemaHashFromOutput -Output $output | Should -Be $script:hash
    }

    It "parses the serilog json console log line with structured properties" {
        $output = @(
            '{"Timestamp":"2026-06-29T22:00:00.0000000Z","Level":"Information","MessageTemplate":"Effective schema hash: {Hash}","Properties":{"Hash":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}}'
        )

        Get-EffectiveSchemaHashFromOutput -Output $output | Should -Be $script:hash
    }

    It "parses uppercase structured hash properties" {
        $output = @(
            '{"Timestamp":"2026-06-29T22:00:00.0000000Z","Level":"Information","MessageTemplate":"Effective schema hash: {Hash}","Properties":{"Hash":"0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF"}}'
        )

        Get-EffectiveSchemaHashFromOutput -Output $output | Should -Be $script:hash
    }

    It "parses mixed output and returns the last hash line" {
        $otherHash = "abcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcdefabcd"
        $output = @(
            '{"Timestamp":"2026-06-29T22:00:00.0000000Z","Level":"Information","MessageTemplate":"Effective schema hash: {Hash}","Hash":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}'
            "Effective schema hash: $otherHash"
        )

        Get-EffectiveSchemaHashFromOutput -Output $output | Should -Be $otherHash
    }

    It "returns null for empty output" {
        Get-EffectiveSchemaHashFromOutput -Output @() | Should -BeNullOrEmpty
    }

    It "ignores blank lines in the output" {
        $output = @(
            ""
            "   "
            "Effective schema hash: $($script:hash)"
        )

        Get-EffectiveSchemaHashFromOutput -Output $output | Should -Be $script:hash
    }

    It "ignores null values in json output" {
        $output = @(
            '{"Timestamp":"2026-06-29T22:00:00.0000000Z","Level":"Information","MessageTemplate":"Effective schema hash: {Hash}","Hash":null,"RenderedMessage":"Effective schema hash: 0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}'
        )

        Get-EffectiveSchemaHashFromOutput -Output $output | Should -Be $script:hash
    }

    It "selects effective schema hash when unrelated hash appears later" {
        $unrelatedHash = "fedcbafedcbafedcbafedcbafedcbafedcbafedcbafedcbafedcbafedcbafedcba"
        $output = @(
            '{"Timestamp":"2026-06-29T22:00:00.0000000Z","Level":"Information","MessageTemplate":"Effective schema hash: {Hash}","Hash":"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"}'
            "{`"Timestamp`":`"2026-06-29T22:00:00.0000000Z`",`"Level`":`"Information`",`"MessageTemplate`":`"Resource key seeds: {SeedCount} entries, hash: {Hash}`",`"Hash`":`"$unrelatedHash`"}"
        )

        Get-EffectiveSchemaHashFromOutput -Output $output | Should -Be $script:hash
    }
}
