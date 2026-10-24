#Requires -Version 7.4
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot 'PackagingContract.psm1') -Force
$script:InternalSigningLeases=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)

function Get-ClashSharpInternalCertificateSnapshot {
    <#
    .SYNOPSIS
        Reads the current user's signing stores and the machine root inventory.
    .DESCRIPTION
        The full before/after sets detect unexpected concurrent changes without removing them.
    #>
    [CmdletBinding()]
    param()
    @(
        [pscustomobject]@{store='Cert:\CurrentUser\My';thumbprints=@(Get-ChildItem Cert:\CurrentUser\My | ForEach-Object Thumbprint | Sort-Object)}
        [pscustomobject]@{store='Cert:\CurrentUser\Root';thumbprints=@(Get-ChildItem Cert:\CurrentUser\Root | ForEach-Object Thumbprint | Sort-Object)}
        [pscustomobject]@{store='Cert:\LocalMachine\Root';thumbprints=@(Get-ChildItem Cert:\LocalMachine\Root | ForEach-Object Thumbprint | Sort-Object)}
    )
}

function Get-ClashSharpInternalSigningState {
    <#
    .SYNOPSIS
        Resolves a live lease token minted by this module instance.
    .DESCRIPTION
        Serialized copies and caller-created records cannot select certificate or key deletion.
    .PARAMETER Lease
        Exact in-process token returned by the creator.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)] $Lease)
    if ($null -eq $Lease.PSObject.Properties['operationId'] -or
        -not $script:InternalSigningLeases.ContainsKey([string]$Lease.operationId)) { throw 'internal-signing.unknown-lease' }
    $state=$script:InternalSigningLeases[[string]$Lease.operationId]
    if (-not [object]::ReferenceEquals($Lease,$state.token) -or
        [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -cne $state.ownerSid) { throw 'internal-signing.lease-owner' }
    return $state
}

function Add-ClashSharpInternalSigningFile {
    <#
    .SYNOPSIS
        Creates and pins one owned encrypted PFX or public certificate.
    .DESCRIPTION
        Never overwrites a path; its intended bytes are recorded before creation for exact cleanup.
    .PARAMETER State
        Private module-owned live operation state.
    .PARAMETER Name
        One of the two fixed signing filenames.
    .PARAMETER Bytes
        Encrypted PFX bytes or public DER certificate bytes.
    #>
    [CmdletBinding()]
    param($State,[string]$Name,[byte[]]$Bytes)
    if ($Name -cnotin @('ClashSharp_TemporaryKey.pfx','ClashSharp_TemporaryKey.cer')) { throw 'internal-signing.file-name' }
    $path=Join-Path $State.directory $Name
    $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()
    $entry=[pscustomobject]@{path=$path;sha256=$hash;created=$false;lease=$null}
    $State.files.Add($entry)
    $writer=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $entry.created=$true
    try { $writer.Write($Bytes);$writer.Flush($true) } finally { $writer.Dispose() }
    $entry.lease=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
}

