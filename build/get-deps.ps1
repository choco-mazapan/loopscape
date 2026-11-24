# Downloads the files listed in build\deps.json (mpv, Rainmeter's installer) and the styles release listed in
# installer\styles-SHA256SUMS.txt into build\deps, checking every SHA-256. Used by the release workflow.
param([string]$Out = (Join-Path $PSScriptRoot 'deps'), [switch]$SkipStyles)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Force $Out | Out-Null

function Get-Checked($url, $sha, $dest) {
  if (-not $url.StartsWith('https://')) { throw "not an https URL: $url" }
  if (-not (Test-Path $dest) -or (Get-FileHash $dest -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sha) {
    "downloading $url"
    Invoke-WebRequest $url -OutFile $dest -UseBasicParsing
  }
  $got = (Get-FileHash $dest -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($got -ne $sha) { Remove-Item $dest; throw "checksum mismatch for $url`n  expected $sha`n  got      $got" }
}

$deps = Get-Content (Join-Path $PSScriptRoot 'deps.json') -Raw | ConvertFrom-Json
foreach ($name in 'mpv', 'rainmeter') {
  $d = $deps.$name
  if ($d.sha256 -eq 'TODO' -or $d.url -like 'TODO*') {
    if ($d.url -like 'https://*') {
      $tmp = Join-Path $Out ([IO.Path]::GetFileName($d.url))
      Invoke-WebRequest $d.url -OutFile $tmp -UseBasicParsing
      throw "deps.json: $name has no sha256 yet. Fetched file: $tmp  sha256 $((Get-FileHash $tmp -Algorithm SHA256).Hash.ToLowerInvariant())"
    }
    throw "deps.json: fill in the url and sha256 for $name"
  }
  Get-Checked $d.url $d.sha256.ToLowerInvariant() (Join-Path $Out ([IO.Path]::GetFileName($d.url)))
}

if (-not $SkipStyles) {
  $lines = Get-Content (Join-Path $root 'installer\styles-SHA256SUMS.txt')
  $tag = ($lines[0] -replace '^#\s*', '').Trim()
  $dir = Join-Path $Out 'styles'
  New-Item -ItemType Directory -Force $dir | Out-Null
  foreach ($l in $lines | Select-Object -Skip 1) {
    if ($l -notmatch '^([0-9a-f]{64})\s+(\S+)$') { continue }
    Get-Checked "https://github.com/choco-mazapan/loopscape/releases/download/$tag/$($Matches[2])" $Matches[1] (Join-Path $dir $Matches[2])
  }
}
"Dependencies in $Out"
