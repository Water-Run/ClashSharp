#Requires -Version 7.0
<#
.SYNOPSIS
    Exercises the offline third-party notices pipeline with isolated package fixtures.
.DESCRIPTION
    Uses synthetic packages and a read-only copy of one already restored signed NuGet package.
    Tests ZIP bytes, lock binding, signature content integrity, source supplements, deterministic
    output, and final MSIX content verification. Does not restore, install, sign, modify package
    caches, or change certificates.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Import-Module -Name (Join-Path $PSScriptRoot 'ThirdPartyNotices.psm1') -Force
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$testParent = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts/installer/third-party-contract-tests'))
$runId = [Guid]::NewGuid().ToString('N')
$testRoot = Join-Path $testParent $runId
$null = New-Item -ItemType Directory -Path $testRoot -Force
$script:assertions = 0
$script:scenarios = [Collections.Generic.List[string]]::new()
$utf8 = [Text.UTF8Encoding]::new($false)

function Get-TestBomSchema {
    <#
    .SYNOPSIS
        Loads the pinned CycloneDX standard and its offline reference schemas.
    .DESCRIPTION
        Keeps upstream files unchanged and bundles their references only in memory, preventing
        schema validation from using the network or a moving upstream license enumeration.
    #>
    $root = Join-Path $PSScriptRoot 'Schemas/CycloneDX-1.6'
    $pins = @{
        'bom-1.6.schema.json' = '3E92DDDBC30CF7F6A02B80F0942B1A4CFD4FB1C26F1DFC4310AFA9D613CAFB93'
        'spdx.schema.json' = 'BAA9D3BD1ED57B6751B0887EDEAD6B5063FF53FF7429CF85D476C6C94AF0166E'
        'jsf-0.82.schema.json' = '8BAE002C25E723DB7EE1F26AFDE680AE1A2B1A8F6B4B4B0FD65DC3BECB090AAE'
    }
    foreach ($name in $pins.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $root $name) -Algorithm SHA256).Hash -cne $pins[$name]) {
            throw 'A pinned CycloneDX schema differs from its reviewed upstream bytes.'
        }
    }
    $schema = Get-Content -LiteralPath (Join-Path $root 'bom-1.6.schema.json') -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $spdx = Get-Content -LiteralPath (Join-Path $root 'spdx.schema.json') -Raw | ConvertFrom-Json -AsHashtable -Depth 100
    $jsfText = Get-Content -LiteralPath (Join-Path $root 'jsf-0.82.schema.json') -Raw
    $jsf = $jsfText.Replace('"#/definitions/', '"#/definitions/__clashsharp_jsf/definitions/') | ConvertFrom-Json -AsHashtable -Depth 100
    $null = $spdx.Remove('$id')
    $null = $jsf.Remove('$id')
    $schema.definitions['__clashsharp_spdx'] = $spdx
    $schema.definitions['__clashsharp_jsf'] = $jsf
    $combined = $schema | ConvertTo-Json -Depth 100
    return $combined.Replace('"spdx.schema.json"', '"#/definitions/__clashsharp_spdx"').Replace('"jsf-0.82.schema.json#/definitions/signature"', '"#/definitions/__clashsharp_jsf/definitions/signature"')
}

function Assert-TestCondition {
    <#
    .SYNOPSIS
        Records an independently observed contract assertion.
    .DESCRIPTION
        Stops the test run immediately if its stated condition is false.
    .PARAMETER Condition
        Observed condition.
    .PARAMETER Message
        Non-sensitive assertion description.
    #>
    param([bool] $Condition, [string] $Message)
    if (-not $Condition) { throw $Message }
    $script:assertions++
}

function Write-TestJson {
    <#
    .SYNOPSIS
        Writes a synthetic fixture document.
    .DESCRIPTION
        Creates only ordinary files beneath the current test scope.
    .PARAMETER Path
        Fixture destination.
    .PARAMETER Value
        Serializable fixture value.
    #>
    param([string] $Path, $Value)
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 20), $utf8)
}

