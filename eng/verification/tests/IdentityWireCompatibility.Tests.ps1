# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# The identity wire-contract compatibility review: what the gate does when the contract version is
# greater than the last published one.
#
# The published baseline is the real committed golden. Every case derives the served document from
# it by a declarative list of edits, so each case reads as "the golden, with this one change". The
# edits never touch an example value (except the cases that are about examples), which makes the
# point of the hard-fail cases: a change that every existing example would still validate against
# is still not compatible, and no review record can say otherwise.
#
# The provider surface cases compile small assemblies in a child process, because two assemblies
# declaring the same type names cannot coexist in one session.

BeforeAll {
    $script:gate = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../Invoke-IdentityWireContractGate.ps1"))

    $realGolden = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot "../../../src/dms/core/EdFi.DataManagementService.Core.Tests.Unit/OpenApi/Fixtures/identity-v2-wire-baseline.json")
    )

    $script:golden = [System.IO.File]::ReadAllText($realGolden).Replace("`r`n", "`n")
    $script:baselinePath = "baseline/identity-v2-wire-baseline.json"
    $script:utf8 = [System.Text.UTF8Encoding]::new($false)

    function Invoke-TestGit {
        [CmdletBinding()]
        param([string] $Repository, [string[]] $Arguments)

        $output = & git -C $Repository -c user.name=test -c user.email=test@example.invalid -c commit.gpgsign=false -c core.autocrlf=false @Arguments 2>&1
        $exitCode = $LASTEXITCODE

        if ($exitCode -ne 0) {
            throw "git $($Arguments -join ' ') failed with $exitCode : $output"
        }

        return $output
    }

    # A repository whose second commit carries the golden as the published baseline.
    function Get-TestRepository {
        [CmdletBinding()]
        [OutputType([pscustomobject])]
        param()

        $root = Join-Path $TestDrive "repo-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path (Join-Path $root "baseline") -Force | Out-Null

        Invoke-TestGit -Repository $root -Arguments @("init", "--quiet") | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $root "README.md"), "x")
        Invoke-TestGit -Repository $root -Arguments @("add", "README.md") | Out-Null
        Invoke-TestGit -Repository $root -Arguments @("commit", "--quiet", "-m", "first") | Out-Null

        [System.IO.File]::WriteAllText((Join-Path $root $script:baselinePath), $script:golden, $script:utf8)
        Invoke-TestGit -Repository $root -Arguments @("add", $script:baselinePath) | Out-Null
        Invoke-TestGit -Repository $root -Arguments @("commit", "--quiet", "-m", "baseline") | Out-Null

        return [pscustomobject]@{ Root = $root; Commit = (Invoke-TestGit -Repository $root -Arguments @("rev-parse", "HEAD")).Trim() }
    }

    # A nupkg with a nuspec recording a commit and, when given, the contract assembly at the path
    # the packages ship it.
    function Get-TestPackage {
        [CmdletBinding()]
        [OutputType([string])]
        param([string] $Commit = "", [string] $AssemblyPath = "", [string] $Version = "1.0.0")

        $stage = Join-Path $TestDrive "stage-$([guid]::NewGuid().ToString('N'))"
        $package = Join-Path $TestDrive "EdFi.Api.Identity.$Version-$([guid]::NewGuid().ToString('N')).nupkg"
        New-Item -ItemType Directory -Path $stage -Force | Out-Null

        $repositoryXml = if ($Commit.Length -gt 0) { "<repository type=`"git`" url=`"https://example.invalid/repo.git`" commit=`"$Commit`" />" } else { "" }

        [System.IO.File]::WriteAllText(
            (Join-Path $stage "EdFi.Api.Identity.nuspec"),
            @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
    <metadata>
        <id>EdFi.Api.Identity</id>
        <version>$Version</version>
        <authors>Ed-Fi Alliance, LLC and contributors</authors>
        <description>Synthetic.</description>
        $repositoryXml
    </metadata>
</package>
"@
        )

        $items = @((Join-Path $stage "EdFi.Api.Identity.nuspec"))

        if ($AssemblyPath.Length -gt 0) {
            $libDirectory = Join-Path $stage "lib/net10.0"
            New-Item -ItemType Directory -Path $libDirectory -Force | Out-Null
            Copy-Item -LiteralPath $AssemblyPath -Destination (Join-Path $libDirectory "EdFi.DataManagementService.Identity.dll")
            $items += (Join-Path $stage "lib")
        }

        Compress-Archive -LiteralPath $items -DestinationPath $package

        return $package
    }

    # Applies declarative edits to the golden and returns the served document text. An edit is
    # @{ Path = @(...); Json = '<json>' } to set or add, or @{ Path = @(...); Remove = $true }.
    function Get-ServedDocument {
        [CmdletBinding()]
        [OutputType([string])]
        param([hashtable[]] $Edit = @())

        $document = [System.Text.Json.Nodes.JsonNode]::Parse($script:golden)

        foreach ($item in $Edit) {
            $parent = $document

            foreach ($segment in @($item.Path | Select-Object -SkipLast 1)) {
                # Explicit assignments: a JsonNode returned from an if expression would be enumerated.
                if ($parent -is [System.Text.Json.Nodes.JsonArray]) {
                    $parent = $parent[[int] $segment]
                }
                else {
                    $parent = $parent[$segment]
                }

                if ($null -eq $parent) {
                    throw "The edit path $($item.Path -join '/') does not exist in the golden at '$segment'."
                }
            }

            $last = $item.Path[-1]

            if ($parent -is [System.Text.Json.Nodes.JsonArray]) {
                $index = [int] $last

                if ($item.ContainsKey('Remove')) {
                    $parent.RemoveAt($index)
                }
                elseif ($index -ge $parent.Count) {
                    $parent.Add([System.Text.Json.Nodes.JsonNode]::Parse($item.Json))
                }
                else {
                    $parent[$index] = [System.Text.Json.Nodes.JsonNode]::Parse($item.Json)
                }
            }
            elseif ($item.ContainsKey('Remove')) {
                if (-not $parent.Remove($last)) {
                    throw "The edit path $($item.Path -join '/') does not exist in the golden."
                }
            }
            else {
                $parent[$last] = [System.Text.Json.Nodes.JsonNode]::Parse($item.Json)
            }
        }

        return $document.ToJsonString()
    }

    function Get-FakeFeed {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Read inside GetNewClosure bodies, and the closure parameters are the injected seam signature.')]
        [CmdletBinding()]
        [OutputType([hashtable])]
        param([string[]] $Versions, [string] $PublishedPackage)

        return @{
            ResolvePackageBaseAddress = {
                param([string] $IndexUrl, [string] $ApiKey)
                return "https://feed.invalid/flat2"
            }
            GetPublishedVersions      = {
                param([string] $BaseAddress, [string] $NormalizedId, [string] $ApiKey)
                return @{ Found = $true; Versions = $Versions }
            }.GetNewClosure()
            SavePublishedPackage      = {
                param([string] $BaseAddress, [string] $NormalizedId, [string] $NormalizedVersion, [string] $Destination, [string] $ApiKey)
                Copy-Item -LiteralPath $PublishedPackage -Destination $Destination
            }.GetNewClosure()
        }
    }

    # Runs the gate at contract version 1.1.0 against the published 1.0.0. $Record is an array of
    # @{ pointer; change; reason } written as the review record, or $null for no record file.
    function Invoke-IncrementGate {
        [CmdletBinding()]
        [OutputType([pscustomobject])]
        param(
            [string] $ServedText = $script:golden,
            [AllowNull()][object[]] $Record = $null,
            [string] $RawRecord = "",
            [string] $PublishedPackage = $script:basePackage,
            [string] $PackedPackage = $script:basePackage,
            [switch] $OmitPackedPackage
        )

        $served = Join-Path $TestDrive "served-$([guid]::NewGuid().ToString('N')).json"
        [System.IO.File]::WriteAllText($served, $ServedText, $script:utf8)

        $recordDirectory = Join-Path $TestDrive "records-$([guid]::NewGuid().ToString('N'))"
        New-Item -ItemType Directory -Path $recordDirectory -Force | Out-Null

        if ($RawRecord.Length -gt 0) {
            [System.IO.File]::WriteAllText((Join-Path $recordDirectory "1.0.0-to-1.1.0.json"), $RawRecord)
        }
        elseif ($null -ne $Record) {
            [System.IO.File]::WriteAllText(
                (Join-Path $recordDirectory "1.0.0-to-1.1.0.json"),
                (@{ from = "1.0.0"; to = "1.1.0"; reviewed = @($Record) } | ConvertTo-Json -Depth 5)
            )
        }

        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $PublishedPackage

        $arguments = @{
            ServedDocumentPath        = $served
            ContractVersion           = "1.1.0"
            RepositoryRoot            = $script:repo.Root
            BaselinePath              = $script:baselinePath
            ResolvePackageBaseAddress = $feed.ResolvePackageBaseAddress
            GetPublishedVersions      = $feed.GetPublishedVersions
            SavePublishedPackage      = $feed.SavePublishedPackage
            ReviewRecordDirectory     = $recordDirectory
        }

        if (-not $OmitPackedPackage) {
            $arguments.PackedPackageFile = $PackedPackage
        }

        $result = [pscustomobject]@{ Decision = $null; Message = "" }

        try {
            $result.Decision = & $script:gate @arguments 6>$null
        }
        catch {
            $result.Message = $_.Exception.Message
        }

        return $result
    }

    # Every "<change> <pointer>" the failure message lists in its hard-fail and needs-review
    # sections, as review record entries.
    function Get-ListedEntry {
        [CmdletBinding()]
        [OutputType([object[]])]
        param([string] $Message)

        $entries = @{}

        foreach ($line in $Message -split "`n") {
            $match = [regex]::Match($line.TrimEnd("`r"), '^  (?:\[[^\]]+\] )?(added|removed|changed) (.+)$')

            if ($match.Success) {
                $entries["$($match.Groups[1].Value)|$($match.Groups[2].Value)"] = @{ pointer = $match.Groups[2].Value; change = $match.Groups[1].Value; reason = "Reviewed by the test." }
            }
        }

        return @($entries.Values)
    }

    $script:repo = Get-TestRepository

    $buildScript = Join-Path $TestDrive "build-assemblies.ps1"
    $assemblyDirectory = Join-Path $TestDrive "assemblies"
    New-Item -ItemType Directory -Path $assemblyDirectory -Force | Out-Null

    [System.IO.File]::WriteAllText($buildScript, @'
param([string] $Directory)

$base = @"
namespace Test.Identity {
    public interface IIdentityService { string Get(string id); }
    public abstract class Base { public abstract void Run(); }
    public class Result { public string Name { get; set; } = ""; }
}
"@

$variants = [ordered]@{
    base             = $base
    interfacemember  = $base.Replace('string Get(string id);', 'string Get(string id); string Extra();')
    interfaceproperty = $base.Replace('string Get(string id);', 'string Get(string id); int Count { get; }')
    interfaceevent   = $base.Replace('string Get(string id);', 'string Get(string id); event System.EventHandler Changed;')
    defaultmember    = $base.Replace('string Get(string id);', 'string Get(string id); string Extra() => "";')
    defaultproperty  = $base.Replace('string Get(string id);', 'string Get(string id); int Count => 0;')
    requiredproperty = $base.Replace('public string Name { get; set; } = "";', 'public string Name { get; set; } = ""; public required string Mandatory { get; set; }')
    requiredfield    = $base.Replace('public string Name { get; set; } = "";', 'public string Name { get; set; } = ""; public required string Mandatory;')
    removedtype      = $base.Replace('public class Result { public string Name { get; set; } = ""; }', '')
    changedsignature = $base.Replace('string Get(string id);', 'string Get(string id, int count);')
    abstractmember   = $base.Replace('public abstract void Run();', 'public abstract void Run(); public abstract void Stop();')
    abstractproperty = $base.Replace('public abstract void Run();', 'public abstract void Run(); public abstract int Size { get; }')
    plainmember      = $base.Replace('public string Name { get; set; } = "";', 'public string Name { get; set; } = ""; public void Touch() { }')
}

foreach ($name in $variants.Keys) {
    Add-Type -TypeDefinition $variants[$name] -OutputAssembly (Join-Path $Directory "$name.dll") -OutputType Library
}
'@)

    & pwsh -NoProfile -File $buildScript -Directory $assemblyDirectory 2>&1 | Out-Null
    $buildExit = $LASTEXITCODE

    if ($buildExit -ne 0) {
        throw "Compiling the test assemblies failed with exit code $buildExit."
    }

    $script:packages = @{}

    foreach ($name in @("base", "interfacemember", "interfaceproperty", "interfaceevent", "defaultmember", "defaultproperty", "requiredproperty", "requiredfield", "removedtype", "changedsignature", "abstractmember", "abstractproperty", "plainmember")) {
        $script:packages[$name] = Get-TestPackage -Commit $script:repo.Commit -AssemblyPath (Join-Path $assemblyDirectory "$name.dll")
    }

    $script:basePackage = $script:packages["base"]
}

