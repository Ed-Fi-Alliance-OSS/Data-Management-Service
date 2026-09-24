# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

Set-StrictMode -Version Latest

function Invoke-CdcQualificationImagePull {
    <# .SYNOPSIS
    Retries an image pull and retains safe per-attempt evidence, including recovered failures.
    #>
    param([string] $Image, [string] $RawDirectory, [string] $Destination)

    # A nonzero native exit must reach the retry/evidence path even for callers that opt
    # into PowerShell native-command errors. Do not change the caller's preference.
    $PSNativeCommandUseErrorActionPreference = $false
    $safeImage = if ($Image -cmatch '\A[a-zA-Z0-9][a-zA-Z0-9._/:-]*(?:@sha256:[a-f0-9]{64})?\z') {
        ConvertTo-CdcSafeEvidence $Image
    }
    else { '[redacted]' }
    $pullId = [guid]::NewGuid().ToString('N')
    $retryDelays = @(5, 15)
    $maxAttempts = $retryDelays.Count + 1
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        $logName = "pull-$pullId-$attempt.log"
        $logPath = Join-Path $RawDirectory $logName
        $started = [DateTimeOffset]::UtcNow
        $timer = [Diagnostics.Stopwatch]::StartNew()
        & docker pull -- $Image *> $logPath
        $exitCode = $LASTEXITCODE
        $timer.Stop()
        $failureCategory = 'None'
        if ($exitCode -ne 0) {
            # Never copy arbitrary Docker prose, registry URLs or credentials into the
            # public artifact. Keep the full output privately and publish known categories.
            [string] $output = Get-Content -LiteralPath $logPath -Raw
            $failureCategory = switch -Regex ($output) {
                '(?i)toomanyrequests|too many requests|rate.?limit|\b429\b' { 'RateLimited'; break }
                '(?i)unauthorized|authentication required|access denied|denied:|\b401\b|\b403\b' { 'Authorization'; break }
                '(?i)manifest unknown|manifest.*not found|name unknown|\b404\b' { 'ImageNotFound'; break }
                '(?i)no space left on device' { 'DiskFull'; break }
                '(?i)no such host|temporary failure in name resolution' { 'Dns'; break }
                '(?i)x509|certificate|tls handshake' { 'Tls'; break }
                '(?i)timeout|timed out|context deadline exceeded' { 'Timeout'; break }
                '(?i)connection reset|connection refused|unexpected EOF|network is unreachable' { 'Connection'; break }
                '(?i)\b50[0234]\b|internal server error|bad gateway|service unavailable|gateway timeout' { 'RegistryUnavailable'; break }
                default { 'Unknown' }
            }
        }
        $retrySeconds = if ($exitCode -ne 0 -and $attempt -lt $maxAttempts) { $retryDelays[$attempt - 1] } else { 0 }
        $outcome = if ($exitCode -eq 0) { 'Succeeded' } else { 'Failed' }
        [ordered]@{
            Image = $safeImage; Attempt = $attempt; MaxAttempts = $maxAttempts
            StartedAtUtc = $started.ToString('O'); DurationMs = $timer.ElapsedMilliseconds
            ExitCode = $exitCode; Outcome = $outcome; FailureCategory = $failureCategory
            RetryDelaySeconds = $retrySeconds; PrivateLog = $logName
        } | ConvertTo-Json -Compress | Add-Content -LiteralPath (Join-Path $Destination 'image-pulls.jsonl')
        Write-Output "CDC image pull $safeImage attempt $attempt/$maxAttempts`: $outcome (exit=$exitCode, category=$failureCategory, retrySeconds=$retrySeconds)."
        if ($exitCode -eq 0) { return }
        if ($retrySeconds -gt 0) { Start-Sleep -Seconds $retrySeconds }
    }
    throw "EnvironmentUnavailable: required image pull failed for $safeImage after $maxAttempts attempts (exit=$exitCode, category=$failureCategory); see image-pulls.jsonl."
}

