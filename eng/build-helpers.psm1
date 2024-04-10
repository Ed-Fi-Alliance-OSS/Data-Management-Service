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
    & $Command

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

#Output message using the Cyan text color
function Write-Command($message){
    Write-MessageColorOutput CYAN $message
}

#Output message using the Green text color
function Write-Success($message){
    Write-MessageColorOutput GREEN $message
}

#Output message using the Yellow text color
function Write-Information($message){
    Write-MessageColorOutput YELLOW $message
}

#Will add a new line
function Write-NewLine(){
    Write-MessageColorOutput WHITE "`n"
}

# Specify one of the following enumerator names
# Black, DarkBlue, DarkGreen, DarkCyan, DarkRed, DarkMagenta, DarkYellow, Gray, DarkGray, Blue, Green, Cyan, Red, Magenta, Yellow, White"
function Write-MessageColorOutput($ForegroundColor)
{
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
