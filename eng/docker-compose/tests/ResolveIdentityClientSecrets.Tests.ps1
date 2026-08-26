# SPDX-License-Identifier: Apache-2.0
# Licensed to the Ed-Fi Alliance under one or more agreements.
# The Ed-Fi Alliance licenses this file to you under the Apache License, Version 2.0.
# See the LICENSE and NOTICES files in the project root for more information.

# Regression coverage for DMS-1171: the local startup scripts must register the CMS
# identity clients with the same secrets DMS/CMS authenticate with, so that overriding
# CONFIG_SERVICE_CLIENT_SECRET (or DMS_CONFIG_IDENTITY_CLIENT_SECRET) does not produce a
# secret mismatch that breaks CMS token acquisition.

BeforeAll {
    $script:dockerComposeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    Import-Module (Join-Path $script:dockerComposeRoot "env-utility.psm1") -Force
    $script:defaultSecret = "ValidClientSecret1234567890!Abcd"
}

Describe "Resolve-IdentityClientSecretConfiguration" {
    Context "when env-file values are provided" {
        It "returns CONFIG_SERVICE_CLIENT_SECRET for the CMSReadOnlyAccess client" {
            $result = Resolve-IdentityClientSecretConfiguration -EnvValues @{ CONFIG_SERVICE_CLIENT_SECRET = "OverrideReadOnly1234567890!Abcd" }
            $result.CmsReadOnlyAccessClientSecret | Should -Be "OverrideReadOnly1234567890!Abcd"
        }

        It "returns DMS_CONFIG_IDENTITY_CLIENT_SECRET for the DmsConfigurationService client" {
            $result = Resolve-IdentityClientSecretConfiguration -EnvValues @{ DMS_CONFIG_IDENTITY_CLIENT_SECRET = "OverrideFullAccess123456789!Abcd" }
            $result.DmsConfigurationServiceClientSecret | Should -Be "OverrideFullAccess123456789!Abcd"
        }

        It "resolves both clients independently" {
            $result = Resolve-IdentityClientSecretConfiguration -EnvValues @{
                CONFIG_SERVICE_CLIENT_SECRET     = "ReadOnlySecret1234567890!Abcdef"
                DMS_CONFIG_IDENTITY_CLIENT_SECRET = "FullAccessSecret1234567890!Abcd"
            }
            $result.CmsReadOnlyAccessClientSecret | Should -Be "ReadOnlySecret1234567890!Abcdef"
            $result.DmsConfigurationServiceClientSecret | Should -Be "FullAccessSecret1234567890!Abcd"
        }

        It "resolves custom client-secret length bounds from the env file" {
            $result = Resolve-IdentityClientSecretConfiguration -EnvValues @{
                DMS_CONFIG_IDENTITY_CLIENT_SECRET_MINIMUM_LENGTH = "10"
                DMS_CONFIG_IDENTITY_CLIENT_SECRET_MAXIMUM_LENGTH = "200"
            }
            $result.ClientSecretMinimumLength | Should -Be 10
            $result.ClientSecretMaximumLength | Should -Be 200
        }
    }

    Context "when env-file values are missing or blank" {
        It "falls back to the local-dev default for both clients" {
            $result = Resolve-IdentityClientSecretConfiguration -EnvValues @{}
            $result.CmsReadOnlyAccessClientSecret | Should -Be $script:defaultSecret
            $result.DmsConfigurationServiceClientSecret | Should -Be $script:defaultSecret
        }

        It "treats a whitespace-only value as missing" {
            $result = Resolve-IdentityClientSecretConfiguration -EnvValues @{ CONFIG_SERVICE_CLIENT_SECRET = "   " }
            $result.CmsReadOnlyAccessClientSecret | Should -Be $script:defaultSecret
        }

        It "falls back to the default 32/128 length bounds" {
            $result = Resolve-IdentityClientSecretConfiguration -EnvValues @{}
            $result.ClientSecretMinimumLength | Should -Be 32
            $result.ClientSecretMaximumLength | Should -Be 128
        }
    }
}

