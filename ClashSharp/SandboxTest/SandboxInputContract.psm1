Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-SandboxObject {
    <#
    .SYNOPSIS
        Requires an exact JSON object shape.
    .DESCRIPTION
        Rejects non-objects, missing properties, additional properties, and incorrectly cased names.
    .PARAMETER Value
        Deserialized object to inspect.
    .PARAMETER Names
        Complete case-sensitive property allowlist.
    #>
    param([object]$Value, [string[]]$Names)
    if ($Value -isnot [System.Management.Automation.PSCustomObject]) {
        throw 'sandbox.object.invalid'
    }
    $actual = @($Value.PSObject.Properties.Name)
    if ($actual.Count -ne $Names.Count) { throw 'sandbox.object.shape' }
    foreach ($name in $Names) {
        if ($actual -cnotcontains $name) { throw 'sandbox.object.shape' }
    }
}

function Get-SandboxFileSha256 {
    <#
    .SYNOPSIS
        Returns the lowercase SHA-256 of an existing literal file.
    .DESCRIPTION
        Streams a read-only shared lease through SHA-256 and always disposes both resources.
    .PARAMETER LiteralPath
        File to read without allowing concurrent writes.
    #>
    param([Parameter(Mandatory)][string]$LiteralPath)
    $stream = [IO.File]::Open($LiteralPath, 'Open', 'Read', 'Read')
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose(); $stream.Dispose() }
}

function Assert-SandboxStringFields {
    <#
    .SYNOPSIS
        Rejects scalar coercion and array comparisons for required JSON strings.
    .DESCRIPTION
        Requires every named property to be a nonempty string before any value comparison.
    .PARAMETER Value
        Object containing required fields.
    .PARAMETER Names
        Fields that must be nonempty strings.
    #>
    param([object]$Value, [string[]]$Names)
    foreach ($name in $Names) {
        if ($Value.$name -isnot [string] -or [string]::IsNullOrWhiteSpace($Value.$name)) {
            throw 'sandbox.string.required'
        }
    }
}

