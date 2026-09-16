# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

#Requires -Version 7

# Promotion, at release time and in the prerelease lane.
#
# A contract's version deliberately does not move with the release, which is what makes both of
# these different from the three release-stamped packages beside them. At release time the version
# to promote is read from the contract's own source rather than from the release tag, and the
# question "has this version already been promoted" cannot be answered by absence from the
# prerelease view, because a version that was never published is absent from there too.
#
# Every case runs against injected lookups and an injected promotion, so nothing here mutates a
# feed or needs a credential. The default lookup's own failure branches are exercised separately at
# the end, with only the HTTP cmdlet mocked.

BeforeAll {
    $script:repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../.."))

    Import-Module (Join-Path $script:repositoryRoot "package-helpers.psm1") -Force

    $script:serviceIndexUrl = "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi/nuget/v3/index.json"
    $script:packageName = "EdFi.Api.Plugins"
    # A throwaway value that never leaves the process and is never sent anywhere: every promotion in
    # this file is injected, so nothing authenticates. The parameter is a SecureString because the
    # production function takes one.
    function Get-FakePassword {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSAvoidUsingConvertToSecureStringWithPlainText', '', Justification = 'A literal placeholder for an injected promotion that performs no request.')]
        [CmdletBinding()]
        [OutputType([securestring])]
        param()

        return ConvertTo-SecureString -String "not-a-real-token" -AsPlainText -Force
    }

    $script:password = Get-FakePassword

    # A feed whose views hold the versions a case names. The lookup is keyed on the view suffix the
    # caller scoped its URL to, which is also what proves the caller asked the view it meant.
    function Get-ViewLookup {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'Read inside the closure, and the closure parameters are the injected seam signature.')]
        [CmdletBinding()]
        [OutputType([scriptblock])]
        param(
            [string[]] $ReleaseVersions = @(),
            [string[]] $PrereleaseVersions = @(),

            # Views the feed refuses to answer for, by name.
            [string[]] $FailingViews = @()
        )

        return {
            param([string] $ViewIndexUrl, [string] $PackageName, [string] $ApiKey)

            foreach ($view in $FailingViews) {
                if ($ViewIndexUrl -like "*@$view/*") {
                    throw "the feed answered 401 for the $view view"
                }
            }

            if ($ViewIndexUrl -like "*@Release/*") {
                if ($ReleaseVersions.Count -eq 0) {
                    return @{ Found = $false; Versions = @() }
                }

                return @{ Found = $true; Versions = $ReleaseVersions }
            }

            if ($ViewIndexUrl -like "*@Prerelease/*") {
                if ($PrereleaseVersions.Count -eq 0) {
                    return @{ Found = $false; Versions = @() }
                }

                return @{ Found = $true; Versions = $PrereleaseVersions }
            }

            throw "unexpected view index URL: $ViewIndexUrl"
        }.GetNewClosure()
    }

    # A prerelease view that is empty until something is promoted into it, which is what makes the
    # second run of an idempotent promotion see the result of the first.
    function Get-GrowingViewLookup {
        [Diagnostics.CodeAnalysis.SuppressMessageAttribute('PSReviewUnusedParameter', '', Justification = 'The closure parameters are the injected seam signature, whether or not this fake reads each one.')]
        [CmdletBinding()]
        [OutputType([scriptblock])]
        param(
            # AllowEmptyCollection because an empty list is the starting state this fake exists to
            # model: nothing has been promoted yet.
            [Parameter(Mandatory)]
            [AllowEmptyCollection()]
            [System.Collections.Generic.List[string]]
            $Promoted
        )

        return {
            param([string] $ViewIndexUrl, [string] $PackageName, [string] $ApiKey)

            if ($ViewIndexUrl -like "*@Release/*") {
                return @{ Found = $false; Versions = @() }
            }

            if ($Promoted.Count -eq 0) {
                return @{ Found = $false; Versions = @() }
            }

            return @{ Found = $true; Versions = @("1.0.0") }
        }.GetNewClosure()
    }

    # Records what a promotion was asked to do, so a case can assert the view and version rather
    # than only that something happened.
    function Get-PromotionRecorder {
        [CmdletBinding()]
        [OutputType([hashtable])]
        param()

        $calls = [System.Collections.Generic.List[hashtable]]::new()

        return @{
            Calls  = $calls
            Script = {
                param($Arguments)

                $calls.Add($Arguments)
            }.GetNewClosure()
        }
    }
}