function New-ClashSharpInternalSigningLease {
    <#
    .SYNOPSIS
        Creates two short-lived internal signing keys and narrowly owned temporary trust.
    .DESCRIPTION
        Uses fresh two-day test certificates, an encrypted MSIX PFX and a non-exportable
        Authenticode key. Requires elevation for temporary machine roots, avoiding interactive
        current-user root enrollment. Does not reuse existing keys. Cleanup owns
        only the exact in-process records; forced process termination relies on disposable CI.
    .PARAMETER Publisher
        Exact MSIX publisher from the trusted source manifest.
    .PARAMETER SigningDirectory
        Ordinary source signing directory containing only its optional tracked .gitkeep.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Publisher,[Parameter(Mandatory)][string]$SigningDirectory)
    if (-not $IsWindows) { throw 'internal-signing.windows-required' }
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if(-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'internal-signing.elevated-disposable-context-required'}
    if ([string]::IsNullOrWhiteSpace($Publisher) -or $Publisher.Length -gt 4096 -or $Publisher -cne $Publisher.Trim()) { throw 'internal-signing.publisher' }
    $directory=Assert-ClashSharpOrdinaryPath -LiteralPath $SigningDirectory -RequireDirectory
    if (@(Get-ChildItem -LiteralPath $directory -Force | Where-Object {$_.Name -cne '.gitkeep' -or $_.PSIsContainer}).Count) { throw 'internal-signing.existing-material' }
    $token=[pscustomobject]@{operationId=[Guid]::NewGuid().ToString('D')}
    $state=[pscustomobject]@{
        token=$token;ownerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;directory=$directory
        before=@(Get-ClashSharpInternalCertificateSnapshot);certificates=[Collections.Generic.List[object]]::new()
        files=[Collections.Generic.List[object]]::new();keys=[Collections.Generic.List[string]]::new()
    }
    $script:InternalSigningLeases.Add($token.operationId,$state)
    try {
        foreach ($role in @('msix','authenticode')) {
            $subject=if($role -ceq 'msix'){$Publisher}else{'CN=ClashSharp Internal Candidate '+[Guid]::NewGuid().ToString('N')}
            $keyName='clashsharp-internal-'+[Guid]::NewGuid().ToString('N')+'-'+$role
            if([Security.Cryptography.CngKey]::Exists($keyName,[Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider,[Security.Cryptography.CngKeyOpenOptions]::UserKey)){throw 'internal-signing.key-collision'}
            # Reserve a known-absent key name before enrollment can partially fail.
            $state.keys.Add($keyName)
            $parameters=@{
                Type='Custom';Subject=$subject;KeyAlgorithm='RSA';KeyLength=2048;HashAlgorithm='SHA256'
                Provider='Microsoft Software Key Storage Provider';KeyUsage='DigitalSignature'
                TextExtension=@('2.5.29.37={text}1.3.6.1.5.5.7.3.3');CertStoreLocation='Cert:\CurrentUser\My'
                FriendlyName=('ClashSharp internal '+$token.operationId+' '+$role);NotAfter=(Get-Date).AddDays(2)
                KeyExportPolicy=if($role -ceq 'msix'){'Exportable'}else{'NonExportable'}
                Container=$keyName
            }
            Write-Host ('Internal signing: creating '+$role+' certificate.')
            $certificate=New-SelfSignedCertificate @parameters
            $record=[pscustomobject]@{
                role=$role;thumbprint=$certificate.Thumbprint;subject=$certificate.Subject
                rawSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certificate.RawData)).ToLowerInvariant()
                keyName=$null;trustAttempted=$false
            }
            if (@($state.before | Where-Object { $_.thumbprints -ccontains $record.thumbprint }).Count) { throw 'internal-signing.certificate-collision' }
            $state.certificates.Add($record)
            try {
                Write-Host ('Internal signing: created '+$role+' certificate.')
                if ($certificate.Subject -cne $subject -or -not $certificate.HasPrivateKey) { throw 'internal-signing.created-identity' }
                $rsa=[Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
                try {
                    if ($rsa -isnot [Security.Cryptography.RSACng] -or $rsa.Key.IsMachineKey) { throw 'internal-signing.key-provider' }
                    $record.keyName=$rsa.Key.KeyName
                    if($record.keyName -cne $keyName){throw 'internal-signing.key-identity'}
                } finally { if($null -ne $rsa){$rsa.Dispose()} }
                Write-Host ('Internal signing: resolved '+$role+' key ownership.')
                $rootStore=[Security.Cryptography.X509Certificates.X509Store]::new('Root','LocalMachine')
                $public=[Security.Cryptography.X509Certificates.X509Certificate2]::new($certificate.RawData)
                try {
                    $rootStore.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
                    $record.trustAttempted=$true
                    $rootStore.Add($public)
                } finally { $public.Dispose();$rootStore.Dispose() }
                Write-Host ('Internal signing: added owned '+$role+' temporary root.')
                if ($role -ceq 'msix') {
                    $password=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
                    $pfx=$null
                    try {
                        $pfx=$certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx,$password)
                        Add-ClashSharpInternalSigningFile -State $state -Name 'ClashSharp_TemporaryKey.pfx' -Bytes $pfx
                    } finally { $password=$null;if($null -ne $pfx){[Array]::Clear($pfx,0,$pfx.Length)} }
                    Add-ClashSharpInternalSigningFile -State $state -Name 'ClashSharp_TemporaryKey.cer' -Bytes $certificate.RawData
                }
            } finally { $certificate.Dispose() }
        }
        return $token
    } catch {
        $original=$_
        $null=Close-ClashSharpInternalSigningLease -Lease $token
        throw $original
    }
}