function Write-TestZip {
    <#
    .SYNOPSIS
        Creates an exact synthetic archive.
    .DESCRIPTION
        Retains deliberately invalid entry names and duplicates for rejection tests.
    .PARAMETER Path
        Fixture archive destination.
    .PARAMETER Entries
        Ordered objects with Name and byte-array Bytes properties.
    #>
    param([string] $Path, [object[]] $Entries)
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $Path) -Force
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    $zip = $null
    try {
        $zip = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
        foreach ($item in $Entries) {
            $entry = $zip.CreateEntry($item.Name)
            $entry.LastWriteTime = [DateTimeOffset]::new(2020, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $target = $entry.Open()
            try { $target.Write($item.Bytes, 0, $item.Bytes.Length) } finally { $target.Dispose() }
        }
    } finally {
        if ($null -ne $zip) { $zip.Dispose() }
        $stream.Dispose()
    }
}

function Read-TestZip {
    <#
    .SYNOPSIS
        Reads fixture entries without extracting their paths.
    .DESCRIPTION
        Returns small in-memory documents that rejection cases can alter independently.
    .PARAMETER Path
        Fixture archive path.
    #>
    param([string] $Path)
    $zip = [IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach ($entry in $zip.Entries) {
            $source = $entry.Open()
            $memory = [IO.MemoryStream]::new()
            try {
                $source.CopyTo($memory)
                [pscustomobject]@{ Name = $entry.FullName; Bytes = $memory.ToArray() }
            } finally { $source.Dispose(); $memory.Dispose() }
        }
    } finally { $zip.Dispose() }
}

function New-TestFixture {
    <#
    .SYNOPSIS
        Creates a minimal four-project repository and two independent package inputs.
    .DESCRIPTION
        Every input is synthetic except the repository SDK version. Hashes are calculated from
        fixture bytes independently of the production generator.
    .PARAMETER Name
        Unique scenario directory name.
    #>
    param([string] $Name)
    $root = Join-Path $testRoot $Name
    $cache = Join-Path $root 'cache'
    $catalogRoot = Join-Path $root 'ClashSharp/Installer/ThirdParty'
    $null = New-Item -ItemType Directory -Path $catalogRoot -Force
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'global.json') -Destination (Join-Path $root 'global.json')
    $hashes = @{}
    foreach ($id in @('Fixture.Library', 'Fixture.Runtime')) {
        $key = $id.ToLowerInvariant()
        $archivePath = Join-Path $cache ($key + '/1.0.0/' + $key + '.1.0.0.nupkg')
        $metadata = '<package><metadata><id>' + $id + '</id><version>1.0.0</version><authors>Fixture</authors><license type="expression">MIT</license></metadata></package>'
        Write-TestZip -Path $archivePath -Entries @(
            [pscustomobject]@{ Name = $key + '.nuspec'; Bytes = $utf8.GetBytes($metadata) }
            [pscustomobject]@{ Name = 'LICENSE.txt'; Bytes = $utf8.GetBytes('Synthetic fixture license.') }
        )
        $hashes[$id] = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $archivePath -Algorithm SHA512).Hash))
    }
    foreach ($project in @('ClashSharp', 'ClashSharp.MihomoService', 'ClashSharp.RecoveryWatchdog', 'ClashSharp.Installer')) {
        $projectRoot = Join-Path $root ('ClashSharp/' + $project)
        Write-TestJson -Path (Join-Path $projectRoot 'packages.lock.json') -Value @{
            version = 1; dependencies = @{ net10 = @{ 'Fixture.Library' = @{ type = 'Direct'; resolved = '1.0.0'; contentHash = $hashes['Fixture.Library'] } } }
        }
        Write-TestJson -Path (Join-Path $projectRoot 'obj/project.assets.json') -Value @{
            version = 3; packageFolders = @{ $cache = @{} }
            libraries = @{ 'Fixture.Library/1.0.0' = @{ type = 'package'; path = 'fixture.library/1.0.0'; sha512 = $hashes['Fixture.Library'] } }
            project = @{ restore = @{ projectName = $project }; frameworks = @{ net10 = @{
                downloadDependencies = @(@{ name = 'Fixture.Runtime'; version = '[1.0.0, 1.0.0]' })
            } } }
        }
    }
    $source = $utf8.GetBytes('Synthetic reviewed source license.')
    [IO.File]::WriteAllBytes((Join-Path $catalogRoot 'source-license.txt'), $source)
    $sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($source)).ToLowerInvariant()
    Write-TestJson -Path (Join-Path $catalogRoot 'catalog.json') -Value @{
        schemaVersion = 1; productVersion = '1.0.0'
        documents = @(@{ file = 'source-license.txt'; sourceUrl = 'https://example.invalid/pinned-source'; length = $source.Length; sha256 = $sourceHash })
        packageLicenseSupplements = @(@{ id = 'Fixture.Library'; version = '1.0.0'; licenseType = 'expression'; licenseValue = 'MIT'; document = 'source-license.txt' })
        downloadDependencies = @(@{ id = 'Fixture.Runtime'; version = '1.0.0'; sha512 = $hashes['Fixture.Runtime'] })
        geoDataEvidence = @{ dataCommit = ('a' * 40); sourceCommit = ('b' * 40); documents = @('source-license.txt'); upstreamInputRevisionsRecorded = $false }
    }
    Write-TestJson -Path (Join-Path $root 'ClashSharp/Installer/release-inputs.json') -Value @{
        geoData = @{ commit = ('a' * 40); sourceCommit = ('b' * 40); files = @(
            @('ASN.mmdb', 'Country.mmdb', 'GeoIP.dat', 'GeoSite.dat') | ForEach-Object {
                @{ name = $_; length = 1; sha256 = ('c' * 64) }
            }
        ) }
    }
    foreach ($path in @('LICENSE', 'ClashSharp/ClashSharp/Binaries/mihomo-LICENSE.txt',
            'ClashSharp/ClashSharp/Binaries/mihomo-NOTICE.txt', 'ClashSharp/ClashSharp/Binaries/mihomo-manifest.json')) {
        $target = Join-Path $root $path
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force
        [IO.File]::WriteAllText($target, 'Synthetic bundled component evidence.', $utf8)
    }
    Write-TestJson -Path (Join-Path $root 'ClashSharp/ClashSharp/Binaries/mihomo-manifest.json') -Value @{
        schemaVersion = 1; version = 'v1.2.3'; length = 1; sha256 = ('d' * 64)
        sourceReleaseUrl = 'https://github.com/MetaCubeX/mihomo/releases/tag/v1.2.3'
    }
    return $root
}