Describe "Invoke-Promote" {
    # The three release-stamped callers pass no -Version and must keep deriving from the tag.
    It "derives the version from the release ref when no explicit version is given" {
        Mock -ModuleName package-helpers Invoke-WebRequest { return [pscustomobject]@{ StatusCode = 200 } }

        Invoke-Promote `
            -PackagesURL "https://packages.invalid" `
            -Username "user" `
            -Password $script:password `
            -ViewId "release" `
            -ReleaseRef "v1.2.3" `
            -PackageName "EdFi.Api" | Out-Null

        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 1 -Exactly -ParameterFilter {
            ($Body | ConvertFrom-Json).packages[0].version -ceq "1.2.3"
        }
    }

    It "uses an explicit version instead of the release ref when one is given" {
        Mock -ModuleName package-helpers Invoke-WebRequest { return [pscustomobject]@{ StatusCode = 200 } }

        Invoke-Promote `
            -PackagesURL "https://packages.invalid" `
            -Username "user" `
            -Password $script:password `
            -ViewId "release" `
            -ReleaseRef "v8.1.0" `
            -PackageName "EdFi.Api.Plugins" `
            -Version "1.0.0" | Out-Null

        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 1 -Exactly -ParameterFilter {
            ($Body | ConvertFrom-Json).packages[0].version -ceq "1.0.0"
        }
    }

    It "still derives when an empty version is passed" {
        Mock -ModuleName package-helpers Invoke-WebRequest { return [pscustomobject]@{ StatusCode = 200 } }

        Invoke-Promote `
            -PackagesURL "https://packages.invalid" `
            -Username "user" `
            -Password $script:password `
            -ViewId "release" `
            -ReleaseRef "v2.0.0" `
            -PackageName "EdFi.Api" `
            -Version "" | Out-Null

        Should -Invoke -ModuleName package-helpers Invoke-WebRequest -Times 1 -Exactly -ParameterFilter {
            ($Body | ConvertFrom-Json).packages[0].version -ceq "2.0.0"
        }
    }
}

Describe "Get-ViewScopedServiceIndexUrl" {
    It "suffixes the feed name with the view" {
        Get-ViewScopedServiceIndexUrl -ServiceIndexUrl $script:serviceIndexUrl -ViewName "Release" |
            Should -BeExactly "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi@Release/nuget/v3/index.json"
    }

    It "scopes to the prerelease view the same way" {
        Get-ViewScopedServiceIndexUrl -ServiceIndexUrl $script:serviceIndexUrl -ViewName "Prerelease" |
            Should -BeExactly "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi@Prerelease/nuget/v3/index.json"
    }

    # Returning the URL unchanged would ask the unscoped feed, which answers a different question:
    # "is this version on the feed at all" rather than "is it in this view".
    It "refuses a URL with no packaging segment rather than returning it unscoped" {
        { Get-ViewScopedServiceIndexUrl -ServiceIndexUrl "https://nuget.org/v3/index.json" -ViewName "Release" } |
            Should -Throw -ExpectedMessage "*no /_packaging/<feed>/ segment*"
    }

    It "refuses an empty URL" {
        { Get-ViewScopedServiceIndexUrl -ServiceIndexUrl "" -ViewName "Release" } | Should -Throw
    }
}