function Get-ClashSharpInternalSigningLeaseInfo {
    <#
    .SYNOPSIS
        Returns public signing references from a live private lease.
    .DESCRIPTION
        Does not return a password, private key, cleanup records or mutable deletion authority.
    .PARAMETER Lease
        Exact in-process token returned by the creator.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Lease)
    $state=Get-ClashSharpInternalSigningState -Lease $Lease
    $msix=@($state.certificates | Where-Object role -CEQ 'msix')
    $auth=@($state.certificates | Where-Object role -CEQ 'authenticode')
    if($msix.Count -ne 1 -or $auth.Count -ne 1){throw 'internal-signing.incomplete-lease'}
    [pscustomobject]@{scope='internal-self-signed';msixThumbprint=$msix[0].thumbprint;authenticodeThumbprint=$auth[0].thumbprint;authenticodeSubject=$auth[0].subject;publicReleaseAuthorized=$false}
}

function Close-ClashSharpInternalSigningLease {
    <#
    .SYNOPSIS
        Removes only the exact files, public trust and private keys owned by a live lease.
    .DESCRIPTION
        Verifies raw certificate identity, preexisting sets and file hashes. Refuses changed
        objects, serialized tokens or unknown keys. Confirms CNG key deletion and original
        store inventories before releasing the token; failures remain observable.
    .PARAMETER Lease
        Exact in-process token returned by the creator, never deserialized ownership metadata.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Lease)
    $state=Get-ClashSharpInternalSigningState -Lease $Lease
    $errors=[Collections.Generic.List[string]]::new()
    foreach($file in $state.files){
        try {
            if($null -ne $file.lease){$file.lease.Dispose();$file.lease=$null}
            if($file.created -and (Test-Path -LiteralPath $file.path)){
                $path=Assert-ClashSharpOrdinaryPath -LiteralPath $file.path -RequireFile
                if((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $file.sha256){throw 'internal-signing.file-changed'}
                Remove-Item -LiteralPath $path -Force
            }
        }catch{$errors.Add('internal-signing.file-cleanup')}
    }
    foreach($record in $state.certificates){
        foreach($store in @('LocalMachine\Root','CurrentUser\My')){
            try {
                $path='Cert:\'+$store+'\'+$record.thumbprint
                if(Test-Path -LiteralPath $path){
                    $certificate=Get-Item -LiteralPath $path
                    try{
                        $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($certificate.RawData)).ToLowerInvariant()
                        if($hash -cne $record.rawSha256 -or @($state.before|Where-Object {$_.thumbprints -ccontains $record.thumbprint}).Count){throw 'internal-signing.certificate-changed'}
                    }finally{$certificate.Dispose()}
                    Remove-Item -Path $path -Confirm:$false
                }
                if(Test-Path -LiteralPath $path){throw 'internal-signing.certificate-remains'}
            }catch{$errors.Add('internal-signing.certificate-cleanup')}
        }
    }
    $keysDeleted=0
    foreach($keyName in $state.keys){
        try{
            $provider=[Security.Cryptography.CngProvider]::MicrosoftSoftwareKeyStorageProvider
            if([Security.Cryptography.CngKey]::Exists($keyName,$provider,[Security.Cryptography.CngKeyOpenOptions]::UserKey)){
                $key=[Security.Cryptography.CngKey]::Open($keyName,$provider,([Security.Cryptography.CngKeyOpenOptions]::UserKey -bor [Security.Cryptography.CngKeyOpenOptions]::Silent))
                try{$key.Delete();$keysDeleted++}finally{$key.Dispose()}
            }
            if([Security.Cryptography.CngKey]::Exists($keyName,$provider,[Security.Cryptography.CngKeyOpenOptions]::UserKey)){throw 'internal-signing.private-key-remains'}
        }catch{$errors.Add('internal-signing.private-key-cleanup')}
    }
    foreach($before in $state.before){
        $after=@(Get-ChildItem -LiteralPath $before.store|ForEach-Object Thumbprint|Sort-Object)
        if(($after -join ',') -cne ($before.thumbprints -join ',')){$errors.Add('internal-signing.store-changed')}
    }
    if($errors.Count){throw ('internal-signing.cleanup-unconfirmed:'+($errors -join ','))}
    $null=$script:InternalSigningLeases.Remove($Lease.operationId)
    [pscustomobject]@{success=$true;certificatesRemoved=$state.certificates.Count;privateKeysDeleted=$keysDeleted;ownedKeySlotsConfirmedAbsent=$state.keys.Count;signingFilesRemoved=$state.files.Count;originalCurrentUserStoresPreserved=$true;originalMachineTrustPreserved=$true;temporaryMachineRootsRemoved=$true}
}

Export-ModuleMember -Function New-ClashSharpInternalSigningLease, Get-ClashSharpInternalSigningLeaseInfo, Close-ClashSharpInternalSigningLease
