# Permanent unit suite for Invoke-NativeCommandWithInput, the bounded stdin-capable runner that
# carries the SQL Server physical-name authority's batch.
#
# The fourteen-item test contract is frozen in the stabilization specification. The behavioral
# half runs real child
# processes; every child is the ALREADY-RUNNING PowerShell, identified by
# [Environment]::ProcessPath - never a name that needs resolving, because command discovery is
# PATH-dependent (measured: it fails under a stripped PATH on both platforms) and raw
# Process.Start resolution survives only by the parent-executable-directory coincidence. A
# self-pin below asserts this file never names the executable literally.
#
# Timing assertions are deliberately non-brittle: each is a whole-call ceiling with a generous
# margin over the configured deadline, never an exact threshold. The ceilings are what kill the
# unbounded-stdin-write and deadline-reset mutants, so they cover the ENTIRE runner call
# including its cleanup.
#
# Structural pins cover what cannot be provoked portably (a genuinely unkillable child, a
# faulted read task, the kill-before-close ordering) and are labeled as structural in their
# names, per the spec's honest-reporting rule.

BeforeAll {
    Import-Module "$PSScriptRoot/../env-utility.psm1" -Force

    $script:moduleFilePath = (Resolve-Path "$PSScriptRoot/../env-utility.psm1").Path

    # The one way this suite launches a PowerShell child (see the self-pin test).
    function Script:Invoke-RunnerWithPowerShellChild {
        param(
            [Parameter(Mandatory)] [string]$ChildCommand,
            [string]$InputText = "",
            [int]$TimeoutSeconds = 30
        )
        return Invoke-NativeCommandWithInput `
            -FilePath ([Environment]::ProcessPath) `
            -ArgumentList @("-NoProfile", "-Command", $ChildCommand) `
            -InputText $InputText `
            -TimeoutSeconds $TimeoutSeconds
    }

    function Script:Measure-RunnerCall {
        param([Parameter(Mandatory)] [scriptblock]$Call)
        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $callResult = & $Call
        $stopwatch.Stop()
        return @{ Result = $callResult; ElapsedMs = $stopwatch.ElapsedMilliseconds }
    }

    # A payload guaranteed to exceed every stdin pipe buffer on both platforms (Linux pipes are
    # 64 KiB; Windows anonymous pipes are far smaller). 3 MiB of ASCII.
    $script:oversizedPayload = [string]::new([char]"x", 3MB)

    # The runner function's own source text, for the structural pins.
    $tokens = $null
    $parseErrors = $null
    $moduleAst = [System.Management.Automation.Language.Parser]::ParseFile(
        $script:moduleFilePath, [ref]$tokens, [ref]$parseErrors)
    $runnerAst = $moduleAst.Find({
            param($node)
            $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq "Invoke-NativeCommandWithInput"
        }, $true)
    $script:runnerText = if ($null -ne $runnerAst) { $runnerAst.Extent.Text } else { "" }
}

