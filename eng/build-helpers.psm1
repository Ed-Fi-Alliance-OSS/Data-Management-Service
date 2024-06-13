# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

function Invoke-RegenerateFile {
    param (
        [string]
        $Path,

        [string]
        $NewContent
    )

    $oldContent = Get-Content -Path $Path

    if ($new_content -ne $oldContent) {
        $relative_path = Resolve-Path -Relative $Path
        Write-Command "Generating $relative_path"
        [System.IO.File]::WriteAllText($Path, $NewContent, [System.Text.Encoding]::UTF8)
    }
}

function Invoke-Execute {
    param (
        [ScriptBlock]
        $Command
    )

    $global:lastexitcode = 0
    Invoke-Command -ScriptBlock $Command | Out-Host

    if ($lastexitcode -ne 0) {
        throw "Error executing command: $Command"
    }
}

function Invoke-Step {
    param (
        [ScriptBlock]
        $block
    )

    $command = $block.ToString().Trim()

    Write-NewLine
    Write-Command $command

    &$block
}

function Invoke-Main {
    param (
        [ScriptBlock]
        $MainBlock
    )

    try {
        &$MainBlock
        Write-NewLine
        Write-Success "Build Succeeded"
        exit 0
    } catch [Exception] {
        Write-NewLine
        Write-Error $_.Exception.Message
        Write-NewLine
        Write-Error "Build Failed"
        exit 1
    }
}

<#
    .DESCRIPTION
    Display a command and its arguments on the console
#>
function Write-Command($message){
    Write-MessageColorOutput CYAN $message
}

<#
    .DESCRIPTION
    Display a command and its arguments on the console
#>
function Write-Success($message){
    Write-MessageColorOutput GREEN $message
}

<#
    .DESCRIPTION
    Display a command and its arguments on the console
#>
function Write-Info($message){
    Write-MessageColorOutput YELLOW $message
}

<#
    .DESCRIPTION
    Add a new break line in the console
#>
function Write-NewLine(){
    Write-MessageColorOutput WHITE "`n"
}

<#
    .DESCRIPTION
    Writes a message to the output with a specified text color.
#>
function Write-MessageColorOutput
{
    param(
        [ValidateSet("Black","DarkBlue","DarkGreen","DarkCyan","DarkRed","DarkMagenta",
        "DarkYellow","Gray","DarkGray","Blue","Green","Cyan","Red","Magenta","Yellow","White",
        ErrorMessage="Please specify a valid color name from the list.",
        IgnoreCase=$true)]
        [String]
        $ForegroundColor
    )

    # save the current color
    $fc = $host.UI.RawUI.ForegroundColor

    # set the new color
    $host.UI.RawUI.ForegroundColor = $ForegroundColor

    # output
    if ($args) {
        Write-Output $args
    }
    else {
        $input | Write-Output
    }

    # restore the original color
    $host.UI.RawUI.ForegroundColor = $fc
}

Export-ModuleMember -Function *