Describe "Start scripts register identity clients with env-file secrets" {
    # Discovery-time cases: $PSScriptRoot is available during discovery in Pester v5.
    $composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $cases = @("start-local-dms.ps1", "start-published-dms.ps1", "start-local-config.ps1") | ForEach-Object {
        @{ Name = $_; ScriptPath = (Join-Path $composeRoot $_) }
    }

    It "resolves the identity client secrets from the env file in <Name>" -ForEach $cases {
        # The per-client bindings asserted below reference $identityClientSecrets, so the script
        # must populate it from the env file via the shared resolver.
        $content = Get-Content -LiteralPath $ScriptPath -Raw
        $content | Should -Match '\$identityClientSecrets\s*=\s*Resolve-IdentityClientSecretConfiguration\s+-EnvValues\s+\$envValues'
    }

    It "binds each client registration to the matching resolved secret and bounds in <Name>" -ForEach $cases {
        # A presence-only check (does the line contain -NewClientSecret?) is not enough: it would
        # still pass with a hard-coded value, with the same secret reused for both clients, or with
        # the two resolved secrets swapped. Assert the exact resolved property each client requires:
        #   - CMSReadOnlyAccess           -> CmsReadOnlyAccessClientSecret        (CONFIG_SERVICE_CLIENT_SECRET)
        #   - DmsConfigurationService     -> DmsConfigurationServiceClientSecret  (DMS_CONFIG_IDENTITY_CLIENT_SECRET)
        #   - CMSAuthMetadataReadOnlyAccess has no dedicated env-file secret and keeps the default.
        # Both operator-secret clients must also pass the env-file length bounds so a CMS-valid
        # secret is not rejected by the setup scripts' default 32/128 validation.
        $minBoundPattern = '-ClientSecretMinimumLength\s+\$identityClientSecrets\.ClientSecretMinimumLength\b'
        $maxBoundPattern = '-ClientSecretMaximumLength\s+\$identityClientSecrets\.ClientSecretMaximumLength\b'

        $setupLines = Get-Content -LiteralPath $ScriptPath | Where-Object {
            $_ -match '\./setup-(keycloak|openiddict)\.ps1' -and $_ -notmatch '-InitDb'
        }

        $setupLines | Should -Not -BeNullOrEmpty -Because "each start script invokes the identity setup scripts"

        foreach ($line in $setupLines) {
            if ($line -match '-NewClientId\s+"CMSAuthMetadataReadOnlyAccess"') {
                # Intentionally not bound to env-file values; uses the setup defaults.
                $line | Should -Not -Match '\$identityClientSecrets\.' -Because "CMSAuthMetadataReadOnlyAccess has no dedicated env-file secret: $line"
            }
            elseif ($line -match '-NewClientId\s+"CMSReadOnlyAccess"') {
                $line | Should -Match '-NewClientSecret\s+\$identityClientSecrets\.CmsReadOnlyAccessClientSecret\b' -Because "CMSReadOnlyAccess must register CONFIG_SERVICE_CLIENT_SECRET: $line"
                $line | Should -Match $minBoundPattern -Because "CMSReadOnlyAccess must validate with the env-file minimum length: $line"
                $line | Should -Match $maxBoundPattern -Because "CMSReadOnlyAccess must validate with the env-file maximum length: $line"
            }
            else {
                # No -NewClientId => the default DmsConfigurationService (full_access) client.
                $line | Should -Match '-NewClientSecret\s+\$identityClientSecrets\.DmsConfigurationServiceClientSecret\b' -Because "DmsConfigurationService must register DMS_CONFIG_IDENTITY_CLIENT_SECRET: $line"
                $line | Should -Match $minBoundPattern -Because "DmsConfigurationService must validate with the env-file minimum length: $line"
                $line | Should -Match $maxBoundPattern -Because "DmsConfigurationService must validate with the env-file maximum length: $line"
            }
        }
    }
}