Describe "A version increment with nothing different" {
    It "reports increment-reviewed with the published version and anchor commit when the document and surface are identical" {
        $result = Invoke-IncrementGate

        $result.Message | Should -BeExactly ""
        $result.Decision.Outcome | Should -BeExactly "increment-reviewed"
        $result.Decision.PublishedVersion | Should -BeExactly "1.0.0"
        $result.Decision.AnchorCommit | Should -BeExactly $script:repo.Commit
    }

    It "ignores the contract version stamp the host adds" {
        $served = Get-ServedDocument -Edit @(@{ Path = @("x-edfi-identity-contract-version"); Json = '"1.1.0"' })

        (Invoke-IncrementGate -ServedText $served).Decision.Outcome | Should -BeExactly "increment-reviewed"
    }

    It "fails when the packed package is not supplied" {
        (Invoke-IncrementGate -OmitPackedPackage).Message | Should -BeLike "*requires -PackedPackageFile*"
    }
}

# Every category fails at an increment, even when a review record lists every difference.
# Every case leaves the examples alone, so every example still validates against the new schema.
Describe "Hard-fail wire differences at a version increment" {
    BeforeAll {
        $script:createSchema = @("components", "schemas", "IdentityCreateRequest")
        $script:responseSchema = @("components", "schemas", "IdentityResponse")
        $script:post = @("paths", "/identities", "post")
        $script:problem404 = @("components", "responses", "Identity404")
    }

    It "<Name> fails as <Category>, with and without a review record" -ForEach @(
        @{ Name = "narrower request acceptance (a new minLength)"; Category = "request-schema"; Edit = @(@{ Path = @("components", "schemas", "IdentityCreateRequest", "properties", "FirstName", "minLength"); Json = "3" }) }
        @{ Name = "wider request acceptance (a type constraint removed)"; Category = "request-schema"; Edit = @(@{ Path = @("components", "schemas", "IdentityCreateRequest", "properties", "FirstName", "type"); Remove = $true }) }
        @{ Name = "a new mandatory request field"; Category = "request-schema"; Edit = @(
                @{ Path = @("components", "schemas", "IdentityCreateRequest", "properties", "Nickname"); Json = '{"type":"string"}' }
                @{ Path = @("components", "schemas", "IdentityCreateRequest", "required"); Json = '["Nickname"]' }
            )
        }
        @{ Name = "a request additionalProperties tightening"; Category = "request-schema"; Edit = @(@{ Path = @("components", "schemas", "IdentityCreateRequest", "additionalProperties"); Json = "false" }) }
        @{ Name = "a new request media type"; Category = "request-media-type"; Edit = @(@{ Path = @("paths", "/identities", "post", "requestBody", "content", "application/xml"); Json = '{"schema":{"$ref":"#/components/schemas/IdentityCreateRequest"}}' }) }
        @{ Name = "a removed request media type"; Category = "request-media-type"; Edit = @(@{ Path = @("paths", "/identities", "post", "requestBody", "content", "text/json"); Remove = $true }) }
        @{ Name = "a request body made optional"; Category = "request-body"; Edit = @(@{ Path = @("paths", "/identities", "post", "requestBody", "required"); Json = "false" }) }
        @{ Name = "a changed request parameter"; Category = "request-parameter"; Edit = @(@{ Path = @("paths", "/identities/{id}", "get", "parameters", "0", "required"); Json = "false" }) }
        @{ Name = "a changed request parameter schema"; Category = "request-schema"; Edit = @(@{ Path = @("paths", "/identities/{id}", "get", "parameters", "0", "schema", "type"); Json = '"integer"' }) }
        @{ Name = "a new request parameter"; Category = "request-parameter"; Edit = @(@{ Path = @("paths", "/identities/{id}", "get", "parameters", "1"); Json = '{"in":"query","name":"verbose","schema":{"type":"boolean"}}' }) }
        @{ Name = "a widened response enum (a response header)"; Category = "response-schema"; Edit = @(@{ Path = @("components", "headers", "CacheControl", "schema", "enum", "1"); Json = '"no-cache"' }) }
        @{ Name = "a widened problem type enum"; Category = "response-schema"; Edit = @(@{ Path = @("components", "schemas", "IdentityNotFoundProblemDetails", "allOf", "1", "properties", "type", "enum", "1"); Json = '"urn:ed-fi:api:identities:other"' }) }
        @{ Name = "a narrowed response enum"; Category = "response-schema"; Edit = @(@{ Path = @("components", "headers", "CacheControl", "schema", "enum", "0"); Remove = $true }) }
        @{ Name = "a new response oneOf alternative"; Category = "response-schema"; Edit = @(@{ Path = @("components", "responses", "Identity404", "content", "application/problem+json", "schema", "oneOf", "3"); Json = '{"$ref":"#/components/schemas/ProblemDetails"}' }) }
        @{ Name = "a new response status code"; Category = "response-status-code"; Edit = @(@{ Path = @("paths", "/identities", "post", "responses", "409"); Json = '{"$ref":"#/components/responses/Identity400"}' }) }
        @{ Name = "a removed response status code"; Category = "response-status-code"; Edit = @(@{ Path = @("paths", "/identities", "post", "responses", "415"); Remove = $true }) }
        @{ Name = "a response status code pointed at a different response"; Category = "response-status-code"; Edit = @(@{ Path = @("paths", "/identities", "post", "responses", "415", '$ref'); Json = '"#/components/responses/Identity400"' }) }
        @{ Name = "a stricter response constraint (minLength)"; Category = "response-schema"; Edit = @(@{ Path = @("components", "schemas", "IdentityResponse", "properties", "UniqueId", "minLength"); Json = "2" }) }
        @{ Name = "a response pattern constraint"; Category = "response-schema"; Edit = @(@{ Path = @("components", "schemas", "IdentityResponse", "properties", "UniqueId", "pattern"); Json = '"^[0-9]+$"' }) }
        @{ Name = "a guaranteed response field no longer required"; Category = "response-schema"; Edit = @(@{ Path = @("components", "schemas", "IdentityResponse", "required", "9"); Remove = $true }) }
        @{ Name = "a new required response field"; Category = "response-schema"; Edit = @(
                @{ Path = @("components", "schemas", "IdentityResponse", "properties", "Nickname"); Json = '{"type":"string"}' }
                @{ Path = @("components", "schemas", "IdentityResponse", "required", "10"); Json = '"Nickname"' }
            )
        }
        @{ Name = "a response property type change"; Category = "response-schema"; Edit = @(@{ Path = @("components", "schemas", "IdentityResponse", "properties", "BirthOrder", "type"); Json = '"string"' }) }
        @{ Name = "a nullable change on a response property"; Category = "response-schema"; Edit = @(@{ Path = @("components", "schemas", "IdentityResponse", "properties", "FirstName", "nullable"); Json = "false" }) }
        @{ Name = "a new response media type"; Category = "response-media-type"; Edit = @(@{ Path = @("paths", "/identities", "post", "responses", "200", "content", "text/plain"); Json = '{"schema":{"type":"string"}}' }) }
        @{ Name = "a new response header"; Category = "response-header"; Edit = @(@{ Path = @("paths", "/identities", "post", "responses", "200", "headers", "Location"); Json = '{"$ref":"#/components/headers/Location"}' }) }
        @{ Name = "a removed response header"; Category = "response-header"; Edit = @(@{ Path = @("paths", "/identities", "post", "responses", "200", "headers", "Cache-Control"); Remove = $true }) }
        @{ Name = "a new problem type in a response example"; Category = "response-problem-type"; Edit = @(@{ Path = @("components", "responses", "Identity404", "content", "application/problem+json", "examples", "brandNew"); Json = '{"summary":"A new problem.","value":{"type":"urn:ed-fi:api:identities:brand-new"}}' }) }
        @{ Name = "a renamed problem type in a response example"; Category = "response-problem-type"; Edit = @(@{ Path = @("components", "responses", "Identity404", "content", "application/problem+json", "examples", "identityNotFound", "value", "type"); Json = '"urn:ed-fi:api:identities:renamed"' }) }
        @{ Name = "a new security scope"; Category = "security"; Edit = @(@{ Path = @("components", "securitySchemes", "oauth2_client_credentials", "flows", "clientCredentials", "scopes", "identities"); Json = '"Identity access"' }) }
        @{ Name = "a security scheme description change"; Category = "security"; Edit = @(@{ Path = @("components", "securitySchemes", "oauth2_client_credentials", "description"); Json = '"Other"' }) }
        @{ Name = "a flow rename"; Category = "security"; Edit = @(
                @{ Path = @("components", "securitySchemes", "oauth2_client_credentials", "flows", "clientCredentials"); Remove = $true }
                @{ Path = @("components", "securitySchemes", "oauth2_client_credentials", "flows", "password"); Json = '{"scopes":{},"tokenUrl":"{tokenUrl}"}' }
            )
        }
        @{ Name = "a changed security requirement"; Category = "security"; Edit = @(@{ Path = @("security", "0", "oauth2_client_credentials", "0"); Json = '"identities"' }) }
        @{ Name = "an operation security requirement"; Category = "security"; Edit = @(@{ Path = @("paths", "/identities", "post", "security"); Json = "[]" }) }
        @{ Name = "a removed operation"; Category = "removed-operation"; Edit = @(@{ Path = @("paths", "/identities/{id}", "get"); Remove = $true }) }
        @{ Name = "a removed path"; Category = "removed-path"; Edit = @(@{ Path = @("paths", "/identities/search"); Remove = $true }) }
    ) {
        $served = Get-ServedDocument -Edit $Edit
        $first = Invoke-IncrementGate -ServedText $served

        $first.Decision | Should -BeNullOrEmpty
        $first.Message | Should -BeLike "*Hard-fail differences*[[]$Category]*"

        # No difference is an example value, so every existing example still validates.
        if ($Category -ne "response-problem-type") {
            @(Get-ListedEntry -Message $first.Message | Where-Object { $_.pointer -match "/examples?(/|$)" }).Count | Should -Be 0
        }

        # A record naming every listed difference, hard ones included, does not help.
        $second = Invoke-IncrementGate -ServedText $served -Record (Get-ListedEntry -Message $first.Message)

        $second.Decision | Should -BeNullOrEmpty
        $second.Message | Should -BeLike "*Hard-fail differences*[[]$Category]*"
        $second.Message | Should -BeLike "*The review record names hard-fail differences, which it cannot waive*"
    }

    It "lists every hard-fail difference, then the uncovered review differences" {
        $served = Get-ServedDocument -Edit @(
            @{ Path = @("components", "schemas", "IdentityResponse", "properties", "UniqueId", "minLength"); Json = "2" }
            @{ Path = @("paths", "/identities", "post", "summary"); Json = '"Other"' }
        )

        $message = (Invoke-IncrementGate -ServedText $served).Message

        $message.IndexOf("Hard-fail differences", [System.StringComparison]::Ordinal) | Should -BeLessThan $message.IndexOf("Differences that need an entry", [System.StringComparison]::Ordinal)
        $message | Should -BeLike "*[[]response-schema] changed /components/schemas/IdentityResponse/properties/UniqueId/minLength*"
        $message | Should -BeLike "*[[]documentation] changed /paths/~1identities/post/summary*"
    }

    It "classifies a schema reached from both the request and the response side as hard" {
        $served = Get-ServedDocument -Edit @(
            @{ Path = @("paths", "/identities", "post", "requestBody", "content", "application/json", "schema", '$ref'); Json = '"#/components/schemas/IdentityResponse"' }
            @{ Path = @("components", "schemas", "IdentityResponse", "properties", "UniqueId", "maxLength"); Json = "10" }
        )

        (Invoke-IncrementGate -ServedText $served).Message | Should -BeLike "*[[]request+response-schema] added /components/schemas/IdentityResponse/properties/UniqueId/maxLength*"
    }
}