function Read-SandboxJson {
    <#
    .SYNOPSIS
        Reads a bounded, complete UTF-8 JSON object.
    .DESCRIPTION
        Rejects empty, oversized or invalid JSON while preserving timestamp strings across PowerShell
        editions. The input stream and text reader are always disposed.
    .PARAMETER LiteralPath
        Exact input or terminal report path.
    #>
    param([Parameter(Mandatory)][string]$LiteralPath)
    $stream = [IO.File]::Open($LiteralPath, 'Open', 'Read', 'Read')
    try {
        if ($stream.Length -lt 2 -or $stream.Length -gt 1048576) { throw 'sandbox.json.length' }
        $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $true)
        try {
            $options = @{ ErrorAction = 'Stop' }
            if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { $options.DateKind = 'String' }
            return ($reader.ReadToEnd() | ConvertFrom-Json @options)
        }
        finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

function Assert-SandboxPlan {
    <#
    .SYNOPSIS
        Validates the complete immutable guest plan and candidate file allowlist.
    .DESCRIPTION
        Requires supported version, scenario, identifiers, package identity, executable digest,
        certificate thumbprint, and exactly four canonical file roles with typed sizes and hashes.
    .PARAMETER Plan
        Parsed schema version 2 plan.
    #>
    param([Parameter(Mandatory)][object]$Plan)
    Assert-SandboxObject $Plan @('schemaVersion', 'scenario', 'runId', 'sandboxId', 'host', 'candidate')
    Assert-SandboxObject $Plan.host @('computerName', 'machineId')
    Assert-SandboxObject $Plan.candidate @('packageName', 'fullName', 'familyName', 'version',
        'applicationId', 'executable', 'executableSha256', 'certificateThumbprint', 'files')
    if (($Plan.schemaVersion -isnot [int] -and $Plan.schemaVersion -isnot [long]) -or
        $Plan.schemaVersion -ne 2 -or
        $Plan.scenario -cnotin @('install-only', 'launch-no-proxy') -or
        $Plan.runId -isnot [string] -or $Plan.runId -cnotmatch '^[0-9a-f]{32}$' -or
        $Plan.sandboxId -isnot [string] -or
        $Plan.sandboxId -cnotmatch '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$' -or
        $Plan.host.computerName -isnot [string] -or
        $Plan.host.computerName -notmatch '^[a-z0-9-]{1,63}$' -or
        $Plan.host.machineId -isnot [string] -or
        $Plan.host.machineId -notmatch '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$') {
        throw 'sandbox.plan.identity'
    }
    $candidate = $Plan.candidate
    Assert-SandboxStringFields $candidate @('packageName', 'fullName', 'familyName', 'version',
        'applicationId', 'executable', 'executableSha256', 'certificateThumbprint')
    $packageName = '67dc1dc3-13fd-46c5-84f4-2932d94b566f'
    if ($candidate.packageName -cne $packageName -or $candidate.version -cne '1.0.0.0' -or
        $candidate.familyName -cnotmatch "^$($packageName)_[a-z0-9]{13}$" -or
        $candidate.fullName -cne ("$($packageName)_1.0.0.0_x64__" + $candidate.familyName.Substring($packageName.Length + 1)) -or
        $candidate.applicationId -cne 'App' -or $candidate.executable -cne 'ClashSharp.exe' -or
        $candidate.executableSha256 -isnot [string] -or
        $candidate.executableSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $candidate.certificateThumbprint -isnot [string] -or
        $candidate.certificateThumbprint -cnotmatch '^[0-9A-F]{40}$' -or
        $candidate.files -isnot [array] -or $candidate.files.Count -ne 4) {
        throw 'sandbox.plan.candidate'
    }
    $paths = @{
        package = 'ClashSharp_1.0.0.0_x64.msix'
        certificate = 'ClashSharp_TemporaryKey.cer'
        provenance = 'payload-provenance.json'
        dependency = 'Dependencies/x64/Microsoft.WindowsAppRuntime.1.8.msix'
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($file in $candidate.files) {
        Assert-SandboxObject $file @('role', 'path', 'length', 'sha256')
        Assert-SandboxStringFields $file @('role', 'path', 'sha256')
        if ($file.role -isnot [string] -or @($paths.Keys) -cnotcontains $file.role -or
            -not $seen.Add($file.role) -or $file.path -cne $paths[$file.role] -or
            ($file.length -isnot [int] -and $file.length -isnot [long]) -or
            $file.length -lt 1 -or $file.length -gt 1073741824 -or
            $file.sha256 -isnot [string] -or $file.sha256 -cnotmatch '^[0-9a-f]{64}$') {
            throw 'sandbox.plan.file'
        }
    }
}

function Assert-SandboxPayload {
    <#
    .SYNOPSIS
        Rechecks every immutable candidate file against the selected plan.
    .DESCRIPTION
        Requires all four files to exist as non-reparse files with the expected size and digest.
    .PARAMETER Plan
        Validated guest plan.
    .PARAMETER Root
        Exact payload directory to read.
    #>
    param([Parameter(Mandatory)][object]$Plan, [Parameter(Mandatory)][string]$Root)
    Assert-SandboxPlan $Plan
    foreach ($file in $Plan.candidate.files) {
        $path = Join-Path $Root $file.path
        $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $item.Length -ne $file.length -or (Get-SandboxFileSha256 $path) -cne $file.sha256) {
            throw 'sandbox.payload.changed'
        }
    }
}

function Assert-SandboxGuestIdentity {
    <#
    .SYNOPSIS
        Rejects host execution before any guest mutation is admitted.
    .DESCRIPTION
        Uses registry evidence because CIM is unavailable in some Sandbox images. This is an
        accidental-host-execution guard, not attestation against a malicious guest administrator.
    .PARAMETER Plan
        Validated host plan.
    .PARAMETER Evidence
        Read-only machine, account, path, and architecture observations.
    #>
    param([Parameter(Mandatory)][object]$Plan, [Parameter(Mandatory)][object]$Evidence)
    Assert-SandboxPlan $Plan
    Assert-SandboxObject $Evidence @('computerName', 'machineId', 'userName', 'profile',
        'productType', 'osBuild', 'architecture', 'scriptPath')
    Assert-SandboxStringFields $Evidence @('computerName', 'machineId', 'userName', 'profile',
        'productType', 'osBuild', 'architecture', 'scriptPath')
    if ($Evidence.computerName -isnot [string] -or
        $Evidence.computerName -notmatch '^[a-z0-9-]{1,63}$' -or
        $Evidence.computerName -ieq $Plan.host.computerName -or
        $Evidence.machineId -isnot [string] -or
        $Evidence.machineId -notmatch '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$' -or
        $Evidence.machineId -ieq $Plan.host.machineId -or
        $Evidence.userName -cne 'WDAGUtilityAccount' -or
        $Evidence.profile -ine 'C:\Users\WDAGUtilityAccount' -or
        $Evidence.productType -cne 'WinNT' -or
        $Evidence.osBuild -isnot [string] -or $Evidence.osBuild -cnotmatch '^[0-9]{5}$' -or
        [int]$Evidence.osBuild -lt 22000 -or $Evidence.architecture -cne 'AMD64' -or
        $Evidence.scriptPath -ine 'C:\ClashSharpTestInput\Run-InSandbox.ps1') {
        throw 'sandbox.guest.required'
    }
}

function Get-SandboxProxyFingerprint {
    <#
    .SYNOPSIS
        Reads a digest of current-user WinINet proxy values without disclosing their contents.
    .DESCRIPTION
        Includes missing values, registry kinds, PAC, bypass, and saved connection settings.
        This function never writes registry values or refreshes system proxy settings.
    #>
    param()
    $snapshot = [ordered]@{}
    foreach ($subkey in @('', '\Connections')) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey(
            'Software\Microsoft\Windows\CurrentVersion\Internet Settings' + $subkey, $false)
        try {
            $names = @('ProxyEnable', 'ProxyServer', 'ProxyOverride', 'AutoConfigURL')
            if ($subkey) { $names = @('DefaultConnectionSettings', 'SavedLegacySettings') }
            foreach ($name in $names) {
                $exists = $null -ne $key -and @($key.GetValueNames()) -contains $name
                $value = $null
                $kind = $null
                if ($exists) {
                    $value = $key.GetValue($name, $null,
                        [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                    $kind = [string]$key.GetValueKind($name)
                    if ($value -is [byte[]]) { $value = [Convert]::ToBase64String($value) }
                }
                $snapshot[$subkey + '\' + $name] = [ordered]@{ exists = $exists; kind = $kind; value = $value }
            }
        } finally { if ($null -ne $key) { $key.Dispose() } }
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes(($snapshot | ConvertTo-Json -Depth 6 -Compress))
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($hash.ComputeHash($bytes))).Replace('-', '').ToLowerInvariant() }
    finally { $hash.Dispose() }
}

Export-ModuleMember -Function Assert-SandboxObject, Get-SandboxFileSha256, Read-SandboxJson,
    Assert-SandboxPlan, Assert-SandboxPayload, Assert-SandboxGuestIdentity, Get-SandboxProxyFingerprint,
    Assert-SandboxStringFields