function Get-CdcQualificationReport {
    <# .SYNOPSIS
    Classifies complete TRX results without treating skipped or unavailable cases as evidence.
    #>
    param([string] $Path, [int] $ExitCode)

    if (-not (Test-Path -LiteralPath $Path)) {
        return [ordered]@{ Status = 'ReportUnavailable'; Total = 0; Passed = 0; Failed = 0; Skipped = 0 }
    }
    [xml] $trx = Get-Content -LiteralPath $Path -Raw
    $results = @($trx.SelectNodes('//*[local-name()="UnitTestResult"]'))
    $passed = @($results | Where-Object outcome -eq 'Passed').Count
    $failed = @($results | Where-Object outcome -eq 'Failed').Count
    $skipped = $results.Count - $passed - $failed
    $environmentFailures = @($results | Where-Object {
        $explicitPrerequisite = $_.InnerText -match 'CDC_TEMPLATE_PINNED_IMAGE_DOCKER_PREREQUISITE_FAILURE|CDC packaged-history qualification requires|provider cleanup qualification requires its admin endpoint'
        $setupConnectionFailure = $_.InnerText -match '\bRunSetUp\b' -and
            $_.InnerText -match '(?i)connection.*(?:refused|error)|server was not found'
        $_.outcome -ne 'Passed' -and ($explicitPrerequisite -or $setupConnectionFailure)
    }).Count
    $counters = $trx.SelectSingleNode('//*[local-name()="Counters"]')
    $complete = $null -ne $counters -and [int] $counters.total -eq $results.Count -and
        [int] $counters.executed -eq $results.Count -and [int] $counters.passed -eq $passed -and
        [int] $counters.GetAttribute('failed') -eq $failed
    if ($null -ne $counters) {
        foreach ($attribute in @('error', 'timeout', 'aborted', 'passedButRunAborted', 'inconclusive', 'notRunnable', 'notExecuted', 'disconnected', 'pending', 'inProgress')) {
            if ([int] $counters.GetAttribute($attribute) -gt 0) { $complete = $false }
        }
    }
    $status = if ($failed -gt $environmentFailures) { 'Failed' }
    elseif ($environmentFailures -gt 0) { 'EnvironmentUnavailable' }
    elseif ($ExitCode -ne 0 -or $failed -gt 0) { 'Failed' }
    elseif ($skipped -gt 0) { 'SkippedQualification' }
    elseif (-not $complete -or $results.Count -eq 0) { 'ReportIncomplete' }
    else { 'Passed' }
    # Attachments survive passing tests too. Count observed failures separately from the
    # deliberate process exits used to qualify the recovery mechanism itself.
    $startupFailures = 0; $startupRecoveries = 0
    $injectedFailures = 0; $injectedRecoveries = 0
    $signatures = @{}
    foreach ($file in Get-ChildItem -LiteralPath (Split-Path -Parent $Path) -Filter 'admission-evidence-sql-*.json' -Recurse) {
        $evidence = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
        if ($evidence.Stage -eq 'unprovisioned-sql-startup') {
            if ($evidence['Container']?['Logs']?['InjectedFailure'] -eq $true) { $injectedFailures++ }
            else {
                $startupFailures++
                $signature = if ($evidence.Signature -in @('LsaInitializationTimeout', 'ReasonTwoErrnoEleven')) { $evidence.Signature } else { 'Other' }
                if (-not $signatures.ContainsKey($signature)) { $signatures[$signature] = 0 }
                $signatures[$signature]++
            }
        }
        elseif ($evidence.Stage -eq 'unprovisioned-sql-recovery' -and $evidence.Outcome -eq 'Ready') {
            if ($evidence.Injected -eq $true) { $injectedRecoveries++ }
            else { $startupRecoveries++ }
        }
    }
    return [ordered]@{
        Status = $status; Total = $results.Count; Passed = $passed; Failed = $failed
        Skipped = $skipped; EnvironmentFailures = $environmentFailures
        SqlStartupFailures = $startupFailures; SqlStartupRecoveries = $startupRecoveries
        SqlStartupInjectedFailures = $injectedFailures; SqlStartupInjectedRecoveries = $injectedRecoveries
        SqlStartupFailureSignatures = $signatures
        Diagnostics = @($results | Where-Object outcome -ne 'Passed' | ForEach-Object {
            [ordered]@{
                TestId = $_.GetAttribute('testId')
                Codes = @([regex]::Matches($_.InnerText, '\bCDC_[A-Z0-9_]+\b') | ForEach-Object Value | Select-Object -Unique)
            }
        })
    }
}