Describe "Invoke-ContractPromotion at release time" {
    It "does nothing when the version is already in the release view" {
        $recorder = Get-PromotionRecorder

        $result = Invoke-ContractPromotion `
            -PackagesURL "https://packages.invalid" `
            -Username "user" `
            -Password $script:password `
            -ServiceIndexUrl $script:serviceIndexUrl `
            -PackageName $script:packageName `
            -Version "1.0.0" `
            -GetViewVersions (Get-ViewLookup -ReleaseVersions @("1.0.0")) `
            -Promote $recorder.Script

        $result | Should -BeLike "*already in the Release view*"
        $recorder.Calls.Count | Should -Be 0
    }

    It "promotes when the version is in the prerelease view only" {
        $recorder = Get-PromotionRecorder

        $result = Invoke-ContractPromotion `
            -PackagesURL "https://packages.invalid" `
            -Username "user" `
            -Password $script:password `
            -ServiceIndexUrl $script:serviceIndexUrl `
            -PackageName $script:packageName `
            -Version "1.0.0" `
            -GetViewVersions (Get-ViewLookup -PrereleaseVersions @("1.0.0")) `
            -Promote $recorder.Script

        $result | Should -BeLike "*promoted to the Release view*"
        $recorder.Calls.Count | Should -Be 1
        $recorder.Calls[0].ViewId | Should -BeExactly "release"
        $recorder.Calls[0].Version | Should -BeExactly "1.0.0"
        $recorder.Calls[0].PackageName | Should -BeExactly $script:packageName
    }

    # Without this branch a version that never reached the feed is indistinguishable from one
    # already promoted, and a missed publish would log "nothing to do" and exit zero.
    It "fails when the version is in neither view, naming the package and the version" {
        $recorder = Get-PromotionRecorder

        {
            Invoke-ContractPromotion `
                -PackagesURL "https://packages.invalid" `
                -Username "user" `
                -Password $script:password `
                -ServiceIndexUrl $script:serviceIndexUrl `
                -PackageName $script:packageName `
                -Version "1.0.0" `
                -GetViewVersions (Get-ViewLookup) `
                -Promote $recorder.Script
        } | Should -Throw -ExpectedMessage "*EdFi.Api.Plugins 1.0.0 is in neither*"

        $recorder.Calls.Count | Should -Be 0
    }

    It "does not promote a different version that happens to be in the prerelease view" {
        $recorder = Get-PromotionRecorder

        {
            Invoke-ContractPromotion `
                -PackagesURL "https://packages.invalid" `
                -Username "user" `
                -Password $script:password `
                -ServiceIndexUrl $script:serviceIndexUrl `
                -PackageName $script:packageName `
                -Version "1.1.0" `
                -GetViewVersions (Get-ViewLookup -PrereleaseVersions @("1.0.0")) `
                -Promote $recorder.Script
        } | Should -Throw

        $recorder.Calls.Count | Should -Be 0
    }

    It "matches a listed version that differs only in NuGet normalization" {
        $recorder = Get-PromotionRecorder

        Invoke-ContractPromotion `
            -PackagesURL "https://packages.invalid" `
            -Username "user" `
            -Password $script:password `
            -ServiceIndexUrl $script:serviceIndexUrl `
            -PackageName $script:packageName `
            -Version "1.0.0" `
            -GetViewVersions (Get-ViewLookup -ReleaseVersions @("1.0.0.0")) `
            -Promote $recorder.Script | Should -BeLike "*already in the Release view*"

        $recorder.Calls.Count | Should -Be 0
    }

    # A view that cannot be read is not a view without the package. Reading a failure as absence
    # would fail the release on the third branch, or promote on the second.
    It "fails rather than promoting when the release view cannot be read" {
        $recorder = Get-PromotionRecorder

        {
            Invoke-ContractPromotion `
                -PackagesURL "https://packages.invalid" `
                -Username "user" `
                -Password $script:password `
                -ServiceIndexUrl $script:serviceIndexUrl `
                -PackageName $script:packageName `
                -Version "1.0.0" `
                -GetViewVersions (Get-ViewLookup -PrereleaseVersions @("1.0.0") -FailingViews @("Release")) `
                -Promote $recorder.Script
        } | Should -Throw -ExpectedMessage "*401*"

        $recorder.Calls.Count | Should -Be 0
    }

    It "fails when the prerelease view cannot be read" {
        $recorder = Get-PromotionRecorder

        {
            Invoke-ContractPromotion `
                -PackagesURL "https://packages.invalid" `
                -Username "user" `
                -Password $script:password `
                -ServiceIndexUrl $script:serviceIndexUrl `
                -PackageName $script:packageName `
                -Version "1.0.0" `
                -GetViewVersions (Get-ViewLookup -FailingViews @("Prerelease")) `
                -Promote $recorder.Script
        } | Should -Throw -ExpectedMessage "*401*"

        $recorder.Calls.Count | Should -Be 0
    }
}

