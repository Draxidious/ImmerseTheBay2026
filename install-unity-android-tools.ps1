#Requires -RunAsAdministrator
# Installs the exact JDK / Android SDK / NDK packages Unity Hub would install for
# Unity 6000.6.8f1, into the same folders Hub uses. URLs, hashes and folder layout
# come from Unity's public release API for this editor version.

$ErrorActionPreference = 'Stop'
$ap = 'C:\Program Files\Unity\Hub\Editor\6000.6.8f1\Editor\Data\PlaybackEngines\AndroidPlayer'
$dl = Join-Path $env:TEMP 'unity-android-6000.6.8f1'
$g  = 'https://dl.google.com/android/repository'
$u  = 'https://download.unity3d.com/download_unity'

# ExtractTo = where the zip is unpacked; From/To = rename Hub performs afterwards.
$items = @(
    @{ Id='jdk';            Name='OpenJDK 17.0.18+8'
       Url="$u/open-jdk/open-jdk-win-x64/jdk17.0.18-8_15e8817d1f5db6db3571ebe7430ef37f7fa8e60e8ff6f3e18ca1cb4c29f78774.zip"
       Sha='6aAXbkzf3bu0C5pgLooFr2H07nWvT4mJacQyPJ6SpSg0r7YEz4Z/Mg+Mq50//KUf'
       ExtractTo="$ap\OpenJDK" }
    @{ Id='sdk-base';       Name='Android SDK base'
       Url="$u/android-sdk-tools/1_5312bb398affd0d94b90d3780976e1a162aa91944ef6bb40c8feb10ad6cc360d.zip"
       Sha='MUfRm1NfDgYjsMKa4jVqDsD2TFXAGBwF7cFMDQl6ZvYomQPehNZPuAcYKRlDeI/O'
       ExtractTo="$ap\SDK" }
    @{ Id='ndk';            Name='Android NDK r27c'
       Url="$g/android-ndk-r27c-windows.zip"
       Sha='HGDUOJ/0s7njhyEL4LKqVy++Y+mob+86+pa3xWDruHelhQsq2SfAimv6p4BxHtLh'
       ExtractTo="$ap\_ndk_tmp"; From="$ap\_ndk_tmp\android-ndk-r27c"; To="$ap\NDK"; Cleanup="$ap\_ndk_tmp" }
    @{ Id='cmake';          Name='CMake 3.22.1'
       Url="$g/cmake-3.22.1-windows.zip"
       Sha='ubkkpyTNRdHYTXpJJFm2rCyWwCUgPx3TVZWplGpUtS+3p5tBCkw7t1DyiXxIMG54'
       ExtractTo="$ap\SDK\cmake\cmake"; From="$ap\SDK\cmake\cmake"; To="$ap\SDK\cmake\3.22.1" }
    @{ Id='build-tools';    Name='SDK Build Tools 36.0.0'
       Url="$g/build-tools_r36_windows.zip"
       Sha='oCn/tanmwwaeFnRtAv+DaV+dXwORU7GMlSK2N8DovihBeIG+I3/zv/frPtP/jGXA'
       ExtractTo="$ap\SDK\build-tools"; From="$ap\SDK\build-tools\android-16"; To="$ap\SDK\build-tools\36.0.0" }
    @{ Id='platform-tools'; Name='SDK Platform Tools 37.0.1'
       Url="$g/platform-tools_r37.0.1-win.zip"
       Sha='atvWWFtpHrXGSWp5V3g4K8bzOo/q/sLhSxuvexWsQ2m3VXZXtZWFQVhqD8GdgUPO'
       ExtractTo="$ap\SDK" }
    @{ Id='platform-34';    Name='SDK Platform 34'
       Url="$g/platform-34-ext7_r02.zip"
       Sha='LkkJWLJ01ztXWPjV2uxJckshlwNYzxoNNeQI5clQrMWCrHBaHcpaPygn1ii7EpEq'
       ExtractTo="$ap\SDK\platforms" }
    @{ Id='platform-36';    Name='SDK Platform 36'
       Url="$g/platform-36_r02.zip"
       Sha='9v7E8tTx7DciqyA/iblrEcCf0XLLxnE+m5BSvv5LvZRW7gQDo7cZht3b1vARQ90P'
       ExtractTo="$ap\SDK\platforms" }
    @{ Id='platform-37';    Name='SDK Platform 37'
       Url="$g/platform-37.0_r02.zip"
       Sha='9IeJRzd3/WvRUwXzwi7OJ+B+dh/qCd5VJyDlHi4o1ful5ACBptxRHECwF/OXk79w'
       ExtractTo="$ap\SDK\platforms" }
    @{ Id='cmdline-tools';  Name='SDK Command Line Tools 16.0'
       Url="$g/commandlinetools-win-12266719_latest.zip"
       Sha='3H6q0QmivqDyr88w9L8AsYO9yGt41w/Or/JYqclRY2ZbCh3kdguKuyAQYM6JwyWe'
       ExtractTo="$ap\SDK\cmdline-tools"; From="$ap\SDK\cmdline-tools\cmdline-tools"; To="$ap\SDK\cmdline-tools\16.0" }
)