Describe "Start scripts pass the resolved database identity and role names to the identity setup scripts" {
    # Same class as the secret and bounds cases above, on the parameters that address the database
    # and name the roles CMS enforces: setup-openiddict.ps1 carries its own default for DbUser
    # (postgres), and BOTH setup-openiddict.ps1 and setup-keycloak.ps1 carry their own defaults for
    # the two role names (cms-client / dms-client), so a caller that omits them silently overrides a
    # configured stack -- under either identity provider. POSTGRES_USER is a supported override --
    # postgresql.yml passes ${POSTGRES_USER:-postgres} to the container -- and the role names are
    # mapped by local-config.yml and published-config.yml onto IdentitySettings:ConfigServiceRole /
    # :ClientRole.

    # Discovery-time cases: $PSScriptRoot is available during discovery in Pester v5.
    $composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
    $dmsCases = @("start-local-dms.ps1", "start-published-dms.ps1") | ForEach-Object {
        @{ Name = $_; ScriptPath = (Join-Path $composeRoot $_) }
    }
    $allCases = @("start-local-dms.ps1", "start-published-dms.ps1", "start-local-config.ps1") | ForEach-Object {
        @{ Name = $_; ScriptPath = (Join-Path $composeRoot $_) }
    }

    BeforeAll {
        # Get-ComposeResolvedEnvValue gives an ambient value precedence by contract, so the keys
        # under test are removed for the duration -- these cases are about what the ENV FILE
        # resolves to. Removed rather than blanked: a blank ambient value is not the same thing as
        # an absent one.
        $script:ambientBackup = @{}
        foreach ($key in @("POSTGRES_USER", "DMS_CONFIG_IDENTITY_SERVICE_ROLE", "DMS_CONFIG_IDENTITY_CLIENT_ROLE")) {
            $script:ambientBackup[$key] = [System.Environment]::GetEnvironmentVariable($key)
            if (Test-Path -LiteralPath "Env:\$key") { Remove-Item -LiteralPath "Env:\$key" }
        }

        # Evaluates the script's REAL assignment statement instead of pattern-matching its text, so
        # these cases prove the value that reaches setup-openiddict.ps1 rather than the presence of
        # a call to the resolver.
        function script:Get-ResolvedAssignment {
            param(
                [Parameter(Mandatory)] [string] $ScriptPath,
                [Parameter(Mandatory)] [string] $VariableName,
                [hashtable] $Variable = @{}
            )
            $parseError = $null
            $token = $null
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$token, [ref]$parseError)
            if ($parseError.Count -gt 0) {
                throw "'$ScriptPath' does not parse: $(($parseError | ForEach-Object { $_.Message }) -join '; ')"
            }
            $assignment = $ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                    $node.Left.VariablePath.UserPath -eq $VariableName
                }, $true) | Select-Object -First 1
            if ($null -eq $assignment) { throw "No assignment to the variable '$VariableName' was found in '$ScriptPath'." }

            return & {
                param($AssignmentText, $VariableTable, $Target)
                foreach ($entry in $VariableTable.GetEnumerator()) {
                    Set-Variable -Name $entry.Key -Value $entry.Value
                }
                . ([scriptblock]::Create($AssignmentText))
                return (Get-Variable -Name $Target -ValueOnly)
            } $assignment.Extent.Text $Variable $VariableName
        }

        # Every setup-openiddict.ps1 invocation in a start script, -InitDb and -InsertData alike.
        function script:Get-SetupOpeniddictLine {
            param([Parameter(Mandatory)] [string] $ScriptPath)
            return @(Get-Content -LiteralPath $ScriptPath | Where-Object { $_ -match '\./setup-openiddict\.ps1' })
        }

        # Every setup-keycloak.ps1 invocation: each one creates a client, so each one consumes the roles.
        function script:Get-SetupKeycloakLine {
            param([Parameter(Mandatory)] [string] $ScriptPath)
            return @(Get-Content -LiteralPath $ScriptPath | Where-Object { $_ -match '\./setup-keycloak\.ps1' })
        }

        # The parameter names a setup script declares, so a splat is checked against what will
        # actually bind rather than against the text of the call.
        function script:Get-ScriptParameterName {
            param([Parameter(Mandatory)] [string] $ScriptPath)
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$null, [ref]$null)
            return @($ast.ParamBlock.Parameters | ForEach-Object { $_.Name.VariablePath.UserPath })
        }

        # The conditions of every if-statement enclosing an assignment, so a test can prove the
        # assignment is shared by both identity-provider branches rather than nested under one.
        function script:Get-EnclosingIfCondition {
            param(
                [Parameter(Mandatory)] [string] $ScriptPath,
                [Parameter(Mandatory)] [string] $VariableName
            )
            $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$null, [ref]$null)
            $assignment = $ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
                    $node.Left -is [System.Management.Automation.Language.VariableExpressionAst] -and
                    $node.Left.VariablePath.UserPath -eq $VariableName
                }, $true) | Select-Object -First 1
            if ($null -eq $assignment) { throw "No assignment to the variable '$VariableName' was found in '$ScriptPath'." }

            $condition = [System.Collections.Generic.List[string]]::new()
            $node = $assignment.Parent
            while ($null -ne $node) {
                if ($node -is [System.Management.Automation.Language.IfStatementAst]) {
                    foreach ($clause in $node.Clauses) { $condition.Add($clause.Item1.Extent.Text) }
                }
                $node = $node.Parent
            }
            return @($condition)
        }
    }

    AfterAll {
        foreach ($key in $script:ambientBackup.Keys) {
            if ($null -ne $script:ambientBackup[$key]) {
                Set-Item -LiteralPath "Env:\$key" -Value $script:ambientBackup[$key]
            }
        }
    }

    It "resolves the PostgreSQL superuser from the env file in <Name>" -ForEach $dmsCases {
        # Without this the setup-openiddict.ps1 default reaches Build-ConnectionString as
        # Username=postgres -- these calls pass -EnvironmentFile, so the DB parameter group is always
        # what the connection string is built from -- and the bootstrap fails to connect before the
        # OpenIddict stores exist, which is before any later restore step can run.
        $resolved = Get-ResolvedAssignment -ScriptPath $ScriptPath -VariableName "identityDbParams" -Variable @{
            DatabaseEngine = "postgresql"
            envValues      = @{ POSTGRES_USER = "northridge_super" }
        }
        $resolved.DbUser | Should -Be "northridge_super"
        $resolved.DbName | Should -Be "ENV:DMS_CONFIG_DATABASE_NAME" -Because "the CMS database seam must be unchanged"
    }

    It "keeps the postgres default when POSTGRES_USER is unset in <Name>" -ForEach $dmsCases {
        $resolved = Get-ResolvedAssignment -ScriptPath $ScriptPath -VariableName "identityDbParams" -Variable @{
            DatabaseEngine = "postgresql"
            envValues      = @{}
        }
        $resolved.DbUser | Should -Be "postgres" -Because "the standard developer flow must be unchanged"
    }

    It "leaves the SQL Server branch on the sa login in <Name>" -ForEach $dmsCases {
        # mssql.yml has no POSTGRES_USER equivalent; the engine authenticates as the fixed sa account.
        $resolved = Get-ResolvedAssignment -ScriptPath $ScriptPath -VariableName "identityDbParams" -Variable @{
            DatabaseEngine = "mssql"
            envValues      = @{ POSTGRES_USER = "northridge_super" }
        }
        $resolved.DbUser | Should -Be "sa"
        $resolved.DbType | Should -Be "MSSQL"
    }

    It "resolves the PostgreSQL superuser from the env file in start-local-config.ps1" {
        $scriptPath = Join-Path ([System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))) "start-local-config.ps1"

        (Get-ResolvedAssignment -ScriptPath $scriptPath -VariableName "dbUser" -Variable @{
                datastore = "postgresql"
                envValues = @{ POSTGRES_USER = "northridge_super" }
            }) | Should -Be "northridge_super"

        (Get-ResolvedAssignment -ScriptPath $scriptPath -VariableName "dbUser" -Variable @{
                datastore = "postgresql"
                envValues = @{}
            }) | Should -Be "postgres" -Because "the standard developer flow must be unchanged"

        (Get-ResolvedAssignment -ScriptPath $scriptPath -VariableName "dbUser" -Variable @{
                datastore = "mssql"
                envValues = @{ POSTGRES_USER = "northridge_super" }
            }) | Should -Be "sa"
    }

    It "resolves the CMS-enforced role names from the env file in <Name>" -ForEach $allCases {
        # CMS's service policy requires the presented token to carry IdentitySettings:ConfigServiceRole.
        # Left to the setup defaults on an overridden stack the client is created carrying a role CMS
        # does not require: registration succeeds and tokens mint, and the failure surfaces later as
        # DMS unable to read claim sets.
        $resolved = Get-ResolvedAssignment -ScriptPath $ScriptPath -VariableName "identityRoleParams" -Variable @{
            envValues = @{
                DMS_CONFIG_IDENTITY_SERVICE_ROLE = "cms-operator"
                DMS_CONFIG_IDENTITY_CLIENT_ROLE  = "dms-operator"
            }
        }
        $resolved.ConfigServiceRole | Should -Be "cms-operator"
        $resolved.DmsClientRole | Should -Be "dms-operator"
    }

    It "keeps the cms-client/dms-client defaults when the role overrides are unset in <Name>" -ForEach $allCases {
        $resolved = Get-ResolvedAssignment -ScriptPath $ScriptPath -VariableName "identityRoleParams" -Variable @{
            envValues = @{}
        }
        $resolved.ConfigServiceRole | Should -Be "cms-client" -Because "the compose files fall back to this value"
        $resolved.DmsClientRole | Should -Be "dms-client" -Because "the compose files fall back to this value"
    }

    It "hands the resolved database identity to every setup-openiddict call in <Name>" -ForEach $allCases {
        # Asserted per call site rather than once per script: the regression this closes was a single
        # call that omitted the parameter while its siblings carried it, and a call added later would
        # reintroduce it the same way. -InitDb counts -- it is the call that creates the database,
        # and it runs first.
        $lines = Get-SetupOpeniddictLine -ScriptPath $ScriptPath
        $lines | Should -Not -BeNullOrEmpty -Because "each start script invokes setup-openiddict.ps1"
        foreach ($line in $lines) {
            $line | Should -Match '(@identityDbParams|-DbUser\s+\$dbUser)' -Because "this call would otherwise default to the postgres superuser: $line"
        }
    }

    It "hands the resolved role names to every client-creating setup-openiddict call in <Name>" -ForEach $allCases {
        # Only -InsertData creates the roles; -InitDb does not consume them.
        $lines = Get-SetupOpeniddictLine -ScriptPath $ScriptPath | Where-Object { $_ -match '-InsertData' }
        $lines | Should -Not -BeNullOrEmpty -Because "each start script registers the identity clients"
        foreach ($line in $lines) {
            $line | Should -Match '@identityRoleParams' -Because "this call would otherwise insert the setup defaults: $line"
        }
    }

    It "hands the resolved role names to every setup-keycloak call in <Name>" -ForEach $allCases {
        # The Keycloak half of the same class: every setup-keycloak.ps1 call creates a client and
        # grants it a role, and that script falls back to cms-client / dms-client exactly as the
        # OpenIddict one does. Asserted per call site, for the same reason as above.
        $lines = Get-SetupKeycloakLine -ScriptPath $ScriptPath
        $lines | Should -Not -BeNullOrEmpty -Because "each start script registers the identity clients under Keycloak"
        foreach ($line in $lines) {
            $line | Should -Match '@identityRoleParams' -Because "this call would otherwise grant the setup-keycloak defaults: $line"
        }
    }

    It "resolves the role names once, above the identity-provider branch, in <Name>" -ForEach $allCases {
        # A resolution that lives inside one provider's branch is the shape this closes: the other
        # branch then reads an undefined variable, splats nothing, and the setup script's defaults
        # win silently. Proven on the AST rather than by line order: no if-statement enclosing the
        # assignment may test $IdentityProvider.
        $conditions = Get-EnclosingIfCondition -ScriptPath $ScriptPath -VariableName "identityRoleParams"
        foreach ($condition in $conditions) {
            $condition | Should -Not -Match 'IdentityProvider' -Because "the role resolution must be shared by both providers, not nested under one: $condition"
        }

        # And it precedes every consumer, so no call can run before the variable exists.
        $lines = [string[]]@(Get-Content -LiteralPath $ScriptPath)
        $assignmentLine = [array]::FindIndex($lines, [Predicate[string]] { param($line) $line -match '^\s*\$identityRoleParams\s*=' })
        $assignmentLine | Should -BeGreaterOrEqual 0
        $firstConsumerLine = [array]::FindIndex($lines, [Predicate[string]] { param($line) $line -match '@identityRoleParams' })
        $firstConsumerLine | Should -BeGreaterThan $assignmentLine
    }

    It "splats only parameters both identity setup scripts declare in <Name>" -ForEach $allCases {
        # The same hashtable is splatted into two different scripts, so a key either script does not
        # declare fails binding at bootstrap time under that provider only. Checked against the real
        # param blocks rather than a copied list.
        $composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $resolved = Get-ResolvedAssignment -ScriptPath $ScriptPath -VariableName "identityRoleParams" -Variable @{ envValues = @{} }
        $keycloakParameter = Get-ScriptParameterName -ScriptPath (Join-Path $composeRoot "setup-keycloak.ps1")
        $openiddictParameter = Get-ScriptParameterName -ScriptPath (Join-Path $composeRoot "setup-openiddict.ps1")
        $resolved.Keys | Should -Not -BeNullOrEmpty
        foreach ($key in $resolved.Keys) {
            $keycloakParameter | Should -Contain $key -Because "setup-keycloak.ps1 must bind -$key"
            $openiddictParameter | Should -Contain $key -Because "setup-openiddict.ps1 must bind -$key"
        }
    }

    It "gives an ambient POSTGRES_USER precedence over the env file in <Name>" -ForEach $allCases {
        # Docker Compose interpolation lets a value set in the shell override the same key in the env
        # file, so the container can be running as a user the file never mentions. Reading the file
        # directly would satisfy the cases above and still address the wrong superuser here, which is
        # why the resolution goes through the shared Compose-precedence resolver.
        $variableName = if ($Name -eq "start-local-config.ps1") { "dbUser" } else { "identityDbParams" }
        $engine = if ($Name -eq "start-local-config.ps1") { @{ datastore = "postgresql" } } else { @{ DatabaseEngine = "postgresql" } }
        $variable = $engine + @{ envValues = @{ POSTGRES_USER = "file_only_user" } }

        $env:POSTGRES_USER = "ambient_super"
        try {
            $resolved = Get-ResolvedAssignment -ScriptPath $ScriptPath -VariableName $variableName -Variable $variable
            $actual = if ($variableName -eq "dbUser") { $resolved } else { $resolved.DbUser }
            $actual | Should -Be "ambient_super"
        }
        finally {
            Remove-Item -LiteralPath "Env:\POSTGRES_USER" -ErrorAction SilentlyContinue
        }
    }
}