Describe "Invoke-ContractPrereleasePromotion in the prerelease lane" {
    # First publication: the push just happened, so nothing is in either view yet.
    It "promotes a version that is in neither view" {
        $recorder = Get-PromotionRecorder

        $result = Invoke-ContractPrereleasePromotion `
            -PackagesURL "https://packages.invalid" `
            -Username "user" `
            -Password $script:password `
            -ServiceIndexUrl $script:serviceIndexUrl `
            -PackageName $script:packageName `
            -Version "1.0.0" `
            -GetViewVersions (Get-ViewLookup) `
            -Promote $recorder.Script

        $result | Should -BeLike "*promoted to the Prerelease view*"
        $recorder.Calls.Count | Should -Be 1
        $recorder.Calls[0].ViewId | Should -BeExactly "prerelease"
        $recorder.Calls[0].Version | Should -BeExactly "1.0.0"
    }

    # The unchanged rerun: the package was already published, the publish check skipped cleanly, and
    # membership is missing because an earlier prerelease predates this step. It is repaired.
    It "repairs missing membership on an unchanged rerun" {
        $recorder = Get-PromotionRecorder

        Invoke-ContractPrereleasePromotion `
            -PackagesURL "https://packages.invalid" `
            -Username "user" `
            -Password $script:password `
            -ServiceIndexUrl $script:serviceIndexUrl `
            -PackageName $script:packageName `
            -Version "1.0.0" `
            -GetViewVersions (Get-ViewLookup) `
            -Promote $recorder.Script | Out-Null

        $recorder.Calls.Count | Should -Be 1
    }

    It "does nothing when the version is already in the prerelease view" {
        $recorder = Get-PromotionRecorder

        $result = Invoke-ContractPrereleasePromotion `
            -PackagesURL "https://packages.invalid" `
            -Username "user" `
            -Password $script:password `
            -ServiceIndexUrl $script:serviceIndexUrl `
            -PackageName $script:packageName `
            -Version "1.0.0" `
            -GetViewVersions (Get-ViewLookup -PrereleaseVersions @("1.0.0")) `
            -Promote $recorder.Script

        $result | Should -BeLike "*already in the Prerelease view*"
        $recorder.Calls.Count | Should -Be 0
    }

    # A released contract keeps its release-view membership. Putting it back into Prerelease is not
    # what this step is for, and doing it on every prerelease would churn the view.
    It "leaves a version that is already in the release view alone" {
        $recorder = Get-PromotionRecorder

        $result = Invoke-ContractPrereleasePromotion `
            -PackagesURL "https://packages.invalid" `
            -Username "user" `
            -Password $script:password `
            -ServiceIndexUrl $script:serviceIndexUrl `
            -PackageName $script:packageName `
            -Version "1.0.0" `
            -GetViewVersions (Get-ViewLookup -ReleaseVersions @("1.0.0")) `
            -Promote $recorder.Script

        $result | Should -BeLike "*already in the Release view*"
        $recorder.Calls.Count | Should -Be 0
    }

    It "fails rather than promoting when a view cannot be read" {
        $recorder = Get-PromotionRecorder

        {
            Invoke-ContractPrereleasePromotion `
                -PackagesURL "https://packages.invalid" `
                -Username "user" `
                -Password $script:password `
                -ServiceIndexUrl $script:serviceIndexUrl `
                -PackageName $script:packageName `
                -Version "1.0.0" `
                -GetViewVersions (Get-ViewLookup -FailingViews @("Prerelease")) `
                -Promote $recorder.Script
        } | Should -Throw

        $recorder.Calls.Count | Should -Be 0
    }

    It "is idempotent across two runs, promoting once" {
        $recorder = Get-PromotionRecorder
        $promoted = [System.Collections.Generic.List[string]]::new()

        # The view gains the version after the first promotion, which is what the second run sees.
        $lookup = Get-GrowingViewLookup -Promoted $promoted

        $promote = {
            param($Arguments)

            $promoted.Add($Arguments.Version)
            $recorder.Calls.Add($Arguments)
        }.GetNewClosure()

        foreach ($run in 1..2) {
            Invoke-ContractPrereleasePromotion `
                -PackagesURL "https://packages.invalid" `
                -Username "user" `
                -Password $script:password `
                -ServiceIndexUrl $script:serviceIndexUrl `
                -PackageName $script:packageName `
                -Version "1.0.0" `
                -GetViewVersions $lookup `
                -Promote $promote | Out-Null
        }

        $recorder.Calls.Count | Should -Be 1
    }
}