function Update-TestPackageHash {
    <#
    .SYNOPSIS
        Rebinds synthetic locks after an intentional valid ZIP replacement.
    .DESCRIPTION
        Allows tests to reach metadata validation instead of failing at the earlier hash check.
    .PARAMETER Root
        Fixture repository.
    #>
    param([string] $Root)
    $archive = Join-Path $Root 'cache/fixture.library/1.0.0/fixture.library.1.0.0.nupkg'
    $hash = [Convert]::ToBase64String([Convert]::FromHexString((Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash))
    foreach ($project in @('ClashSharp', 'ClashSharp.MihomoService', 'ClashSharp.RecoveryWatchdog', 'ClashSharp.Installer')) {
        $projectRoot = Join-Path $Root ('ClashSharp/' + $project)
        $lockPath = Join-Path $projectRoot 'packages.lock.json'
        $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
        $lock.dependencies.net10.'Fixture.Library'.contentHash = $hash
        Write-TestJson -Path $lockPath -Value $lock
        $assetsPath = Join-Path $projectRoot 'obj/project.assets.json'
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
        $assets.libraries.'Fixture.Library/1.0.0'.sha512 = $hash
        Write-TestJson -Path $assetsPath -Value $assets
    }
}

function Assert-TestRejects {
    <#
    .SYNOPSIS
        Confirms a malformed fixture fails.
    .DESCRIPTION
        Counts a rejection only when the production boundary throws an exception.
    .PARAMETER Name
        Scenario name.
    .PARAMETER Action
        Operation expected to fail.
    #>
    param([string] $Name, [scriptblock] $Action)
    $rejected = $false
    try { $null = & $Action } catch { $rejected = $true }
    Assert-TestCondition -Condition $rejected -Message ("Accepted invalid fixture: " + $Name)
    $script:scenarios.Add($Name)
}

try {
    $validRoot = New-TestFixture -Name 'valid'
    $validPath = Join-Path $validRoot 'notices.zip'
    $first = New-ClashSharpThirdPartyNotices -RepositoryRoot $validRoot -OutputPath $validPath
    $verified = Get-ClashSharpThirdPartyNoticesContract -LiteralPath $validPath
    Assert-TestCondition ($verified.PackageCount -eq 2 -and $verified.Sha256 -ceq $first.Sha256) 'The valid two-package inventory was not preserved.'
    Assert-TestCondition ($verified.BomComponentCount -eq 7 -and $verified.BomSha256 -cmatch '^[0-9a-f]{64}$') 'The BOM must include two NuGet packages, mihomo and four data assets.'
    Assert-TestCondition ($first.BomComponentCount -eq $verified.BomComponentCount -and $first.BomSha256 -ceq $verified.BomSha256 -and $first.DocumentCount -eq $verified.DocumentCount) 'Producer and archive verifier must agree on BOM identity and original document count.'
    $second = New-ClashSharpThirdPartyNotices -RepositoryRoot $validRoot -OutputPath (Join-Path $validRoot 'again.zip')
    Assert-TestCondition ($first.Sha256 -ceq $second.Sha256) 'Identical inputs did not produce identical archive bytes.'
    $entries = @(Read-TestZip -Path $validPath)
    $indexEntry = @($entries | Where-Object Name -CEQ 'inventory.json')[0]
    $bomEntry = @($entries | Where-Object Name -CEQ 'bom.cdx.json')[0]
    $bomSchema = Get-TestBomSchema
    $bomText = $utf8.GetString($bomEntry.Bytes)
    Assert-TestCondition (Test-Json -Json $bomText -Schema $bomSchema) 'The generated BOM does not conform to the complete offline CycloneDX schema.'
    $invalidBom = $bomText | ConvertFrom-Json -AsHashtable
    $invalidBom.components[0].type = 'not-a-cyclonedx-component'
    $invalidBomText = $invalidBom | ConvertTo-Json -Depth 24
    Assert-TestCondition (-not (Test-Json -Json $invalidBomText -Schema $bomSchema -ErrorAction SilentlyContinue)) 'The standard schema accepted an invalid component kind.'
    $script:scenarios.Add('offline-cyclonedx-schema')
    $index = $utf8.GetString($indexEntry.Bytes) | ConvertFrom-Json -AsHashtable
    Assert-TestCondition (@($index.packages | Where-Object { $_.projects.Count -ne 4 }).Count -eq 0) 'A production entry point was omitted.'
    Assert-TestCondition (-not $utf8.GetString($indexEntry.Bytes).Contains($testRoot)) 'Inventory exposed a machine-specific restore path.'
    Assert-TestCondition (-not $index.geoData.upstreamInputRevisionsRecorded) 'Unproven upstream input history was incorrectly claimed.'
    $msixPath = Join-Path $validRoot 'package.msix'
    $noticeBytes = [IO.File]::ReadAllBytes($validPath)
    Write-TestZip -Path $msixPath -Entries @([pscustomobject]@{ Name = 'ThirdParty/THIRD-PARTY-NOTICES.zip'; Bytes = $noticeBytes })
    $embedded = Assert-ClashSharpMsixThirdPartyNotices -LiteralPath $msixPath -ExpectedContract $verified
    Assert-TestCondition ($embedded.Sha256 -ceq $first.Sha256) 'MSIX embedded archive did not match its build input.'
    $script:scenarios.Add('valid-deterministic-four-project-archive-and-msix')
    Assert-TestRejects 'existing-output-preserved' { New-ClashSharpThirdPartyNotices -RepositoryRoot $validRoot -OutputPath $validPath }
    Assert-TestCondition ((Get-FileHash -LiteralPath $validPath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $first.Sha256) 'An existing output was changed.'

    foreach ($name in @('missing-inventory', 'missing-readme', 'missing-bom', 'changed-bom', 'changed-bom-hash', 'missing-document', 'corrupt-document',
            'duplicate-entry', 'case-collision', 'traversal-entry', 'unreferenced-document', 'bad-reference')) {
        $altered = @($entries | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Bytes = [byte[]]$_.Bytes.Clone() } })
        $documentName = @($altered | Where-Object { $_.Name.StartsWith('documents/') })[0].Name
        switch ($name) {
            'missing-inventory' { $altered = @($altered | Where-Object Name -CNE 'inventory.json') }
            'missing-readme' { $altered = @($altered | Where-Object Name -CNE 'README.txt') }
            'missing-bom' { $altered = @($altered | Where-Object Name -CNE 'bom.cdx.json') }
            'changed-bom' { @($altered | Where-Object Name -CEQ 'bom.cdx.json')[0].Bytes = $utf8.GetBytes('{}') }
            'changed-bom-hash' {
                $entry = @($altered | Where-Object Name -CEQ 'bom.cdx.json')[0]
                $bom = $utf8.GetString($entry.Bytes) | ConvertFrom-Json -AsHashtable
                $bom.components[0].hashes[0].content = ('f' * 64)
                $entry.Bytes = $utf8.GetBytes(($bom | ConvertTo-Json -Depth 24) + [char]10)
            }
            'missing-document' { $altered = @($altered | Where-Object Name -CNE $documentName) }
            'corrupt-document' { @($altered | Where-Object Name -CEQ $documentName)[0].Bytes = $utf8.GetBytes('changed') }
            'duplicate-entry' { $altered += $altered[0] }
            'case-collision' { $altered += [pscustomobject]@{ Name = 'INVENTORY.JSON'; Bytes = $indexEntry.Bytes } }
            'traversal-entry' { $altered += [pscustomobject]@{ Name = '../outside.txt'; Bytes = $utf8.GetBytes('outside') } }
            'unreferenced-document' {
                $bytes = $utf8.GetBytes('unreferenced')
                $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
                $altered += [pscustomobject]@{ Name = 'documents/' + $hash + '.txt'; Bytes = $bytes }
            }
            'bad-reference' {
                $badIndex = $utf8.GetString($indexEntry.Bytes) | ConvertFrom-Json -AsHashtable
                $badIndex.packages[0].documents[0].length++
                @($altered | Where-Object Name -CEQ 'inventory.json')[0].Bytes = $utf8.GetBytes(($badIndex | ConvertTo-Json -Depth 20))
            }
        }
        $path = Join-Path $validRoot ($name + '.zip')
        Write-TestZip -Path $path -Entries $altered
        Assert-TestRejects $name { Get-ClashSharpThirdPartyNoticesContract -LiteralPath $path }
    }
    foreach ($name in @('missing-msix-notices', 'case-msix-notices', 'duplicate-msix-notices', 'changed-msix-notices')) {
        $items = @([pscustomobject]@{ Name = 'ThirdParty/THIRD-PARTY-NOTICES.zip'; Bytes = $noticeBytes })
        switch ($name) {
            'missing-msix-notices' { $items[0].Name = 'Other/file.txt' }
            'case-msix-notices' { $items[0].Name = 'thirdparty/third-party-notices.zip' }
            'duplicate-msix-notices' { $items += [pscustomobject]@{ Name = 'thirdparty/third-party-notices.zip'; Bytes = $noticeBytes } }
            'changed-msix-notices' { $items[0].Bytes = [byte[]]$noticeBytes.Clone(); $items[0].Bytes[10] = $items[0].Bytes[10] -bxor 1 }
        }
        $path = Join-Path $validRoot ($name + '.msix')
        Write-TestZip -Path $path -Entries $items
        Assert-TestRejects $name { Assert-ClashSharpMsixThirdPartyNotices -LiteralPath $path -ExpectedContract $verified }
    }
    foreach ($name in @('changed-package', 'missing-package', 'duplicate-package-entry', 'wrong-package-identity',
            'missing-license', 'oversized-license', 'lock-drift', 'unsafe-library-path', 'sdk-unpinned',
            'sdk-range', 'source-drift', 'source-path', 'geodata-drift')) {
        $root = New-TestFixture -Name $name
        $packagePath = Join-Path $root 'cache/fixture.library/1.0.0/fixture.library.1.0.0.nupkg'
        $packageEntries = @(Read-TestZip -Path $packagePath)
        $assetsPath = Join-Path $root 'ClashSharp/ClashSharp/obj/project.assets.json'
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
        $catalogPath = Join-Path $root 'ClashSharp/Installer/ThirdParty/catalog.json'
        $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json -AsHashtable
        switch ($name) {
            'changed-package' { $packageEntries[1].Bytes = $utf8.GetBytes('changed'); Write-TestZip $packagePath $packageEntries }
            'missing-package' { Remove-Item -LiteralPath $packagePath }
            'duplicate-package-entry' { Write-TestZip $packagePath ($packageEntries + $packageEntries[1]); Update-TestPackageHash $root }
            'wrong-package-identity' {
                $packageEntries[0].Bytes = $utf8.GetBytes($utf8.GetString($packageEntries[0].Bytes).Replace('Fixture.Library', 'Fixture.Other'))
                Write-TestZip $packagePath $packageEntries
                Update-TestPackageHash $root
            }
            'missing-license' {
                $packageEntries[0].Bytes = $utf8.GetBytes($utf8.GetString($packageEntries[0].Bytes).Replace('<license type="expression">MIT</license>', '<license type="file">absent.txt</license>'))
                Write-TestZip $packagePath $packageEntries
                Update-TestPackageHash $root
            }
            'oversized-license' { $packageEntries[1].Bytes = [byte[]]::new(1048577); Write-TestZip $packagePath $packageEntries; Update-TestPackageHash $root }
            'lock-drift' {
                $lockPath = Join-Path $root 'ClashSharp/ClashSharp/packages.lock.json'
                $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
                $lock.dependencies.net10.'Fixture.Library'.contentHash = [Convert]::ToBase64String([byte[]]::new(64))
                Write-TestJson $lockPath $lock
            }
            'unsafe-library-path' { $assets.libraries.'Fixture.Library/1.0.0'.path = '../library'; Write-TestJson $assetsPath $assets }
            'sdk-unpinned' { $assets.project.frameworks.net10.downloadDependencies[0].name = 'Other.Runtime'; Write-TestJson $assetsPath $assets }
            'sdk-range' { $assets.project.frameworks.net10.downloadDependencies[0].version = '[1.0.0, 2.0.0]'; Write-TestJson $assetsPath $assets }
            'source-drift' { $catalog.documents[0].sha256 = '0' * 64; Write-TestJson $catalogPath $catalog }
            'source-path' { $catalog.documents[0].file = '../outside.txt'; Write-TestJson $catalogPath $catalog }
            'geodata-drift' { $catalog.geoDataEvidence.sourceCommit = 'c' * 40; Write-TestJson $catalogPath $catalog }
        }
        $output = Join-Path $root 'rejected.zip'
        Assert-TestRejects $name { New-ClashSharpThirdPartyNotices -RepositoryRoot $root -OutputPath $output }
        Assert-TestCondition (-not (Test-Path -LiteralPath $output)) ('A rejected fixture left a generated archive: ' + $name)
    }
    $supplementRoot = New-TestFixture -Name 'license-supplement'
    $packagePath = Join-Path $supplementRoot 'cache/fixture.library/1.0.0/fixture.library.1.0.0.nupkg'
    $packageEntries = @(Read-TestZip -Path $packagePath | Where-Object Name -CNE 'LICENSE.txt')
    Write-TestZip -Path $packagePath -Entries $packageEntries
    Update-TestPackageHash -Root $supplementRoot
    $supplementPath = Join-Path $supplementRoot 'notices.zip'
    $null = New-ClashSharpThirdPartyNotices -RepositoryRoot $supplementRoot -OutputPath $supplementPath
    $supplementResult = Get-ClashSharpThirdPartyNoticesContract -LiteralPath $supplementPath
    Assert-TestCondition ($supplementResult.PackageCount -eq 2) 'The exact reviewed license supplement was not accepted.'
    $script:scenarios.Add('exact-license-supplement')

    foreach ($name in @('notice-with-license-supplement', 'notice-with-embedded-license', 'missing-notice-supplement',
            'duplicate-notice-supplement', 'ambiguous-package-supplement')) {
        $root = New-TestFixture -Name $name
        $catalogRoot = Join-Path $root 'ClashSharp/Installer/ThirdParty'
        $catalogPath = Join-Path $catalogRoot 'catalog.json'
        $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json -AsHashtable
        $notice = $utf8.GetBytes('Synthetic exact reviewed package notice.')
        $noticeHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($notice)).ToLowerInvariant()
        [IO.File]::WriteAllBytes((Join-Path $catalogRoot 'source-notice.txt'), $notice)
        $catalog.documents += @{ file = 'source-notice.txt'; sourceUrl = 'https://example.invalid/pinned-notice'; length = $notice.Length; sha256 = $noticeHash }
        $catalog.packageLicenseSupplements[0].notices = @('source-notice.txt')
        if ($name -ceq 'notice-with-license-supplement') {
            $packagePath = Join-Path $root 'cache/fixture.library/1.0.0/fixture.library.1.0.0.nupkg'
            $packageEntries = @(Read-TestZip -Path $packagePath | Where-Object Name -CNE 'LICENSE.txt')
            Write-TestZip -Path $packagePath -Entries $packageEntries
            Update-TestPackageHash -Root $root
        }
        switch ($name) {
            'missing-notice-supplement' { $catalog.packageLicenseSupplements[0].notices = @('absent.txt') }
            'duplicate-notice-supplement' { $catalog.packageLicenseSupplements[0].notices = @('source-notice.txt', 'source-notice.txt') }
            'ambiguous-package-supplement' { $catalog.packageLicenseSupplements += $catalog.packageLicenseSupplements[0] }
        }
        Write-TestJson -Path $catalogPath -Value $catalog
        $output = Join-Path $root 'notices.zip'
        if ($name.StartsWith('notice-with-', [StringComparison]::Ordinal)) {
            $null = New-ClashSharpThirdPartyNotices -RepositoryRoot $root -OutputPath $output
            $contract = Get-ClashSharpThirdPartyNoticesContract -LiteralPath $output
            $inventoryEntry = @(Read-TestZip -Path $output | Where-Object Name -CEQ 'inventory.json')[0]
            $inventory = $utf8.GetString($inventoryEntry.Bytes) | ConvertFrom-Json -AsHashtable
            $package = @($inventory.packages | Where-Object id -CEQ 'Fixture.Library')[0]
            $boundNotice = @($package.documents | Where-Object originalName -CEQ 'source-notice.txt')
            Assert-TestCondition ($contract.PackageCount -eq 2) 'The package notice changed the dependency inventory.'
            Assert-TestCondition ($boundNotice.Count -eq 1 -and $boundNotice[0].sha256 -ceq $noticeHash) 'The exact reviewed package notice was not bound to its package.'
            $script:scenarios.Add($name)
        } else {
            Assert-TestRejects $name { New-ClashSharpThirdPartyNotices -RepositoryRoot $root -OutputPath $output }
            Assert-TestCondition (-not (Test-Path -LiteralPath $output)) 'Rejected notice metadata left an output archive.'
        }
    }

    # Use an actual signed archive so removal of ValidateIntegrityAsync cannot be hidden by
    # an unsigned-package digest failure or a later synthetic metadata-identity mismatch.
    $signedRoot = New-TestFixture -Name 'signed-content-integrity'
    $actualAssets = Get-Content -LiteralPath (Join-Path $repositoryRoot 'ClashSharp/ClashSharp/obj/project.assets.json') -Raw | ConvertFrom-Json -AsHashtable -Depth 64
    $signedId = 'Microsoft.Data.Sqlite.Core'
    $signedVersion = '10.0.0'
    $signedKey = $signedId + '/' + $signedVersion
    $library = $actualAssets.libraries[$signedKey]
    $relativeArchive = $signedKey.ToLowerInvariant() + '/microsoft.data.sqlite.core.10.0.0.nupkg'
    $actualArchives = @($actualAssets.packageFolders.Keys | ForEach-Object { Join-Path $_ $relativeArchive } | Where-Object { Test-Path -LiteralPath $_ })
    Assert-TestCondition ($actualArchives.Count -eq 1) 'The restored signed-package regression input is missing or ambiguous.'
    $signedArchive = Join-Path $signedRoot ('cache/' + $relativeArchive)
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $signedArchive) -Force
    Copy-Item -LiteralPath $actualArchives[0] -Destination $signedArchive
    $signedEntries = @(Read-TestZip -Path $signedArchive)
    Assert-TestCondition (@($signedEntries | Where-Object Name -CEQ '.signature.p7s').Count -eq 1) 'The signed-package regression input is not signed.'
    foreach ($project in @('ClashSharp', 'ClashSharp.MihomoService', 'ClashSharp.RecoveryWatchdog', 'ClashSharp.Installer')) {
        $projectRoot = Join-Path $signedRoot ('ClashSharp/' + $project)
        $lockPath = Join-Path $projectRoot 'packages.lock.json'
        $lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json -AsHashtable
        $lock.dependencies.net10[$signedId] = @{ type = 'Direct'; resolved = $signedVersion; contentHash = $library.sha512 }
        Write-TestJson -Path $lockPath -Value $lock
        $assetsPath = Join-Path $projectRoot 'obj/project.assets.json'
        $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json -AsHashtable
        $assets.libraries[$signedKey] = @{ type = 'package'; path = $signedKey.ToLowerInvariant(); sha512 = $library.sha512 }
        Write-TestJson -Path $assetsPath -Value $assets
    }
    $catalogPath = Join-Path $signedRoot 'ClashSharp/Installer/ThirdParty/catalog.json'
    $catalog = Get-Content -LiteralPath $catalogPath -Raw | ConvertFrom-Json -AsHashtable
    $catalog.packageLicenseSupplements += @{ id = $signedId; version = $signedVersion; licenseType = 'expression'; licenseValue = 'MIT'; document = 'source-license.txt' }
    Write-TestJson -Path $catalogPath -Value $catalog
    $signedOutput = Join-Path $signedRoot 'signed-valid.zip'
    $null = New-ClashSharpThirdPartyNotices -RepositoryRoot $signedRoot -OutputPath $signedOutput
    $signedContract = Get-ClashSharpThirdPartyNoticesContract -LiteralPath $signedOutput
    Assert-TestCondition ($signedContract.PackageCount -eq 3) 'The unchanged signed package did not pass the full generator.'
    $metadataEntry = @($signedEntries | Where-Object { $_.Name.EndsWith('.nuspec') })[0]
    $metadataEntry.Bytes = $utf8.GetBytes($utf8.GetString($metadataEntry.Bytes).Replace('</metadata>', '<releaseNotes>Changed without resigning.</releaseNotes></metadata>'))
    Write-TestZip -Path $signedArchive -Entries $signedEntries
    $integrityRejected = $false
    try {
        $null = New-ClashSharpThirdPartyNotices -RepositoryRoot $signedRoot -OutputPath (Join-Path $signedRoot 'signed-rejected.zip')
    } catch {
        $cause = $_.Exception
        while ($null -ne $cause) {
            if ($cause.GetType().FullName -ceq 'NuGet.Packaging.Signing.SignatureException') { $integrityRejected = $true }
            $cause = $cause.InnerException
        }
    }
    Assert-TestCondition $integrityRejected 'Changed signed content was not rejected by NuGet signature integrity validation.'
    Assert-TestCondition (-not (Test-Path -LiteralPath (Join-Path $signedRoot 'signed-rejected.zip'))) 'Rejected signed content left a generated archive.'
    $script:scenarios.Add('actual-signed-package-and-corrupt-content')
    [ordered]@{ passed = $true; scenarios = $script:scenarios.Count; assertions = $script:assertions; networkOrInstallationInvoked = $false } | ConvertTo-Json -Compress
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    if ($resolved -cne (Join-Path $testParent $runId) -or -not $resolved.StartsWith($testParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unexpected test cleanup root.'
    }
    $entries = @((Get-Item -LiteralPath $resolved -Force)) + @(Get-ChildItem -LiteralPath $resolved -Recurse -Force)
    foreach ($entry in $entries) {
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
            ($entry.FullName -cne $resolved -and -not $entry.FullName.StartsWith($resolved + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))) {
            throw 'Unexpected test cleanup entry.'
        }
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
