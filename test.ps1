param([string]$DefaultServer = '')
$ErrorActionPreference = 'Stop'
$taskExpectedServer = if ($DefaultServer.Trim()) { ([Uri]$DefaultServer.Trim()).AbsoluteUri.TrimEnd('/') } else { '' }
Push-Location $PSScriptRoot
$taskSavedCache = $env:GOCACHE
$taskSavedTemp = $env:GOTMPDIR
try {
    ./build.ps1 -DefaultServer $DefaultServer
    $env:GOCACHE = Join-Path $PSScriptRoot '.cache/go-build'
    $env:GOTMPDIR = Join-Path $PSScriptRoot '.cache/tmp'
    go test -count=3 -timeout 90s ./...
    if ($LASTEXITCODE -ne 0) { throw '内核连续三次验证失败。' }
    go vet ./...
    if ($LASTEXITCODE -ne 0) { throw 'Go vet 失败。' }
    $taskVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    $taskMSBuild = & $taskVsWhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
    & $taskMSBuild wpf/tests/WpfTests.csproj /nologo /t:Build /v:minimal /m:1 /nr:false
    if ($LASTEXITCODE -ne 0) { throw 'WPF 测试程序构建失败。' }
    for ($taskRun = 1; $taskRun -le 3; $taskRun++) {
        $taskArguments = '"' + (Join-Path $PSScriptRoot 'test-results/wpf-native') + '" "' + $taskExpectedServer + '"'
        $taskOutput = Join-Path $PSScriptRoot ".cache/wpf-native-$taskRun.out"
        $taskError = Join-Path $PSScriptRoot ".cache/wpf-native-$taskRun.err"
        # Match a normal desktop launch instead of inheriting terminal startup window flags.
        $taskProcess = Start-Process -FilePath (Join-Path $PSScriptRoot 'test-results/wpf/WpfTests.exe') -ArgumentList $taskArguments -WindowStyle Normal -Wait -PassThru -RedirectStandardOutput $taskOutput -RedirectStandardError $taskError
        Get-Content -LiteralPath $taskOutput -Encoding utf8
        Get-Content -LiteralPath $taskError -Encoding utf8
        if ($taskProcess.ExitCode -ne 0) { throw "WPF 第 $taskRun 次交互验证失败。" }
        Write-Host "WPF 第 $taskRun 次交互验证通过。"
    }
} finally {
    $env:GOCACHE = $taskSavedCache
    $env:GOTMPDIR = $taskSavedTemp
    Pop-Location
}