function ConvertTo-CdcSafeEvidence {
    param([AllowNull()] $Value, [string] $Key = '')

    # Structured fixtures already omit raw logs/configuration and document bodies. This second
    # boundary drops sensitive fields and arbitrary prose before publishing their attachments.
    if ($Key -match '(?i)password|secret|credential|connectionstring|payload|documentbody|hostname|database(name)?$|sourceidentifier') {
        return '[redacted]'
    }
    if ($Value -is [System.Collections.IDictionary]) {
        $copy = [ordered]@{}
        foreach ($name in $Value.Keys) { $copy[$name] = ConvertTo-CdcSafeEvidence $Value[$name] $name }
        return $copy
    }
    if ($Value -is [array]) {
        $items = @($Value | ForEach-Object { ConvertTo-CdcSafeEvidence $_ })
        return ,$items
    }
    if ($Value -is [string]) {
        if ($Value.Length -gt 240 -or $Value -match '(?i)sentinel|secret|EdFi_Dms1|password|://|localhost|127\.0\.0\.1|edfi_datastore|admission_[a-f0-9]{32}|dms-cdc-template-[a-f0-9]{32}|cdc-(?:policy|history|admission)-[a-f0-9]|(?:Host|Server|User Id)=|[{}]') {
            return '[redacted]'
        }
    }
    return $Value
}