# Every needs-review difference requires an exact record entry, and an entry matching nothing fails.
Describe "Needs-review wire differences at a version increment" {
    It "<Name> fails without a record, passes with an exact record, and fails with a stale extra entry" -ForEach @(
        @{ Name = "a summary change"; Pointer = "/paths/~1identities/post/summary"; Change = "changed"; Edit = @(@{ Path = @("paths", "/identities", "post", "summary"); Json = '"Another summary"' }) }
        @{ Name = "an operationId change"; Pointer = "/paths/~1identities/post/operationId"; Change = "changed"; Edit = @(@{ Path = @("paths", "/identities", "post", "operationId"); Json = '"createIdentityV2"' }) }
        @{ Name = "a description added inside a request schema"; Pointer = "/components/schemas/IdentityCreateRequest/properties/FirstName/description"; Change = "added"; Edit = @(@{ Path = @("components", "schemas", "IdentityCreateRequest", "properties", "FirstName", "description"); Json = '"The first name."' }) }
        @{ Name = "a description added inside a response schema"; Pointer = "/components/schemas/IdentityResponse/properties/FirstName/description"; Change = "added"; Edit = @(@{ Path = @("components", "schemas", "IdentityResponse", "properties", "FirstName", "description"); Json = '"The first name."' }) }
        @{ Name = "a response description change"; Pointer = "/paths/~1identities/post/responses/200/description"; Change = "changed"; Edit = @(@{ Path = @("paths", "/identities", "post", "responses", "200", "description"); Json = '"Created."' }) }
        @{ Name = "a response example value change"; Pointer = "/paths/~1identities/post/responses/200/content/application~1json/example"; Change = "changed"; Edit = @(@{ Path = @("paths", "/identities", "post", "responses", "200", "content", "application/json", "example"); Json = '"999"' }) }
        @{ Name = "an example summary change keeping the problem type"; Pointer = "/components/responses/Identity404/content/application~1problem+json/examples/identityNotFound/summary"; Change = "changed"; Edit = @(@{ Path = @("components", "responses", "Identity404", "content", "application/problem+json", "examples", "identityNotFound", "summary"); Json = '"Other"' }) }
        @{ Name = "a header description change"; Pointer = "/components/headers/CacheControl/description"; Change = "changed"; Edit = @(@{ Path = @("components", "headers", "CacheControl", "description"); Json = '"Other"' }) }
        @{ Name = "a parameter description change"; Pointer = "/paths/~1identities~1{id}/get/parameters/0/description"; Change = "changed"; Edit = @(@{ Path = @("paths", "/identities/{id}", "get", "parameters", "0", "description"); Json = '"Other"' }) }
        @{ Name = "a new path"; Pointer = "/paths/~1identities~1ping"; Change = "added"; Edit = @(@{ Path = @("paths", "/identities/ping"); Json = '{"get":{"responses":{"200":{"description":"Alive."}}}}' }) }
        @{ Name = "a new operation on an existing path"; Pointer = "/paths/~1identities~1{id}/delete"; Change = "added"; Edit = @(@{ Path = @("paths", "/identities/{id}", "delete"); Json = '{"responses":{"204":{"description":"Deleted."}}}' }) }
        @{ Name = "an x- extension"; Pointer = "/x-edfi-note"; Change = "added"; Edit = @(@{ Path = @("x-edfi-note"); Json = '"hello"' }) }
        @{ Name = "an x- extension inside a schema"; Pointer = "/components/schemas/IdentityResponse/x-edfi-note"; Change = "added"; Edit = @(@{ Path = @("components", "schemas", "IdentityResponse", "x-edfi-note"); Json = '"hello"' }) }
        @{ Name = "a document info title change"; Pointer = "/info/title"; Change = "changed"; Edit = @(@{ Path = @("info", "title"); Json = '"Identity API"' }) }
        @{ Name = "a new unreferenced schema"; Pointer = "/components/schemas/Unused"; Change = "added"; Edit = @(@{ Path = @("components", "schemas", "Unused"); Json = '{"type":"string"}' }) }
    ) {
        $served = Get-ServedDocument -Edit $Edit
        $entry = @{ pointer = $Pointer; change = $Change; reason = "Reviewed by the test: documentation only." }

        $without = Invoke-IncrementGate -ServedText $served
        $without.Decision | Should -BeNullOrEmpty
        $without.Message | Should -BeLike "*Differences that need an entry in eng/verification/IdentityWireCompatibility/1.0.0-to-1.1.0.json*$Change $Pointer*"
        $without.Message | Should -Not -BeLike "*Hard-fail*"

        $exact = Invoke-IncrementGate -ServedText $served -Record @($entry)
        $exact.Message | Should -BeExactly ""
        $exact.Decision.Outcome | Should -BeExactly "increment-reviewed"

        $stale = Invoke-IncrementGate -ServedText $served -Record @($entry, @{ pointer = "/paths/~1identities/post/tags"; change = "added"; reason = "Not a real difference." })
        $stale.Decision | Should -BeNullOrEmpty
        $stale.Message | Should -BeLike "*Stale review record entries that match no difference:*added /paths/~1identities/post/tags*"
    }

    It "requires an exact change kind, not just a pointer" {
        $served = Get-ServedDocument -Edit @(@{ Path = @("paths", "/identities", "post", "summary"); Json = '"Another summary"' })

        $result = Invoke-IncrementGate -ServedText $served -Record @(@{ pointer = "/paths/~1identities/post/summary"; change = "added"; reason = "Wrong kind." })

        $result.Message | Should -BeLike "*changed /paths/~1identities/post/summary*"
        $result.Message | Should -BeLike "*Stale review record entries*added /paths/~1identities/post/summary*"
    }

    It "requires an exact pointer, not a parent pointer" {
        $served = Get-ServedDocument -Edit @(@{ Path = @("paths", "/identities", "post", "summary"); Json = '"Another summary"' })

        $result = Invoke-IncrementGate -ServedText $served -Record @(@{ pointer = "/paths/~1identities/post"; change = "changed"; reason = "A parent is not the difference." })

        $result.Decision | Should -BeNullOrEmpty
        $result.Message | Should -BeLike "*Stale review record entries*changed /paths/~1identities/post*"
    }

    It "fails a record with no differences at all as stale" {
        $result = Invoke-IncrementGate -Record @(@{ pointer = "/info/title"; change = "changed"; reason = "Nothing changed." })

        $result.Decision | Should -BeNullOrEmpty
        $result.Message | Should -BeLike "*Stale review record entries*changed /info/title*"
    }

    It "treats a schema reachable only from a new operation as review, not hard-fail" {
        $served = Get-ServedDocument -Edit @(
            @{ Path = @("components", "schemas", "PingRequest"); Json = '{"type":"object","required":["Name"],"properties":{"Name":{"type":"string"}}}' }
            @{ Path = @("paths", "/identities/ping"); Json = '{"post":{"requestBody":{"content":{"application/json":{"schema":{"$ref":"#/components/schemas/PingRequest"}}}},"responses":{"200":{"description":"Alive."}}}}' }
        )

        $without = Invoke-IncrementGate -ServedText $served
        $without.Message | Should -Not -BeLike "*Hard-fail*"

        $record = @(
            @{ pointer = "/paths/~1identities~1ping"; change = "added"; reason = "A new probe operation; existing operations are untouched." }
            @{ pointer = "/components/schemas/PingRequest"; change = "added"; reason = "Reachable only from the new probe operation." }
        )

        (Invoke-IncrementGate -ServedText $served -Record $record).Decision.Outcome | Should -BeExactly "increment-reviewed"
    }

    It "fails a record that does not parse, names the wrong versions, or has an entry without a reason" {
        $served = Get-ServedDocument -Edit @(@{ Path = @("paths", "/identities", "post", "summary"); Json = '"Another summary"' })

        (Invoke-IncrementGate -ServedText $served -RawRecord "{ not json").Message | Should -BeLike "*is not parseable JSON*"
        (Invoke-IncrementGate -ServedText $served -RawRecord '{"from":"1.0.0","to":"1.2.0","reviewed":[]}').Message | Should -BeLike "*must be an object with from '1.0.0' and to '1.1.0'*"
        (Invoke-IncrementGate -ServedText $served -RawRecord '{"from":"1.0.0","to":"1.1.0","reviewed":[{"pointer":"/info/title","change":"changed","reason":" "}]}').Message | Should -BeLike "*has no reason*"
        (Invoke-IncrementGate -ServedText $served -RawRecord '{"from":"1.0.0","to":"1.1.0","reviewed":[{"pointer":"/info/title","change":"modified","reason":"x"}]}').Message | Should -BeLike "*must be added, removed or changed*"
    }
}