try {
    if (-not (Test-Path $ap)) { throw "Android Build Support folder not found: $ap" }
    if (Get-Process Unity -ErrorAction SilentlyContinue) { throw 'Close the Unity editor first, then run this again.' }
    New-Item -ItemType Directory -Force $dl | Out-Null

    foreach ($i in $items) {
        $marker = Join-Path $dl "$($i.Id).done"
        if (Test-Path $marker) { Write-Host "[skip] $($i.Name) already installed"; continue }

        $zip = Join-Path $dl "$($i.Id).zip"
        Write-Host "`n[download] $($i.Name)" -ForegroundColor Cyan
        & curl.exe -L --fail --retry 3 -o $zip $i.Url
        if ($LASTEXITCODE -ne 0) { throw "Download failed: $($i.Url)" }

        $hex   = (Get-FileHash $zip -Algorithm SHA384).Hash
        $bytes = [byte[]]::new($hex.Length / 2)
        for ($n = 0; $n -lt $bytes.Length; $n++) { $bytes[$n] = [Convert]::ToByte($hex.Substring($n * 2, 2), 16) }
        if ([Convert]::ToBase64String($bytes) -ne $i.Sha) { throw "Checksum mismatch for $($i.Name)" }

        Write-Host "[extract] $($i.Name)"
        if ($i.To -and (Test-Path $i.To)) { Remove-Item $i.To -Recurse -Force }
        New-Item -ItemType Directory -Force $i.ExtractTo | Out-Null
        & tar.exe -xf $zip -C $i.ExtractTo
        if ($LASTEXITCODE -ne 0) { throw "Extract failed: $($i.Name)" }

        if ($i.From) { Move-Item $i.From $i.To }
        if ($i.Cleanup) { Remove-Item $i.Cleanup -Recurse -Force }

        # The JDK zip may wrap everything in one top-level folder; Unity wants bin\ directly under OpenJDK.
        if ($i.Id -eq 'jdk' -and -not (Test-Path "$ap\OpenJDK\bin\java.exe")) {
            $kids = @(Get-ChildItem "$ap\OpenJDK")
            if ($kids.Count -eq 1 -and $kids[0].PSIsContainer) {
                Get-ChildItem $kids[0].FullName -Force | Move-Item -Destination "$ap\OpenJDK"
                Remove-Item $kids[0].FullName -Force
            }
            if (-not (Test-Path "$ap\OpenJDK\bin\java.exe")) { throw 'JDK extracted but bin\java.exe was not found.' }
        }

        Remove-Item $zip -Force
        New-Item -ItemType File $marker | Out-Null
    }
    Write-Host "`nAll done. Reopen Unity and check Preferences > External Tools." -ForegroundColor Green
}
catch {
    Write-Host "`nFAILED: $($_.Exception.Message)" -ForegroundColor Red
}
Read-Host 'Press Enter to close'