function Export-CdcQualificationEvidence {
    <# .SYNOPSIS
    Publishes result metadata and structured attachments without raw assertion output.
    #>
    param([string] $RawDirectory, [string] $Destination)

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    $attachmentPattern = '^(?:cdc-runbook-|cdc-message-contract-|admission-evidence-|cdc-controller-|managed-lifecycle-|native-recovery-|cdc-history-|record-size-|\d+-)[a-zA-Z0-9_.-]+\.json$'
    foreach ($file in Get-ChildItem -LiteralPath $RawDirectory -Filter '*.trx' -Recurse) {
        [xml] $trx = Get-Content -LiteralPath $file.FullName -Raw
        # Assertion diffs and stdout can contain a whole record or an unlabelled password.
        # Publish outcomes/timings and structured attachments; never publish raw test output.
        foreach ($node in @($trx.SelectNodes('//*[local-name()="Output" or local-name()="CollectorDataEntries" or local-name()="RunInfos"]'))) {
            $node.ParentNode.RemoveChild($node) | Out-Null
        }
        foreach ($node in @($trx.SelectNodes('//*[local-name()="ResultFile"]'))) {
            $name = [IO.Path]::GetFileName($node.path.Replace('\', '/'))
            if ($name -match $attachmentPattern) { $node.path = $name }
            else { $node.ParentNode.RemoveChild($node) | Out-Null }
        }
        foreach ($node in $trx.SelectNodes('//*[local-name()="UnitTestResult"]')) {
            $node.SetAttribute('relativeResultsDirectory', '.')
        }
        foreach ($attribute in $trx.SelectNodes('//@*')) {
            # TestCase arguments can themselves contain a sentinel or a credential-bearing URL.
            # Namespace declarations are structural XML, not diagnostic values.
            # History attachment basenames contain only a generated NUnit ID and GUID.
            # Their directory was removed above; do not mistake this safe filename for
            # a private history source identifier and break its published JSON link.
            $generatedHistoryAttachment = $attribute.Name -eq 'path' -and
                $attribute.OwnerElement.LocalName -eq 'ResultFile' -and
                $attribute.Value -cmatch '^cdc-history-\d+(?:-\d+)*-[a-f0-9]{32}\.json$'
            if ($generatedHistoryAttachment) { continue }
            if ($attribute.Name -ne 'xmlns' -and $attribute.Prefix -ne 'xmlns') {
                $attribute.Value = ConvertTo-CdcSafeEvidence $attribute.Value
            }
        }
        $trx.Save((Join-Path $Destination $file.Name))
    }
    foreach ($file in Get-ChildItem -LiteralPath $RawDirectory -Filter '*.json' -Recurse) {
        # Only test attachments, never runtime settings, workflow journals or source documents.
        if ($file.Name -notmatch $attachmentPattern) { continue }
        if ($file.Name -like 'cdc-runbook-images-*') {
            # Image provenance is separate from Pester outcomes. Publish only bounded identities.
            try {
                $manifest = Get-Content -LiteralPath $file.FullName -Raw |
                    ConvertFrom-Json -AsHashtable -NoEnumerate -Depth 100 -ErrorAction Stop
            }
            catch { continue }
            if ($manifest -isnot [System.Collections.IDictionary] -or
                $manifest['SnippetId'] -isnot [string] -or
                $manifest['SnippetId'] -cnotmatch '^cdc-[a-z0-9-]{1,124}\z' -or
                $manifest['Provider'] -isnot [string] -or
                $manifest['Provider'] -cnotin @('postgresql', 'sqlserver')) { continue }
            $imageFields = @('ProviderImageId', 'ApplicationImage', 'ConfigurationImage')
            if (@($imageFields | Where-Object {
                $manifest[$_] -isnot [string] -or $manifest[$_] -cnotmatch '^sha256:[a-f0-9]{64}\z'
            }).Count) { continue }
            [ordered]@{
                SnippetId = $manifest.SnippetId
                Provider = $manifest.Provider
                ProviderImageId = $manifest.ProviderImageId
                ApplicationImage = $manifest.ApplicationImage
                ConfigurationImage = $manifest.ConfigurationImage
            } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Destination $file.Name)
            continue
        }
        $value = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable -NoEnumerate -Depth 100
        if ($file.Name -like 'cdc-runbook-*') {
            # Narrow procedure attachment schema. Never retain settings, command output or prose,
            # even innocuous-looking values missed by the general fixture redaction heuristic.
            $safeValue = @($value.Cases | ForEach-Object {
                if ($_.TestId -cmatch '^CDC-DOC cdc-[a-z0-9-]+$' -and
                    $_.SnippetId -cmatch '^cdc-[a-z0-9-]+$' -and
                    $_.Outcome -cin @('Passed', 'NotPassed', 'Missing', 'Duplicate')) {
                    [ordered]@{ TestId = $_.TestId; SnippetId = $_.SnippetId; Outcome = $_.Outcome }
                }
            })
        }
        else { $safeValue = ConvertTo-CdcSafeEvidence $value }
        ConvertTo-Json -InputObject $safeValue -Depth 100 |
            Set-Content -LiteralPath (Join-Path $Destination $file.Name)
    }
}

function Get-CdcQualificationProviderSuite {
    <# .SYNOPSIS
    Returns the ordered provider suites and their complete test filters for execution and CI scheduling.
    #>
    param([ValidateSet('Postgresql', 'Mssql')][string] $Provider)

    $categories = [ordered]@{
        Admission = 'CdcControllerAdmission'; Lifecycle = 'CdcControllerManagedLifecycle'
        Recovery = 'CdcControllerNativeRecovery'; RecordSize = 'CdcControllerRecordSize'
        Telemetry = 'CdcConnectorTelemetryQualification'; History = 'CdcPublicationHistory'
    }
    $filters = [ordered]@{}
    foreach ($phase in $categories.Keys) {
        $filters[$phase] = "Category=$($categories[$phase])&Category=$($Provider)Integration"
    }
    $filters['MessageContract'] = "(Category=CdcMessageContractSerialized|Category=CdcMessageContractKafka)&Category=$($Provider)Integration"
    return $filters
}

function Get-CdcRunbookPesterReport {
    <# .SYNOPSIS
    Requires named wrapper cases independently of discovery; exclusions cannot pass qualification.
    #>
    param([object[]] $Tests, [ValidateSet('Contract', 'PostgresqlSetup', 'MssqlSetup', 'PostgresqlLifecycle', 'MssqlLifecycle')][string] $QualificationProfile = 'Contract')
    [string[]] $required = if ($QualificationProfile -in @('PostgresqlLifecycle', 'MssqlLifecycle')) { @('cdc-managed-start') } elseif ($QualificationProfile -eq 'PostgresqlSetup') { @('cdc-pg-bootstrap-local', 'cdc-pg-e2e-setup') } elseif ($QualificationProfile -eq 'MssqlSetup') { @('cdc-sqlserver-bootstrap-local', 'cdc-sqlserver-bootstrap-published', 'cdc-sqlserver-e2e-setup') } else { @(
        'cdc-pg-bootstrap-local', 'cdc-pg-bootstrap-published',
        'cdc-sqlserver-bootstrap-local', 'cdc-sqlserver-bootstrap-published',
        'cdc-pg-e2e-setup', 'cdc-sqlserver-e2e-setup', 'cdc-pg-e2e-build', 'cdc-sqlserver-e2e-build',
        'cdc-pg-infrastructure', 'cdc-sqlserver-infrastructure',
        'cdc-managed-stop', 'cdc-managed-start', 'cdc-managed-start-rejected', 'cdc-stack-teardown'
    ) }
    $cases = @($required | ForEach-Object {
        $id = $_
        $found = @($Tests | Where-Object ExpandedName -eq "CDC-DOC $id")
        $outcome = if ($found.Count -eq 0) { 'Missing' } elseif ($found.Count -ne 1) { 'Duplicate' }
        elseif ($found[0].Result -eq 'Passed') { 'Passed' } else { 'NotPassed' }
        [ordered]@{ TestId = "CDC-DOC $id"; SnippetId = $id.Replace('-start-rejected', '-start'); Outcome = $outcome }
    })
    $passed = @($cases | Where-Object Outcome -eq Passed).Count
    return [ordered]@{ Name = $(if ($QualificationProfile -in @('PostgresqlLifecycle', 'MssqlLifecycle')) { $QualificationProfile.Replace('Lifecycle', '-runbook-lifecycle') } elseif ($QualificationProfile -eq 'PostgresqlSetup') { 'Postgresql-runbook-setup' } elseif ($QualificationProfile -eq 'MssqlSetup') { 'Mssql-runbook-setup' } else { 'runbook-wrappers' }); Status = $(if ($passed -eq $required.Count) { 'Passed' } else { 'Failed' });
        Total = $required.Count; Passed = $passed; Failed = $required.Count - $passed; Skipped = 0; Cases = $cases }
}

function Get-CdcRunbookCliReport {
    <# .SYNOPSIS
    Requires command, configuration, output, packaged and link cases in the Contract CLI report.
    #>
    param([string] $Path)
    # Minimum parameterized counts detect partial discovery as well as whole-fixture exclusion.
    $required = [ordered]@{
        It_dispatches_the_marked_command_and_exposes_its_options_in_help = 19
        It_Cdc_runbook_loads_complete_provider_settings_and_renders_the_connector = 2
        It_Cdc_runbook_reads_the_marked_no_consumers_acknowledgement = 12
        It_Cdc_runbook_rejects_unsupported_recovery_using_original_controller_evidence = 6
        It_matches_status_excerpts_and_optional_fields_from_the_controller = 8
        It_matches_operation_scoped_results = 2
        It_Cdc_runbook_emits_the_operation_scoped_example_in_one_stdout_value = 2
        It_Cdc_runbook_matches_packaged_failure_diagnostics_and_exit_codes = 3
        It_Cdc_runbook_keeps_watch_pass_json_on_stderr_and_one_final_result_on_stdout = 1
        It_resolves_relative_links_and_explicit_or_generated_anchors = 13
    }
    Get-CdcRequiredMethodReport -Path $Path -Required $required -Name 'runbook-cli'
}

function Get-CdcRunbookLifecycleReport {
    <# .SYNOPSIS
    Requires retained-offset, restart and rejection cases from the existing provider Lifecycle selection.
    #>
    param([string] $Path)
    # Exact provider case counts also reject duplicate discovery.
    $required = [ordered]@{
        It_keeps_committed_offsets_and_no_tasks_across_worker_restart_until_guarded_start = 1
        It_restarts_an_intact_running_connector_with_fresh_ready_evidence = 2
        It_rejects_missing_corrupt_and_incomplete_provenance_without_authorizing_resume = 1
        It_rejects_unavailable_live_evidence_while_stopped = 2
        It_rejects_an_independent_empty_or_populated_source_without_mutating_either = 2
        It_retains_a_terminal_incident_despite_healthy_current_provider_and_offset_evidence = 1
    }
    Get-CdcRequiredMethodReport -Path $Path -Required $required -Name 'runbook-lifecycle-behavior' -ExactCount
}

function Get-CdcRunbookRecoveryReport {
    <# .SYNOPSIS
    Requires every native recovery case, including packaged marked-command observations.
    #>
    param([string] $Path)
    $required = [ordered]@{
        It_observes_publication_before_crash_revalidation_and_rejects_prior_process_metrics = 1
        It_detects_failed_task_recovery_on_the_same_worker_without_certifying_the_gap = 1
        It_routes_incomplete_or_acknowledged_but_unverified_shutdown_to_native_recovery = 2
        It_rejects_unknown_recovery_evidence_and_unauthorized_controller_mutations = 1
        It_rejects_missing_provenance_after_native_recovery_without_reconstructing_state = 1
        It_contains_recovered_connectors_and_retains_terminal_history_loss = 2
        It_repeats_the_offline_barrier_sequence_after_worker_crash_interrupts_initial_readiness = 1
    }
    Get-CdcRequiredMethodReport -Path $Path -Required $required -Name 'runbook-recovery-behavior' -ExactCount
}

function Get-CdcRunbookTelemetryReport {
    <# .SYNOPSIS
    Requires exporter replacement and marked provider inspections in the existing Telemetry lane.
    #>
    param([string] $Path, [ValidateSet('Postgresql', 'Mssql')] [string] $Provider)
    $required = [ordered]@{
        It_qualifies_the_pinned_exporter_with_real_streaming_and_replaces_task_and_worker_metrics = 1
    }
    if ($Provider -eq 'Postgresql') {
        $required.It_executes_marked_slot_disk_and_progress_inspections_with_unavailable_actions = 1
    } else {
        $required.It_executes_marked_capture_retention_version_store_and_disk_inspections_with_unavailable_actions = 1
    }
    Get-CdcRequiredMethodReport -Path $Path -Required $required -Name 'runbook-telemetry-behavior' -ExactCount
}

function Get-CdcRunbookRecordSizeReport {
    <# .SYNOPSIS
    Requires live marked acknowledgement/command cases and their rollout/rejection evidence.
    #>
    param([string] $Path)
    $required = [ordered]@{
        It_executes_marked_record_size_increase_with_explicit_inventory = 2
        It_executes_marked_record_size_retry_after_partial_broker_change = 1
        It_orders_a_confirmed_increase_and_preserves_identity = 2
        It_rejects_incomplete_or_mismatched_confirmation_without_mutation = 6
        It_requires_renewed_confirmation_at_every_interrupted_boundary = 7
        It_replays_the_uncommitted_over_budget_materialized_record_after_capacity_alignment = 1
        It_retains_broker_history_through_managed_down_up_and_acknowledged_size_recreation = 1
    }
    Get-CdcRequiredMethodReport -Path $Path -Required $required -Name 'runbook-record-size-behavior' -ExactCount
}

function Get-CdcRunbookHistoryReport {
    <# .SYNOPSIS
    Requires exact packaged history cases and the live provider/marked retirement follow-on.
    #>
    param([string] $Path, [ValidateSet('History', 'Cleanup')] [string] $Selection = 'History')
    $required = if ($Selection -eq 'History') { [ordered]@{
        It_allows_all_three_commands_from_managed_non_CDC_creation = 1
        It_rejects_initial_enablement_of_a_managed_non_CDC_source_published_to_the_runtime = 2
        It_rejects_untrusted_or_exposed_history_without_any_mutation = 15
        It_preserves_historical_rejection_after_retiring_possible_exposure_with_a_surviving_source = 1
        It_rejects_after_reservation_wins_without_entering_the_provider_mutex_early = 3
        It_holds_the_controller_lock_through_E18_mutation_when_administration_wins = 3
    } } else { [ordered]@{
        It_removes_provider_artifacts_and_only_owned_unused_database_jobs = 1
        It_keeps_shared_provider_artifacts_and_rejects_unsafe_deletion = 1
        It_rejects_broad_or_orphaned_provider_artifacts = 1
        It_rejects_a_different_live_physical_source = 1
        It_reconciles_actual_committed_deletion_after_lost_response = 1
        It_resumes_interrupted_retirement_and_preserves_shared_artifacts_and_source_history = 1
    } }
    Get-CdcRequiredMethodReport -Path $Path -Required $required -Name "runbook-$($Selection.ToLowerInvariant())-behavior" -ExactCount
}

function Get-CdcRunbookKafkaReport {
    <# .SYNOPSIS
    Requires exact marked Kafka inspections and authenticated policy cases for each separate profile.
    #>
    param([string] $Path, [ValidateSet('Secured', 'Local')] [string] $KafkaProfile)
    $required = if ($KafkaProfile -eq 'Secured') { [ordered]@{
        It_executes_marked_broker_retention_inspections_and_rejects_denied_authority = 1
        It_prepares_live_worker_only_offsets_before_the_qualified_worker_starts = 1
        It_allows_only_the_configured_public_topic_and_consumer_group = 2
        It_denies_cross_binding_and_internal_reads = 16
        It_denies_consumer_writes_and_connector_access_to_worker_offsets = 1
        It_preserves_unavailable_acl_authority_when_description_is_denied = 1
        It_fails_closed_on_unsafe_effective_grants_even_with_a_missing_required_grant = 6
        It_rejects_live_topic_drift_without_repair = 11
        It_requires_explicit_isr_even_when_the_broker_default_is_two = 2
        It_retains_tombstones_with_the_public_policy_and_accepts_stronger_retention = 1
        It_rejects_changed_partition_identity_and_actual_weak_replica_assignments = 3
    } } else { [ordered]@{
        It_labels_local_policy_without_claiming_acl_or_production_durability_proof = 1
        It_executes_marked_topic_policy_status_with_shared_offset_drift = 1
    } }
    Get-CdcRequiredMethodReport -Path $Path -Required $required -Name "runbook-kafka-$($KafkaProfile.ToLowerInvariant())" -ExactCount
}

function Get-CdcRunbookConsumerReport {
    <# .SYNOPSIS
    Requires all six existing provider-neutral consumer broker conformance scenarios.
    #>
    param([string] $Path)
    $required = [ordered]@{
        It_requires_durable_application_and_every_checkpoint_including_the_empty_partition = 1
        It_reconstructs_independently_expected_state_from_higher_lower_duplicate_and_null_records = 1
        It_renews_an_idle_proof_from_real_unchanged_ends_only_after_all_checkpoints_complete = 1
        It_continues_from_durable_next_offsets_without_replaying_bootstrap = 1
        It_reconstructs_from_earliest_after_checkpoint_loss = 1
        It_reconstructs_from_earliest_after_checkpoint_corruption = 1
    }
    Get-CdcRequiredMethodReport -Path $Path -Required $required -Name 'runbook-consumer-reference' -ExactCount
}

function Get-CdcRequiredMethodReport {
    param([string] $Path, [Collections.IDictionary] $Required, [string] $Name, [switch] $ExactCount)
    $results = @()
    if (Test-Path -LiteralPath $Path) {
        [xml] $trx = Get-Content -LiteralPath $Path -Raw
        $results = @($trx.SelectNodes('//*[local-name()="UnitTestResult"]'))
    }
    $cases = @($required.Keys | ForEach-Object {
        $id = $_
        $found = @($results | Where-Object { $_.testName.Split('(')[0] -eq $id })
        $passed = @($found | Where-Object outcome -eq Passed).Count
        [ordered]@{ TestId = $id; Required = $required[$id]; Total = $found.Count; Passed = $passed;
            Outcome = $(if (($found.Count -eq $required[$id] -or (-not $ExactCount -and $found.Count -gt $required[$id])) -and $passed -eq $found.Count) { 'Passed' } else { 'NotPassed' }) }
    })
    $passed = @($cases | Where-Object Outcome -eq Passed).Count
    return [ordered]@{ Name = $Name; Status = $(if ($passed -eq $required.Count) { 'Passed' } else { 'Failed' });
        Total = $required.Count; Passed = $passed; Failed = $required.Count - $passed; Skipped = 0; Cases = $cases }
}

Export-ModuleMember -Function Invoke-CdcQualificationImagePull, Get-CdcQualificationReport, Export-CdcQualificationEvidence, Get-CdcQualificationProviderSuite, Get-CdcRunbookPesterReport, Get-CdcRunbookCliReport, Get-CdcRunbookLifecycleReport, Get-CdcRunbookRecoveryReport, Get-CdcRunbookRecordSizeReport, Get-CdcRunbookHistoryReport, Get-CdcRunbookTelemetryReport, Get-CdcRunbookKafkaReport, Get-CdcRunbookConsumerReport