Describe "Provider surface at a version increment" {
    It "<Name> fails as <Category>, with and without a review record" -ForEach @(
        @{ Name = "a new interface member"; Package = "interfacemember"; Category = "surface-interface-member" }
        @{ Name = "a new interface property"; Package = "interfaceproperty"; Category = "surface-interface-member" }
        @{ Name = "a new interface event"; Package = "interfaceevent"; Category = "surface-interface-member" }
        @{ Name = "a new required property"; Package = "requiredproperty"; Category = "surface-required-member" }
        @{ Name = "a new required field"; Package = "requiredfield"; Category = "surface-required-member" }
        @{ Name = "a new abstract member on an existing class"; Package = "abstractmember"; Category = "surface-abstract-member" }
        @{ Name = "a new abstract property on an existing class"; Package = "abstractproperty"; Category = "surface-abstract-member" }
        @{ Name = "a removed type"; Package = "removedtype"; Category = "surface-removed-or-changed" }
        @{ Name = "a changed signature"; Package = "changedsignature"; Category = "surface-removed-or-changed" }
    ) {
        $first = Invoke-IncrementGate -PackedPackage $script:packages[$Package]

        $first.Decision | Should -BeNullOrEmpty
        $first.Message | Should -BeLike "*Hard-fail differences*[[]$Category] *SURFACE:*"

        $second = Invoke-IncrementGate -PackedPackage $script:packages[$Package] -Record (Get-ListedEntry -Message $first.Message)

        $second.Decision | Should -BeNullOrEmpty
        $second.Message | Should -BeLike "*Hard-fail differences*[[]$Category] *SURFACE:*"
        $second.Message | Should -BeLike "*The review record names hard-fail differences, which it cannot waive*"
    }

    It "lists a changed signature as the removed line and the added line" {
        $message = (Invoke-IncrementGate -PackedPackage $script:packages["changedsignature"]).Message

        $message | Should -Match ([regex]::Escape('removed SURFACE:METHOD Test.Identity.IIdentityService.Get`0(System.String id nullable=[0]) : System.String'))
        $message | Should -Match ([regex]::Escape('added SURFACE:METHOD Test.Identity.IIdentityService.Get`0(System.String id nullable=[0], System.Int32 count nullable=[]) : System.String'))
    }

    It "needs a review record for <Name>, which an existing provider inherits" -ForEach @(
        @{ Name = "an interface method with a default implementation"; Package = "defaultmember"; Line = 'METHOD Test.Identity.IIdentityService.Extra`0() : System.String nullable=[0] accessibility=public modifiers=virtual generics=none' }
        @{ Name = "an interface property with a default implementation"; Package = "defaultproperty"; Line = 'PROPERTY Test.Identity.IIdentityService.Count : System.Int32 nullable=[] get=public:virtual set=none setkind=none' }
    ) {
        $without = Invoke-IncrementGate -PackedPackage $script:packages[$Package]
        $without.Decision | Should -BeNullOrEmpty
        $without.Message | Should -Match ([regex]::Escape("[surface-interface-default-member] added SURFACE:$Line"))
        $without.Message | Should -Not -BeLike "*Hard-fail*"

        $entry = @{ pointer = "SURFACE:$Line"; change = "added"; reason = "Existing providers inherit the default implementation." }

        (Invoke-IncrementGate -PackedPackage $script:packages[$Package] -Record @($entry)).Decision.Outcome | Should -BeExactly "increment-reviewed"
    }

    It "needs a review record for an added member that is none of the hard-fail kinds" {
        $line = 'METHOD Test.Identity.Result.Touch`0() : System.Void nullable=[] accessibility=public modifiers=none generics=none'

        $without = Invoke-IncrementGate -PackedPackage $script:packages["plainmember"]
        $without.Decision | Should -BeNullOrEmpty
        $without.Message | Should -BeLike "*Differences that need an entry*[[]surface-addition] added SURFACE:*"
        $without.Message | Should -Match ([regex]::Escape("added SURFACE:$line"))
        $without.Message | Should -Not -BeLike "*Hard-fail*"

        $entry = @{ pointer = "SURFACE:$line"; change = "added"; reason = "A concrete helper on a result type; existing providers need not change." }

        (Invoke-IncrementGate -PackedPackage $script:packages["plainmember"] -Record @($entry)).Decision.Outcome | Should -BeExactly "increment-reviewed"

        $stale = Invoke-IncrementGate -PackedPackage $script:packages["plainmember"] -Record @($entry, @{ pointer = 'SURFACE:METHOD Test.Identity.Result.Gone`0() : System.Void'; change = "added"; reason = "Not real." })
        $stale.Message | Should -BeLike "*Stale review record entries*SURFACE:METHOD Test.Identity.Result.Gone*"
    }

    It "compares the published surface, not the packed one, as the baseline" {
        # The published package carries the extra member, the packed one does not: a removal.
        $result = Invoke-IncrementGate -PublishedPackage $script:packages["interfacemember"] -PackedPackage $script:packages["base"]

        $result.Message | Should -BeLike "*[[]surface-removed-or-changed] removed SURFACE:METHOD Test.Identity.IIdentityService.Extra*"
    }

    It "fails when the packed package has no contract assembly" {
        $empty = Get-TestPackage -Commit $script:repo.Commit

        (Invoke-IncrementGate -PackedPackage $empty).Message | Should -BeLike "*carries 0 lib/net10.0/EdFi.DataManagementService.Identity.dll entries*"
    }

    It "fails when the published package has no contract assembly" {
        $empty = Get-TestPackage -Commit $script:repo.Commit

        (Invoke-IncrementGate -PublishedPackage $empty).Message | Should -BeLike "*published EdFi.Api.Identity 1.0.0 package carries 0*"
    }
}

