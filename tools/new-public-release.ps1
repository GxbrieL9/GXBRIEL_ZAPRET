param([string]$OutputDirectory = '', [string]$SigningKeyPath = '', [string]$SourcesArchive = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts/public-release/release' }
[xml]$project = Get-Content -LiteralPath (Join-Path $root 'ui/GxbrielZapret/GxbrielZapret.csproj')
$version = [string]($project.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Версия должна иметь формат X.Y.Z.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
dotnet publish (Join-Path $root 'ui/GxbrielZapret/GxbrielZapret.csproj') -c Release -r win-x64 --self-contained true -p:ContinuousIntegrationBuild=true -p:DebugType=None -o $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw 'Сборка выпуска завершилась ошибкой.' }
$exe = Join-Path $OutputDirectory 'GXBRIEL_ZAPRET.exe'
$bytes = [Text.UTF8Encoding]::new($false).GetBytes(([ordered]@{
    Version=$version; FileName='GXBRIEL_ZAPRET.exe'; Url="https://github.com/GxbrieL9/GXBRIEL_ZAPRET/releases/download/v$version/GXBRIEL_ZAPRET.exe"
    Sha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant(); Size=(Get-Item -LiteralPath $exe).Length
} | ConvertTo-Json))
[IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'app-update.json'), $bytes)
$key = if ($SigningKeyPath) { [IO.File]::ReadAllText($SigningKeyPath) } else { $env:RELEASE_SIGNING_KEY }
if (-not $key) { throw 'Не задан закрытый ключ подписи выпуска. Неподписанное обновление не публикуется.' }
$rsa = [Security.Cryptography.RSA]::Create()
try {
    $rsa.ImportFromPem($key)
    $signature = $rsa.SignData($bytes,[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pkcs1)
    [IO.File]::WriteAllBytes((Join-Path $OutputDirectory 'app-update.sig'),$signature)
} finally { $rsa.Dispose(); $key = $null }
$portable = Join-Path $OutputDirectory 'portable'
New-Item -ItemType Directory -Path $portable -Force | Out-Null
Copy-Item -LiteralPath $exe -Destination $portable
Copy-Item -LiteralPath (Join-Path $root 'README.md'),(Join-Path $root 'THIRD_PARTY.md'),(Join-Path $root 'LICENSE') -Destination $portable
Copy-Item -LiteralPath (Join-Path $root 'third_party/licenses') -Destination (Join-Path $portable 'licenses') -Recurse
Compress-Archive -Path (Join-Path $portable '*') -DestinationPath (Join-Path $OutputDirectory 'GXBRIEL_ZAPRET-win-x64.zip') -Force
if (-not $SourcesArchive -or -not (Test-Path -LiteralPath $SourcesArchive)) { throw 'Для выпуска нужны соответствующие исходники зависимостей.' }
Copy-Item -LiteralPath $SourcesArchive -Destination (Join-Path $OutputDirectory 'third-party-sources.zip') -Force
$hashes = Get-ChildItem -LiteralPath $OutputDirectory -File | Where-Object Extension -in '.exe','.zip','.json','.sig' | Sort-Object Name |
    ForEach-Object { "{0}  {1}" -f (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(),$_.Name }
[IO.File]::WriteAllLines((Join-Path $OutputDirectory 'SHA256SUMS.txt'),[string[]]$hashes,[Text.UTF8Encoding]::new($false))
Write-Output "Подготовлен подписанный выпуск $version"