Describe "Invoke-NativeCommandWithInput behavioral contract" {

    It "delivers stdin text completely and terminates it with EOF (contract test 1)" {
        # The child can only report the length AFTER EOF, so a runner that never closes stdin
        # (mutant M-R12c) hangs this child into a timeout instead of completing.
        $result = Invoke-RunnerWithPowerShellChild `
            -ChildCommand '$t = [Console]::In.ReadToEnd(); Write-Output ("LEN:" + $t.Length)' `
            -InputText ([string]::new([char]"a", 10000)) `
            -TimeoutSeconds 30
        $result.Started | Should -BeTrue
        $result.TimedOut | Should -BeFalse
        $result.StdinCompleted | Should -BeTrue
        $result.ExitCode | Should -Be 0
        $result.StandardOutput | Should -Match "LEN:10000"
        $result.FailureKind | Should -Be "None"
    }

    It "keeps one deadline effective for an oversized payload a child never reads (contract tests 2 and 10)" {
        # The measured revision-5 defect: a synchronous (or unbounded-awaited) write blocks for
        # the child's entire lifetime BEFORE any exit timeout begins. The whole-call ceiling -
        # deadline x3 plus fixed slack - therefore kills mutant M-R12a, and because it covers
        # cleanup too, it also catches a timeout path that hangs in close or drain.
        $timeoutSeconds = 3
        $measured = Measure-RunnerCall -Call {
            Invoke-RunnerWithPowerShellChild `
                -ChildCommand "Start-Sleep -Seconds 60" `
                -InputText $script:oversizedPayload `
                -TimeoutSeconds $timeoutSeconds
        }
        $measured.Result.TimedOut | Should -BeTrue
        $measured.Result.StdinCompleted | Should -BeFalse
        $measured.Result.ExitCode | Should -BeNullOrEmpty
        $measured.ElapsedMs | Should -BeLessThan (($timeoutSeconds * 3000) + 10000)
    }

    It "does not restart the deadline between the stdin write and the exit wait (contract test 10, mutant M-R12b)" {
        # Two-stage shape: the child drains stdin only after 5 seconds (so the oversized write
        # genuinely pends that long), then never exits. With ONE deadline of 8 seconds the whole
        # call ends around the deadline plus cleanup; a runner that grants the exit wait a fresh
        # 8 seconds after the 5-second write phase lands well past the 12.5-second ceiling.
        $measured = Measure-RunnerCall -Call {
            Invoke-RunnerWithPowerShellChild `
                -ChildCommand 'Start-Sleep -Seconds 5; $null = [Console]::In.ReadToEnd(); Start-Sleep -Seconds 120' `
                -InputText $script:oversizedPayload `
                -TimeoutSeconds 8
        }
        $measured.Result.TimedOut | Should -BeTrue
        $measured.ElapsedMs | Should -BeLessThan 12500
    }

    It "kills the complete process tree on timeout, leaving no grandchild (contract test 3)" {
        # The child reports its grandchild's PID over stdout before hanging, so the drained
        # output tells this test exactly which process must be gone. Mutant M-R12d (plain kill,
        # or none) leaves the grandchild running.
        $childCommand = @'
$grandchild = Start-Process -FilePath ([Environment]::ProcessPath) -ArgumentList @("-NoProfile", "-Command", "Start-Sleep -Seconds 120") -PassThru
Write-Output ("GRANDCHILD:" + $grandchild.Id)
[Console]::Out.Flush()
Start-Sleep -Seconds 120
'@
        $result = Invoke-RunnerWithPowerShellChild -ChildCommand $childCommand -TimeoutSeconds 8
        $result.TimedOut | Should -BeTrue
        $result.StandardOutput | Should -Match "GRANDCHILD:\d+"
        $grandchildId = [int][regex]::Match($result.StandardOutput, "GRANDCHILD:(\d+)").Groups[1].Value

        # Tree-kill propagation is asynchronous; poll briefly before judging.
        $deadline = [datetime]::UtcNow.AddSeconds(5)
        $stillRunning = $true
        while ([datetime]::UtcNow -lt $deadline) {
            $stillRunning = $null -ne (Get-Process -Id $grandchildId -ErrorAction SilentlyContinue)
            if (-not $stillRunning) { break }
            Start-Sleep -Milliseconds 250
        }
        if ($stillRunning) { Stop-Process -Id $grandchildId -Force -ErrorAction SilentlyContinue }
        $stillRunning | Should -BeFalse -Because "Kill(`$true) must terminate the grandchild, not only the direct child"
    }

    It "drains simultaneous stdout and stderr larger than their pipe buffers without deadlock (contract test 4)" {
        # 100,000 characters per stream exceeds every pipe buffer; a runner that stops draining
        # either stream (mutant M-R12e) deadlocks the child against the full pipe and times out.
        $childCommand = '$o = [string]::new("o", 100000); $e = [string]::new("e", 100000); [Console]::Out.Write($o); [Console]::Error.Write($e)'
        $result = Invoke-RunnerWithPowerShellChild -ChildCommand $childCommand -TimeoutSeconds 30
        $result.TimedOut | Should -BeFalse
        $result.ExitCode | Should -Be 0
        $result.StandardOutput.Length | Should -Be 100000
        $result.StandardError.Length | Should -Be 100000
    }

    It "round-trips the child exit code (contract test 5)" {
        $result = Invoke-RunnerWithPowerShellChild -ChildCommand "exit 42" -TimeoutSeconds 30
        $result.Started | Should -BeTrue
        $result.TimedOut | Should -BeFalse
        $result.ExitCode | Should -Be 42
    }

    It "reports early child exit during stdin delivery as StdinFailure, never as success and never as a throw (contract test 6)" {
        # The child dies while the oversized write is still pending; measured, the write task
        # faults with IOException while the exit code stays readable. A partial write must
        # never look like verification: StdinCompleted stays false REGARDLESS of the clean exit
        # code, and the fault surfaces as a kind + type name, not an exception.
        $result = $null
        {
            $script:earlyExitResult = Invoke-RunnerWithPowerShellChild `
                -ChildCommand "exit 7" `
                -InputText $script:oversizedPayload `
                -TimeoutSeconds 20
        } | Should -Not -Throw
        $result = $script:earlyExitResult
        $result.TimedOut | Should -BeFalse
        $result.ExitCode | Should -Be 7
        $result.StdinCompleted | Should -BeFalse
        $result.FailureKind | Should -Be "StdinFailure"
        $result.FailureTypeName | Should -Match "IOException"
    }

    It "keeps a delivery timeout classified as TimedOut even when the child exits before the deadline (mutant M-R12l)" {
        # The child hands its inherited stdin pipe to a grandchild (whose own stdout/stderr are
        # redirected away) and then exits with a clean code BEFORE the delivery deadline. The
        # oversized write therefore stays pending - no broken-pipe fault - while HasExited turns
        # true: exactly the race in which a fall-through exit poll would erase the delivery
        # timeout and report TimedOut = false beside exit code 9. The spent delivery budget must
        # win: TimedOut stays true, StdinCompleted stays false, and no exit code converts the
        # result into success.
        $childCommand = @'
$outFile = [System.IO.Path]::GetTempFileName()
$errFile = [System.IO.Path]::GetTempFileName()
$grandchild = Start-Process -FilePath ([Environment]::ProcessPath) -ArgumentList @("-NoProfile", "-Command", "Start-Sleep -Seconds 120") -RedirectStandardOutput $outFile -RedirectStandardError $errFile -PassThru
Write-Output ("PIPEHOLDER:" + $grandchild.Id)
exit 9
'@
        $result = Invoke-RunnerWithPowerShellChild `
            -ChildCommand $childCommand `
            -InputText $script:oversizedPayload `
            -TimeoutSeconds 8
        try {
            # The scenario only exists if the child really exited early with the pipe held
            # open: a broken pipe would have produced StdinFailure instead of a timeout.
            $result.StandardOutput | Should -Match "PIPEHOLDER:\d+"
            $result.FailureKind | Should -Not -Be "StdinFailure"
            $result.TimedOut | Should -BeTrue
            $result.StdinCompleted | Should -BeFalse
            $result.ExitCode | Should -BeNullOrEmpty
        }
        finally {
            $pipeHolderMatch = [regex]::Match([string]$result.StandardOutput, "PIPEHOLDER:(\d+)")
            if ($pipeHolderMatch.Success) {
                Stop-Process -Id ([int]$pipeHolderMatch.Groups[1].Value) -Force -ErrorAction SilentlyContinue
            }
        }
    }

    It "maps a missing executable to a structured StartFailure instead of a raw exception (contract test 7)" {
        # Measured: unhandled, this surfaces as Win32Exception whose MESSAGE embeds the working
        # directory. The runner must convert it to a kind plus a bare type name.
        $result = $null
        {
            $script:startFailureResult = Invoke-NativeCommandWithInput `
                -FilePath "dms1270-no-such-runner-binary" `
                -ArgumentList @("-x") `
                -InputText "" `
                -TimeoutSeconds 10
        } | Should -Not -Throw
        $result = $script:startFailureResult
        $result.Started | Should -BeFalse
        $result.FailureKind | Should -Be "StartFailure"
        $result.FailureTypeName | Should -Be "System.ComponentModel.Win32Exception"
    }

    It "returns within the ceiling after a timed-out run and never lets the abandoned stdin task's later fault escape (contract test 13)" {
        # Kill-first was measured to complete the abandoned write as Faulted/IOException within
        # seconds; the bounded grace protects against the poisoned-task case. Either way the
        # call returns inside the ceiling and nothing is thrown then or later.
        $timeoutSeconds = 3
        $measured = $null
        {
            $script:graceMeasured = Measure-RunnerCall -Call {
                Invoke-RunnerWithPowerShellChild `
                    -ChildCommand "Start-Sleep -Seconds 60" `
                    -InputText $script:oversizedPayload `
                    -TimeoutSeconds $timeoutSeconds
            }
        } | Should -Not -Throw
        $measured = $script:graceMeasured
        $measured.Result.TimedOut | Should -BeTrue
        $measured.Result.StdinCompleted | Should -BeFalse
        $measured.ElapsedMs | Should -BeLessThan (($timeoutSeconds * 3000) + 10000)
    }

    It "emits exactly one result object and no stream pollution" {
        $everything = & {
            Invoke-RunnerWithPowerShellChild -ChildCommand "exit 0" -TimeoutSeconds 30
        } *>&1
        @($everything).Count | Should -Be 1
        @($everything)[0].FailureKind | Should -Be "None"
    }

    It "keeps candidate text, passwords, exception messages, and raw child output out of the failure diagnostics (contract test 11)" {
        # Sentinels ride in every channel a leak could come from: the input text, an argument
        # shaped like the real password argument, and the (missing) executable name. The only
        # failure diagnostic the structure carries is FailureTypeName, and it must be a bare
        # type name.
        $result = Invoke-NativeCommandWithInput `
            -FilePath "dms1270-no-such-runner-binary-SENTINELEXE" `
            -ArgumentList @("-e", "SQLCMDPASSWORD=SENTINELPW123") `
            -InputText "SENTINELCANDIDATE456" `
            -TimeoutSeconds 10
        $result.FailureKind | Should -Be "StartFailure"
        $result.FailureTypeName | Should -Not -Match "SENTINEL"
        $result.FailureTypeName | Should -Match '^[A-Za-z0-9._+]+$'
    }
}

Describe "Invoke-NativeCommandWithInput structural pins" {

    It "exists and was parsed for pinning" {
        $script:runnerText | Should -Not -BeNullOrEmpty
    }

    It "delivers stdin as explicit ASCII bytes through BaseStream, never through the StreamWriter text layer (contract test 12, mutants M-R12h/M-R12k)" {
        # Measured basis: a Close() on the text layer during a pending write throws and poisons
        # the task permanently, and its safety on completed writes rests only on the mutable
        # AutoFlush state. The FlushAsync pin is structural-only by design: write-through was
        # measured for redirected stdin, so dropping the flush is behaviorally unobservable and
        # this pin is its ONLY killer - reported honestly, per the spec.
        $script:runnerText | Should -Match '\.BaseStream'
        $script:runnerText | Should -Match '\[System\.Text\.Encoding\]::ASCII'
        $script:runnerText | Should -Match '\.WriteAsync\('
        $script:runnerText | Should -Match '\.FlushAsync\('
        $script:runnerText | Should -Not -Match 'StandardInput\.Write'
    }

    It "uses one monotonic stopwatch budget and no wall-clock deadline (structural - no reliable clock-adjustment oracle exists)" {
        # A DateTime-based deadline stretches or collapses when the system clock is adjusted
        # mid-run; the monotonic stopwatch does not. Behaviorally indistinguishable without
        # adjusting the host clock, so the requirement is pinned structurally, honestly.
        [regex]::Matches($script:runnerText, '\[System\.Diagnostics\.Stopwatch\]::StartNew\(').Count | Should -Be 1
        $script:runnerText | Should -Not -Match 'UtcNow'
        $script:runnerText | Should -Not -Match '\.AddSeconds\('
        $script:runnerText | Should -Not -Match '\.Restart\('
    }

    It "orders the timeout path as kill, then exit confirmation, then stdin close (contract test 12, corrected M-R12i - structural)" {
        # Kill only INITIATES termination, so kill-before-close alone is not enough: the close
        # must also follow an exit-confirmation wait, or it races the still-pending write (the
        # measured poison). The behavioral kill is not reliable here - a wrong-order mutant
        # that swallows the thrown exception still returns within the ceiling because the grace
        # bound abandons the poisoned task - so the full ordering is pinned.
        $terminationRegionStart = $script:runnerText.LastIndexOf('$result.TimedOut = $true')
        $terminationRegionStart | Should -BeGreaterThan 0
        $terminationRegion = $script:runnerText.Substring($terminationRegionStart)
        $killIndex = $terminationRegion.IndexOf('.Kill(')
        $killIndex | Should -BeGreaterThan 0
        $terminationRegion.Substring(0, $killIndex) | Should -Not -Match 'StandardInput\.Close\('
        $exitConfirmationIndex = $terminationRegion.IndexOf('WaitForExit(', $killIndex)
        $closeIndex = $terminationRegion.IndexOf('StandardInput.Close(', $killIndex)
        $exitConfirmationIndex | Should -BeGreaterThan $killIndex
        $closeIndex | Should -BeGreaterThan $exitConfirmationIndex
    }

    It "kills the entire tree, not only the root" {
        $script:runnerText | Should -Match '\.Kill\(\$true\)'
    }

    It "gates every unbounded wait behind confirmed termination and reports TerminationFailure otherwise (contract test 14, mutant M-R12j - structural)" {
        # A genuinely unkillable child is not portably constructible in a unit test, so the
        # gating is pinned structurally: the kill-failed branch must return BEFORE the
        # parameterless WaitForExit()/drains, carrying the TerminationFailure kind.
        $script:runnerText | Should -Match '"TerminationFailure"'
        $script:runnerText | Should -Match '(?s)if \(-not \$exitConfirmed\) \{.*?return \[pscustomobject\]\$result'
    }

    It "never awaits the stdin task unbounded (bounded grace only)" {
        $script:runnerText | Should -Match '\$cleanupGraceMs'
        $script:runnerText | Should -Not -Match '\$stdinTask\.GetAwaiter'
        $script:runnerText | Should -Not -Match '\$stdinTask\.Wait\(\)'
    }

    It "disposes the process on every path (contract test 9 - structural)" {
        $script:runnerText | Should -Match '(?s)finally \{\s*\$process\.Dispose\(\)'
    }

    It "guards each output drain individually so a read-task failure cannot escape or overwrite the primary result (contract test 8 - structural)" {
        # A faulted read task cannot be provoked portably (measured: teardown-adjacent reads
        # complete benignly with partial output), so the guard is pinned: each drain sits in
        # its own try/catch, and the catch assigns a kind only when none is set.
        [regex]::Matches($script:runnerText, '(?s)try \{\s*\$result\.Standard(Output|Error) = \$standard(Output|Error)Task\.GetAwaiter').Count | Should -Be 2
        [regex]::Matches($script:runnerText, 'if \(\$result\.FailureKind -eq "None"\)').Count | Should -BeGreaterOrEqual 2
    }

    It "does not name the PowerShell executable literally anywhere in this suite (mutant M-R12g)" {
        # Measured: name-based resolution is PATH-dependent at the command-discovery layer and
        # parent-directory-coincidence-dependent at the raw layer; only ProcessPath is exact.
        # The pattern is assembled at run time so this assertion cannot match itself.
        $selfText = Get-Content -LiteralPath $PSCommandPath -Raw
        $forbiddenName = "pw" + "sh"
        $selfText | Should -Not -Match $forbiddenName
        $selfText | Should -Match '\[Environment\]::ProcessPath'
    }
}