Describe "The gate at the published version is unchanged by the compatibility review" {
    It "still fails a changed document at the same version, without consulting a record" {
        $served = Get-ServedDocument -Edit @(@{ Path = @("paths", "/identities", "post", "summary"); Json = '"Another summary"' })

        $served2 = Join-Path $TestDrive "same-version-$([guid]::NewGuid().ToString('N')).json"
        [System.IO.File]::WriteAllText($served2, $served, $script:utf8)
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $script:basePackage

        { & $script:gate -ServedDocumentPath $served2 -ContractVersion "1.0.0" -RepositoryRoot $script:repo.Root -BaselinePath $script:baselinePath -ResolvePackageBaseAddress $feed.ResolvePackageBaseAddress -GetPublishedVersions $feed.GetPublishedVersions -SavePublishedPackage $feed.SavePublishedPackage } |
            Should -Throw -ExpectedMessage "*differs from the baseline published with EdFi.Api.Identity 1.0.0*"
    }

    It "still reports unchanged for an identical document, without a packed package" {
        $served2 = Join-Path $TestDrive "same-version-$([guid]::NewGuid().ToString('N')).json"
        [System.IO.File]::WriteAllText($served2, $script:golden, $script:utf8)
        $feed = Get-FakeFeed -Versions @("1.0.0") -PublishedPackage $script:basePackage

        $decision = & $script:gate -ServedDocumentPath $served2 -ContractVersion "1.0.0" -RepositoryRoot $script:repo.Root -BaselinePath $script:baselinePath -ResolvePackageBaseAddress $feed.ResolvePackageBaseAddress -GetPublishedVersions $feed.GetPublishedVersions -SavePublishedPackage $feed.SavePublishedPackage 6>$null

        $decision.Outcome | Should -BeExactly "unchanged"
    }
}