Describe "setup-keycloak.ps1 percent-encodes the role name in both role lookup URLs" {
    # The role names are configured values, routed in by the start scripts through identityRoleParams
    # (asserted above), and each travels as one path segment of a Keycloak admin URL. Interpolated raw,
    # a space, '#', '?' or '/' ends the path, starts a fragment or a query, or adds a segment: the
    # lookup answers 404 for a role that exists, and Create_Role then creates a second one under the raw
    # name. Both lookups -- the realm role and the realm-management client role -- encode the segment,
    # and the encoded URL reads back as exactly the role name.
    BeforeAll {
        $composeRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
        $keycloakScript = Join-Path $composeRoot "setup-keycloak.ps1"
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($keycloakScript, [ref]$null, [ref]$null)
        foreach ($name in @("Get_Role", "Get_Realm_Admin_Role")) {
            $function = $ast.FindAll({
                    param($node)
                    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
                }, $true) | Select-Object -First 1
            if ($null -eq $function) { throw "setup-keycloak.ps1 must define $name" }
            . ([scriptblock]::Create($function.Extent.Text))
        }
        # The lifted functions read these from the script scope -- this file's, once lifted here -- and
        # the token unqualified, from the calling scope; Set-Variable, because the analyzer cannot see
        # that dynamic read and would report the assignment as unused.
        $script:KeycloakServer = "http://keycloak.test:8045"
        $script:Realm = "edfi"
        Set-Variable -Name access_token -Value "token"
        function Get_Realm_Management_ClientId { return "rm-client-id" }
    }

    It "requests '<Role>' as the single segment '<Encoded>' in both lookups" -ForEach @(
        @{ Role = "cms client"; Encoded = "cms%20client" },
        @{ Role = "role#1"; Encoded = "role%231" },
        @{ Role = "what?x"; Encoded = "what%3Fx" },
        @{ Role = "a/b"; Encoded = "a%2Fb" },
        @{ Role = "cms-client"; Encoded = "cms-client" }
    ) {
        # The false pass: the raw name produced a URL whose path, query or fragment differed from the
        # role, Keycloak answered 404, Get_Role returned null and Create_Role created the role again.
        # AbsoluteUri is the form the request goes out with; [uri]::ToString() would unescape %20 back
        # to a space and hide exactly the case under test.
        Mock Invoke-RestMethod { [pscustomobject]@{ name = "found"; requested = $Uri.AbsoluteUri } }

        $realmRole = Get_Role $Role
        $realmRole.requested | Should -Be "http://keycloak.test:8045/admin/realms/edfi/roles/$Encoded"
        ([uri]$realmRole.requested).Query | Should -BeNullOrEmpty
        ([uri]$realmRole.requested).Fragment | Should -BeNullOrEmpty
        ([uri]$realmRole.requested).Segments.Count | Should -Be 6 -Because "the role is one segment, however many separators it carries"
        [uri]::UnescapeDataString(([uri]$realmRole.requested).Segments[-1]) | Should -Be $Role

        $adminRole = Get_Realm_Admin_Role $Role
        $adminRole.requested | Should -Be "http://keycloak.test:8045/admin/realms/edfi/clients/rm-client-id/roles/$Encoded"
        ([uri]$adminRole.requested).Query | Should -BeNullOrEmpty
        ([uri]$adminRole.requested).Fragment | Should -BeNullOrEmpty
        [uri]::UnescapeDataString(([uri]$adminRole.requested).Segments[-1]) | Should -Be $Role

        Should -Invoke Invoke-RestMethod -Times 2 -Exactly
    }

    It "still answers null for a role Keycloak does not have" {
        Mock Invoke-RestMethod {
            throw [Microsoft.PowerShell.Commands.HttpResponseException]::new("not found", [System.Net.Http.HttpResponseMessage]::new([System.Net.HttpStatusCode]::NotFound))
        }

        Get_Role "cms client" | Should -BeNullOrEmpty
        Get_Realm_Admin_Role "cms client" | Should -BeNullOrEmpty
    }

    It "escapes at both lookup sites and nowhere interpolates a raw role name into a URL" {
        $text = Get-Content -Raw -LiteralPath $keycloakScript
        @([regex]::Matches($text, '/roles/\$\(\[uri\]::EscapeDataString\(\$roleName\)\)"')).Count | Should -Be 2 -Because "the realm role and the realm-management client role lookups both take the name as a segment"
        $text | Should -Not -Match '/roles/\$roleName' -Because "a raw interpolation is the shape this closes"
    }
}
