# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# The publish check's real feed implementations, with only the HTTP cmdlets replaced.
#
# The sibling suite injects the three seams and so never runs the code that talks to a feed. That
# leaves the branch that matters most untested: the one that decides whether a response means "this
# id has never been published" or "this feed cannot be trusted". A malformed 200 reaching that
# branch is a push of a package whose contents were never compared, so these cases run the defaults
# themselves and mock Invoke-RestMethod and Invoke-WebRequest underneath them.
#
# No network, no credential, and no local server: the mocks answer from a table.

BeforeAll {
    $script:checker = [System.IO.Path]::GetFullPath(
        (Join-Path $PSScriptRoot "../Invoke-ContractPublishCheck.ps1")
    )

    $script:packageId = "EdFi.Api.TestContract"
    $script:serviceIndexUrl = "https://feed.invalid/index.json"
    $script:baseAddress = "https://feed.invalid/flat2"

    $script:fixtureRoot = Join-Path ([System.IO.Path]::GetTempPath()) "dms1501-feed-defaults-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $script:fixtureRoot -Force | Out-Null

    # A real .nupkg, so that a case reaching the comparison compares something rather than failing
    # earlier for an unrelated reason.
    function New-MinimalPackage {
        [CmdletBinding(SupportsShouldProcess)]
        [OutputType([string])]
        param()

        $stage = Join-Path $script:fixtureRoot "stage-$([guid]::NewGuid().ToString('N'))"
        $package = Join-Path $script:fixtureRoot "$($script:packageId).1.0.0-$([guid]::NewGuid().ToString('N')).nupkg"

        if (-not $PSCmdlet.ShouldProcess($package, "Build package")) {
            return $null
        }

        $libraryFolder = Join-Path $stage "lib/net10.0"
        New-Item -ItemType Directory -Path $libraryFolder -Force | Out-Null

        Add-Type -OutputAssembly (Join-Path $libraryFolder "$($script:packageId).dll") -OutputType Library -TypeDefinition @"
#nullable enable
namespace EdFi.Api.TestContract
{
    public class Contract
    {
        public void Apply(int value) { }
    }
}
"@

        [System.IO.File]::WriteAllText((Join-Path $libraryFolder "$($script:packageId).xml"), @"
<?xml version="1.0"?>
<doc>
    <assembly><name>$($script:packageId)</name></assembly>
    <members>
        <member name="M:EdFi.Api.TestContract.Contract.Apply(System.Int32)"><summary>Applies.</summary></member>
    </members>
</doc>
"@)

        [System.IO.File]::WriteAllText((Join-Path $stage "$($script:packageId).nuspec"), @"
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
    <metadata>
        <id>$($script:packageId)</id>
        <version>1.0.0</version>
        <authors>Ed-Fi Alliance, LLC and contributors</authors>
        <description>A contract used by the feed-default fixtures.</description>
    </metadata>
</package>
"@)

        $entries = @(Get-ChildItem -LiteralPath $stage -Force | ForEach-Object { $_.FullName })
        Compress-Archive -LiteralPath $entries -DestinationPath $package

        return $package
    }

    # The exception shape Invoke-RestMethod raises for an HTTP status, so the default's 404 branch is
    # reached the way the real cmdlet reaches it rather than through a bare throw.
    function Get-HttpError {
        [CmdletBinding()]
        [OutputType([System.Management.Automation.ErrorRecord])]
        param([Parameter(Mandatory)][int] $StatusCode)

        $response = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode] $StatusCode)
        $exception = [Microsoft.PowerShell.Commands.HttpResponseException]::new(
            "Response status code does not indicate success: $StatusCode.",
            $response
        )

        return [System.Management.Automation.ErrorRecord]::new(
            $exception,
            "WebCmdletWebResponseException",
            [System.Management.Automation.ErrorCategory]::InvalidOperation,
            $null
        )
    }

    # A function rather than a variable. A mock body runs in the scope of the script that called the
    # mocked command, so $script: variables belonging to this test file are not visible inside it;
    # functions defined here are.
    function Get-HealthyServiceIndex {
        [CmdletBinding()]
        [OutputType([pscustomobject])]
        param()

        return [pscustomobject]@{
            resources = @(
                [pscustomobject]@{ '@id' = "https://feed.invalid/search"; '@type' = "SearchQueryService/3.0.0" },
                # The trailing slash is deliberate: a real service index advertises one, and the
                # composed package-index URL must not double it.
                [pscustomobject]@{ '@id' = "https://feed.invalid/flat2/"; '@type' = "PackageBaseAddress/3.0.0" }
            )
        }
    }

    function Get-ServiceIndexUrl {
        [CmdletBinding()]
        [OutputType([string])]
        param()

        return "https://feed.invalid/index.json"
    }

    # The package the feed serves, written to one path so a mock can find it without a closure.
    function Set-PublishedFixture {
        [CmdletBinding(SupportsShouldProcess)]
        param([Parameter(Mandatory)][string] $Path)

        if ($PSCmdlet.ShouldProcess($Path, "Publish fixture")) {
            Copy-Item -LiteralPath $Path -Destination (Get-PublishedFixturePath) -Force
        }
    }

    function Get-PublishedFixturePath {
        [CmdletBinding()]
        [OutputType([string])]
        param()

        return Join-Path ([System.IO.Path]::GetTempPath()) "dms1501-feed-defaults-published.nupkg"
    }

    function Invoke-DefaultCheck {
        [CmdletBinding()]
        param([Parameter(Mandatory)][string] $PackageFile)

        # Only PackageFile, PackageId, PackageVersion, WorkingDirectory and the service index are
        # passed: the three feed seams are left unset so the script's own defaults run.
        return & $script:checker `
            -PackageFile $PackageFile `
            -PackageId $script:packageId `
            -PackageVersion "1.0.0" `
            -WorkingDirectory (Join-Path $script:fixtureRoot "work-$([guid]::NewGuid().ToString('N'))") `
            -ServiceIndexUrl (Get-ServiceIndexUrl)
    }
}

AfterAll {
    if ($script:fixtureRoot -and (Test-Path -LiteralPath $script:fixtureRoot)) {
        Remove-Item -LiteralPath $script:fixtureRoot -Recurse -Force
    }
}

Describe "The default feed implementations" {
    It "reads a healthy service index and a 404 package index as an absent package" {
        Mock Invoke-RestMethod {
            if ($Uri -eq (Get-ServiceIndexUrl)) {
                return Get-HealthyServiceIndex
            }

            throw (Get-HttpError -StatusCode 404)
        }

        $result = Invoke-DefaultCheck -PackageFile (New-MinimalPackage)

        $result.ShouldPush | Should -BeTrue
        $result.Reason | Should -BeExactly "absent"
    }

    # The defect this suite exists for: a 200 that is not a versions index was filtered down to an
    # empty version list, which reads as absence, which is a push.
    It "fails on a 200 package index that carries no versions array" {
        Mock Invoke-RestMethod {
            if ($Uri -eq (Get-ServiceIndexUrl)) {
                return Get-HealthyServiceIndex
            }

            return [pscustomobject]@{ error = "not a versions index" }
        }

        { Invoke-DefaultCheck -PackageFile (New-MinimalPackage) } |
            Should -Throw -ExpectedMessage "*malformed response*"
    }

    It "fails when the service index answers 404" {
        Mock Invoke-RestMethod { throw (Get-HttpError -StatusCode 404) }

        { Invoke-DefaultCheck -PackageFile (New-MinimalPackage) } |
            Should -Throw -ExpectedMessage "*service index*"
    }

    It "fails when the service index answers 401" {
        Mock Invoke-RestMethod { throw (Get-HttpError -StatusCode 401) }

        { Invoke-DefaultCheck -PackageFile (New-MinimalPackage) } |
            Should -Throw -ExpectedMessage "*not an empty feed*"
    }

    It "fails when the service index times out" {
        Mock Invoke-RestMethod { throw [System.TimeoutException]::new("The operation has timed out.") }

        { Invoke-DefaultCheck -PackageFile (New-MinimalPackage) } |
            Should -Throw -ExpectedMessage "*service index*"
    }

    It "fails when the service index advertises no package base address" {
        Mock Invoke-RestMethod {
            return [pscustomobject]@{
                resources = @([pscustomobject]@{ '@id' = "https://feed.invalid/search"; '@type' = "SearchQueryService/3.0.0" })
            }
        }

        { Invoke-DefaultCheck -PackageFile (New-MinimalPackage) } |
            Should -Throw -ExpectedMessage "*no PackageBaseAddress*"
    }

    It "fails when the advertised package base address is not absolute" {
        Mock Invoke-RestMethod {
            return [pscustomobject]@{
                resources = @([pscustomobject]@{ '@id' = "flat2/"; '@type' = "PackageBaseAddress/3.0.0" })
            }
        }

        { Invoke-DefaultCheck -PackageFile (New-MinimalPackage) } |
            Should -Throw -ExpectedMessage "*not an absolute http or https address*"
    }

    It "fails on a package index status other than 200 or 404" {
        Mock Invoke-RestMethod {
            if ($Uri -eq (Get-ServiceIndexUrl)) {
                return Get-HealthyServiceIndex
            }

            throw (Get-HttpError -StatusCode 503)
        }

        { Invoke-DefaultCheck -PackageFile (New-MinimalPackage) } |
            Should -Throw -ExpectedMessage "*503*"
    }

    It "fails on a package-index failure that carries no status at all" {
        Mock Invoke-RestMethod {
            if ($Uri -eq (Get-ServiceIndexUrl)) {
                return Get-HealthyServiceIndex
            }

            throw [System.Net.Http.HttpRequestException]::new("No such host is known.")
        }

        { Invoke-DefaultCheck -PackageFile (New-MinimalPackage) } |
            Should -Throw -ExpectedMessage "*not an absent package*"
    }

    It "fails when the download answers 404 after the version was listed" {
        Mock Invoke-RestMethod {
            if ($Uri -eq (Get-ServiceIndexUrl)) {
                return Get-HealthyServiceIndex
            }

            return [pscustomobject]@{ versions = @("1.0.0") }
        }
        Mock Invoke-WebRequest { throw (Get-HttpError -StatusCode 404) }

        { Invoke-DefaultCheck -PackageFile (New-MinimalPackage) } |
            Should -Throw -ExpectedMessage "*feed fault rather than an absent package*"
    }

    It "composes the flat-container URLs NuGet defines" {
        Mock Invoke-RestMethod {
            if ($Uri -eq (Get-ServiceIndexUrl)) {
                return Get-HealthyServiceIndex
            }

            return [pscustomobject]@{ versions = @("1.0.0") }
        }
        Mock Invoke-WebRequest { throw (Get-HttpError -StatusCode 500) }

        { Invoke-DefaultCheck -PackageFile (New-MinimalPackage) } | Should -Throw

        # Lowercase id, lowercase normalized version, and the trailing slash on the advertised base
        # address collapsed rather than doubled.
        Should -Invoke Invoke-RestMethod -Times 1 -Exactly -ParameterFilter {
            $Uri -ceq "https://feed.invalid/flat2/edfi.api.testcontract/index.json"
        }
        Should -Invoke Invoke-WebRequest -Times 1 -Exactly -ParameterFilter {
            $Uri -ceq "https://feed.invalid/flat2/edfi.api.testcontract/1.0.0/edfi.api.testcontract.1.0.0.nupkg"
        }
    }

    It "compares rather than pushes when the feed serves the published package" {
        Set-PublishedFixture -Path (New-MinimalPackage)
        $packed = New-MinimalPackage

        Mock Invoke-RestMethod {
            if ($Uri -eq (Get-ServiceIndexUrl)) {
                return Get-HealthyServiceIndex
            }

            return [pscustomobject]@{ versions = @("1.0.0") }
        }
        Mock Invoke-WebRequest {
            Copy-Item -LiteralPath (Get-PublishedFixturePath) -Destination $OutFile
        }

        $result = Invoke-DefaultCheck -PackageFile $packed

        $result.ShouldPush | Should -BeFalse
        $result.Reason | Should -BeExactly "unchanged"
    }
}