Describe "The default view lookup" {
    # The seam-injected cases above never run this code, and it is the code that decides whether a
    # feed response means "not in this view" or "do not trust this answer".
    It "reads a 404 package index in a view that answered as absence" {
        Mock -ModuleName package-helpers Invoke-RestMethod {
            if ($Uri -like "*index.json" -and $Uri -notlike "*flat2*") {
                return [pscustomobject]@{
                    resources = @([pscustomobject]@{ '@id' = "https://feed.invalid/flat2/"; '@type' = "PackageBaseAddress/3.0.0" })
                }
            }

            $response = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]::NotFound)
            throw [Microsoft.PowerShell.Commands.HttpResponseException]::new("404", $response)
        }

        Test-PackageInView `
            -ServiceIndexUrl $script:serviceIndexUrl `
            -ViewName "Release" `
            -PackageName $script:packageName `
            -Version "1.0.0" | Should -BeFalse
    }

    It "finds a listed version" {
        Mock -ModuleName package-helpers Invoke-RestMethod {
            if ($Uri -like "*index.json" -and $Uri -notlike "*flat2*") {
                return [pscustomobject]@{
                    resources = @([pscustomobject]@{ '@id' = "https://feed.invalid/flat2/"; '@type' = "PackageBaseAddress/3.0.0" })
                }
            }

            return [pscustomobject]@{ versions = @("0.9.0", "1.0.0") }
        }

        Test-PackageInView `
            -ServiceIndexUrl $script:serviceIndexUrl `
            -ViewName "Prerelease" `
            -PackageName $script:packageName `
            -Version "1.0.0" | Should -BeTrue
    }

    It "fails when the view's service index cannot be read" {
        Mock -ModuleName package-helpers Invoke-RestMethod { throw "the feed answered 401" }

        {
            Test-PackageInView `
                -ServiceIndexUrl $script:serviceIndexUrl `
                -ViewName "Release" `
                -PackageName $script:packageName `
                -Version "1.0.0"
        } | Should -Throw -ExpectedMessage "*is not a view without the package*"
    }

    It "fails on a package-index status other than 200 or 404" {
        Mock -ModuleName package-helpers Invoke-RestMethod {
            if ($Uri -like "*index.json" -and $Uri -notlike "*flat2*") {
                return [pscustomobject]@{
                    resources = @([pscustomobject]@{ '@id' = "https://feed.invalid/flat2/"; '@type' = "PackageBaseAddress/3.0.0" })
                }
            }

            $response = [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]::InternalServerError)
            throw [Microsoft.PowerShell.Commands.HttpResponseException]::new("500", $response)
        }

        {
            Test-PackageInView `
                -ServiceIndexUrl $script:serviceIndexUrl `
                -ViewName "Release" `
                -PackageName $script:packageName `
                -Version "1.0.0"
        } | Should -Throw -ExpectedMessage "*not an absent package*"
    }

    It "fails on a 200 that carries no versions array" {
        Mock -ModuleName package-helpers Invoke-RestMethod {
            if ($Uri -like "*index.json" -and $Uri -notlike "*flat2*") {
                return [pscustomobject]@{
                    resources = @([pscustomobject]@{ '@id' = "https://feed.invalid/flat2/"; '@type' = "PackageBaseAddress/3.0.0" })
                }
            }

            return [pscustomobject]@{ error = "not a versions index" }
        }

        {
            Test-PackageInView `
                -ServiceIndexUrl $script:serviceIndexUrl `
                -ViewName "Release" `
                -PackageName $script:packageName `
                -Version "1.0.0"
        } | Should -Throw -ExpectedMessage "*malformed response*"
    }

    It "fails when the view advertises no package base address" {
        Mock -ModuleName package-helpers Invoke-RestMethod {
            return [pscustomobject]@{ resources = @() }
        }

        {
            Test-PackageInView `
                -ServiceIndexUrl $script:serviceIndexUrl `
                -ViewName "Release" `
                -PackageName $script:packageName `
                -Version "1.0.0"
        } | Should -Throw -ExpectedMessage "*no PackageBaseAddress*"
    }

    It "asks the view-scoped index rather than the unscoped feed" {
        Mock -ModuleName package-helpers Invoke-RestMethod {
            if ($Uri -like "*index.json" -and $Uri -notlike "*flat2*") {
                return [pscustomobject]@{
                    resources = @([pscustomobject]@{ '@id' = "https://feed.invalid/flat2/"; '@type' = "PackageBaseAddress/3.0.0" })
                }
            }

            return [pscustomobject]@{ versions = @("1.0.0") }
        }

        Test-PackageInView `
            -ServiceIndexUrl $script:serviceIndexUrl `
            -ViewName "Release" `
            -PackageName $script:packageName `
            -Version "1.0.0" | Out-Null

        Should -Invoke -ModuleName package-helpers Invoke-RestMethod -Times 1 -Exactly -ParameterFilter {
            $Uri -ceq "https://pkgs.dev.azure.com/ed-fi-alliance/Ed-Fi-Alliance-OSS/_packaging/EdFi@Release/nuget/v3/index.json"
        }
    }
}
