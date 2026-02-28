param(
    [string]$ProjectPath = "VRCosme.csproj",
    [string]$OutputPath = "THIRD-PARTY-NOTICES.txt",
    [string]$NoticeOutputDir = "third_party_notices",
    [switch]$SkipNoticeExtraction
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-GlobalPackagesPath {
    $line = dotnet nuget locals global-packages --list | Select-Object -First 1
    if (-not $line) {
        throw "Failed to read NuGet global-packages path."
    }

    $parts = $line.Split(":", 2)
    if ($parts.Count -lt 2) {
        throw "Unexpected output from 'dotnet nuget locals global-packages --list': $line"
    }

    return $parts[1].Trim()
}

function Resolve-LicenseName {
    param(
        [string]$LicenseType,
        [string]$LicenseValue,
        [string]$LicenseFilePath
    )

    if ($LicenseType -eq "expression" -and $LicenseValue) {
        return $LicenseValue.Trim()
    }

    if ($LicenseType -eq "file" -and (Test-Path $LicenseFilePath)) {
        $licenseText = Get-Content -Path $LicenseFilePath -Raw

        if ($licenseText -match "(?im)^Six Labors Split License") {
            return "Six Labors Split License 1.0 (Apache-2.0 or Commercial)"
        }

        if ($licenseText -match "(?im)^MIT License") {
            return "MIT"
        }

        if ($licenseText -match "(?im)^Apache License" -and $licenseText -match "(?im)Version 2\.0") {
            return "Apache-2.0"
        }

        if ($licenseText -match "(?im)BSD 3-Clause") {
            return "BSD-3-Clause"
        }

        if ($licenseText -match "(?im)^MICROSOFT SOFTWARE LICENSE TERMS") {
            return "Microsoft Software License Terms (Proprietary)"
        }
    }

    if ($LicenseValue) {
        return "Embedded license file: $LicenseValue"
    }

    return "Unknown"
}

function Resolve-LicenseUrl {
    param(
        [string]$PackageId,
        [string]$Version,
        [string]$LicenseType,
        [string]$LicenseName
    )

    if ($LicenseType -eq "expression" -and $LicenseName -and $LicenseName -notmatch " ") {
        return "https://licenses.nuget.org/$LicenseName"
    }

    return "https://www.nuget.org/packages/$PackageId/$Version/License"
}

Write-Host "Restoring project..."
dotnet restore $ProjectPath | Out-Host

$projectFullPath = (Resolve-Path $ProjectPath).Path
$projectDir = Split-Path -Parent $projectFullPath
$noticeOutputPath = Join-Path $projectDir $NoticeOutputDir
if (-not $SkipNoticeExtraction) {
    if (Test-Path $noticeOutputPath) {
        Remove-Item -Path $noticeOutputPath -Recurse -Force
    }
    New-Item -ItemType Directory -Path $noticeOutputPath -Force | Out-Null
}

$assetsPath = Join-Path $projectDir "obj\project.assets.json"
if (-not (Test-Path $assetsPath)) {
    $candidate = Get-ChildItem -Path $projectDir -Recurse -Filter "project.assets.json" |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if (-not $candidate) {
        throw "project.assets.json was not found. Run restore first."
    }
    $assetsPath = $candidate.FullName
}

$assets = Get-Content -Path $assetsPath -Raw | ConvertFrom-Json
$assetsPathDisplay = Resolve-Path -LiteralPath $assetsPath -Relative
$frameworkName = ($assets.targets.PSObject.Properties | Select-Object -First 1).Name
if (-not $frameworkName) {
    throw "No target framework was found in project.assets.json."
}

$target = $assets.targets.$frameworkName
$libraries = $assets.libraries.PSObject.Properties |
    Where-Object { $_.Value.type -eq "package" } |
    Sort-Object Name

$directSet = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$frameworkDeps = $assets.project.frameworks.$frameworkName.dependencies
if ($frameworkDeps) {
    foreach ($dep in $frameworkDeps.PSObject.Properties) {
        [void]$directSet.Add($dep.Name)
    }
}

$globalPackages = Get-GlobalPackagesPath
$packageInfos = @()

foreach ($lib in $libraries) {
    $packageKey = $lib.Name
    $parts = $packageKey.Split("/", 2)
    if ($parts.Count -ne 2) {
        continue
    }

    $packageId = $parts[0]
    $version = $parts[1]
    $packageLower = $packageId.ToLowerInvariant()
    $packageDir = Join-Path $globalPackages "$packageLower\$version"

    $entry = $target.$packageKey
    $propNames = @()
    if ($entry) {
        $propNames = $entry.PSObject.Properties.Name
    }

    $hasRuntime = $propNames -contains "runtime"
    $hasRuntimeTargets = $propNames -contains "runtimeTargets"
    $hasNative = $propNames -contains "native"
    $hasBuild = ($propNames -contains "build") -or ($propNames -contains "buildTransitive")

    $hasBinaryPayloadViaBuild = $false
    if ($hasBuild -and (Test-Path $packageDir)) {
        $binaryCandidates = @(Get-ChildItem -Path $packageDir -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object {
                ($_.FullName -match "\\bin\\|\\runtimes\\") -and
                ($_.Extension -in @(".dll", ".so", ".dylib"))
            } |
            Select-Object -First 1)
        $hasBinaryPayloadViaBuild = $binaryCandidates.Count -gt 0
    }

    $runtimeLikelihood = if ($hasRuntime -or $hasRuntimeTargets -or $hasNative -or $hasBinaryPayloadViaBuild) {
        "High"
    }
    elseif ($hasBuild) {
        "Medium (build assets only)"
    }
    else {
        "Low"
    }

    $nuspecPath = Join-Path $packageDir "$packageLower.nuspec"
    if (-not (Test-Path $nuspecPath)) {
        $nuspecPath = Get-ChildItem -Path $packageDir -Filter "*.nuspec" -ErrorAction SilentlyContinue |
            Select-Object -First 1 -ExpandProperty FullName
    }

    $licenseType = ""
    $licenseValue = ""
    $copyrightText = ""
    if ($nuspecPath -and (Test-Path $nuspecPath)) {
        [xml]$nuspec = Get-Content -Path $nuspecPath
        $metadata = $nuspec.package.metadata
        if ($metadata.license) {
            $licenseType = [string]$metadata.license.type
            $licenseValue = [string]$metadata.license.'#text'
        }
        $copyrightText = [string]$metadata.copyright
    }

    $licenseFilePath = if ($licenseType -eq "file" -and $licenseValue) {
        Join-Path $packageDir $licenseValue
    }
    else {
        ""
    }

    $licenseName = Resolve-LicenseName -LicenseType $licenseType -LicenseValue $licenseValue -LicenseFilePath $licenseFilePath
    $licenseUrl = Resolve-LicenseUrl -PackageId $packageId -Version $version -LicenseType $licenseType -LicenseName $licenseName

    $noticeFiles = @()
    $bundledNoticeFiles = @()
    if (Test-Path $packageDir) {
        $noticeFiles = @(Get-ChildItem -Path $packageDir -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match "^(?i)(NOTICE(\..*)?|THIRD-PARTY(-NOTICES)?(\..*)?|ThirdPartyNotices(\..*)?)$" } |
            ForEach-Object { $_.FullName.Substring($packageDir.Length + 1) } |
            Sort-Object -Unique)

        if ((-not $SkipNoticeExtraction) -and $noticeFiles.Count -gt 0) {
            foreach ($noticeRelPath in $noticeFiles) {
                $sourcePath = Join-Path $packageDir $noticeRelPath
                $destPath = Join-Path (Join-Path $noticeOutputPath $packageId) $noticeRelPath
                $destDir = Split-Path -Parent $destPath
                New-Item -ItemType Directory -Path $destDir -Force | Out-Null
                Copy-Item -Path $sourcePath -Destination $destPath -Force

                $bundledRelPath = Join-Path (Join-Path $NoticeOutputDir $packageId) $noticeRelPath
                $bundledNoticeFiles += $bundledRelPath.Replace("\", "/")
            }
        }
    }

    $packageInfos += [PSCustomObject]@{
        PackageId          = $packageId
        Version            = $version
        DependencyType     = if ($directSet.Contains($packageId)) { "Direct" } else { "Transitive" }
        RuntimeLikelihood  = $runtimeLikelihood
        IsDirect           = $directSet.Contains($packageId)
        RuntimeFlags       = @(
            if ($hasRuntime) { "runtime" }
            if ($hasRuntimeTargets) { "runtimeTargets" }
            if ($hasNative) { "native" }
            if ($hasBuild) { "build/buildTransitive" }
            if ($hasBinaryPayloadViaBuild) { "binary payload via build targets" }
        ) -join ", "
        LicenseName        = $licenseName
        LicenseUrl         = $licenseUrl
        Copyright          = if ($copyrightText) { $copyrightText } else { "N/A" }
        NoticeFiles        = $noticeFiles
        BundledNoticeFiles = $bundledNoticeFiles
        SourceAssets       = $assetsPathDisplay
        ComplianceNote     = if ($licenseName -like "Six Labors Split License*") {
            "This is a split license. Apache-2.0 applies only when the license conditions are met; otherwise a commercial license may be required."
        }
        else {
            ""
        }
    }
}

$sorted = $packageInfos | Sort-Object @{ Expression = "IsDirect"; Descending = $true }, PackageId
$runtimeLikely = $sorted | Where-Object { $_.RuntimeLikelihood -eq "High" }
$direct = $sorted | Where-Object { $_.DependencyType -eq "Direct" }
$transitive = $sorted | Where-Object { $_.DependencyType -eq "Transitive" }

$sb = [System.Text.StringBuilder]::new()
[void]$sb.AppendLine("THIRD-PARTY NOTICES FOR VRCOSME")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("Generated by: scripts/Generate-ThirdPartyNotices.ps1")
[void]$sb.AppendLine("Project: $ProjectPath")
[void]$sb.AppendLine("Target framework: $frameworkName")
[void]$sb.AppendLine("Dependency source: $assetsPathDisplay")
if (-not $SkipNoticeExtraction) {
    [void]$sb.AppendLine("Bundled NOTICE directory: .\$NoticeOutputDir")
}
[void]$sb.AppendLine("")
[void]$sb.AppendLine("This file lists NuGet packages resolved for the current build.")
[void]$sb.AppendLine("Keep this file with binary distributions (zip/installer) of VRCosme.")
[void]$sb.AppendLine("")
[void]$sb.AppendLine("RUNTIME-LIKELY DEPENDENCIES")
[void]$sb.AppendLine("(Packages that expose runtime/native/runtimeTargets assets in project.assets.json)")
foreach ($pkg in $runtimeLikely) {
    [void]$sb.AppendLine("- $($pkg.PackageId) $($pkg.Version) [$($pkg.DependencyType)]")
}
[void]$sb.AppendLine("")
[void]$sb.AppendLine("DIRECT DEPENDENCIES")
foreach ($pkg in $direct) {
    [void]$sb.AppendLine("- $($pkg.PackageId) $($pkg.Version)")
}
[void]$sb.AppendLine("")
[void]$sb.AppendLine("TRANSITIVE DEPENDENCIES")
foreach ($pkg in $transitive) {
    [void]$sb.AppendLine("- $($pkg.PackageId) $($pkg.Version)")
}
[void]$sb.AppendLine("")
[void]$sb.AppendLine("PACKAGE NOTICE DETAILS")

foreach ($pkg in $sorted) {
    [void]$sb.AppendLine("")
    [void]$sb.AppendLine("Package: $($pkg.PackageId)")
    [void]$sb.AppendLine("Version: $($pkg.Version)")
    [void]$sb.AppendLine("Dependency type: $($pkg.DependencyType)")
    [void]$sb.AppendLine("Runtime likelihood: $($pkg.RuntimeLikelihood)")
    [void]$sb.AppendLine("Runtime asset flags: $($pkg.RuntimeFlags)")
    [void]$sb.AppendLine("License: $($pkg.LicenseName)")
    [void]$sb.AppendLine("License reference: $($pkg.LicenseUrl)")
    [void]$sb.AppendLine("Copyright: $($pkg.Copyright)")

    if ($pkg.NoticeFiles.Count -gt 0) {
        [void]$sb.AppendLine("NOTICE handling: NOTICE/Third-party notice file detected in package: $($pkg.NoticeFiles -join ", "). Keep applicable attributions in redistributed binaries.")
        if ($pkg.BundledNoticeFiles.Count -gt 0) {
            [void]$sb.AppendLine("Bundled NOTICE copy: $($pkg.BundledNoticeFiles -join ", ")")
        }
    }
    else {
        [void]$sb.AppendLine("NOTICE handling: No standalone NOTICE file was detected in this package.")
    }

    if ($pkg.ComplianceNote) {
        [void]$sb.AppendLine("Compliance note: $($pkg.ComplianceNote)")
    }
}

[System.IO.File]::WriteAllText((Join-Path $projectDir $OutputPath), $sb.ToString(), [System.Text.UTF8Encoding]::new($false))
Write-Host "Generated $(Join-Path $projectDir $OutputPath)"
