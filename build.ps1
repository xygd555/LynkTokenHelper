param([string]$DefaultServer = '', [switch]$SkipMiniProgramCompatibilityChecks)
$ErrorActionPreference = 'Stop'
$DefaultServer = $DefaultServer.Trim().TrimEnd('/')
if ($DefaultServer) {
    $serverUri = $null
    if ($DefaultServer -match '[\s\x22\x27]' -or
        -not [Uri]::TryCreate($DefaultServer, [UriKind]::Absolute, [ref]$serverUri) -or
        $serverUri.Scheme -notin @('http','https') -or
        -not $serverUri.Host -or $serverUri.UserInfo -or $serverUri.Query -or $serverUri.Fragment) {
        throw '默认服务器必须是完整的 HTTP 或 HTTPS 地址，不包含账号、密码、查询参数、片段、空白或引号。'
    }
    $DefaultServer = $serverUri.AbsoluteUri.TrimEnd('/')
}
$taskVsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $taskVsWhere)) { throw '请安装 Visual Studio 的 .NET 桌面开发工作负载。' }
$taskMSBuild = & $taskVsWhere -latest -products '*' -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' | Select-Object -First 1
if (-not $taskMSBuild) { throw '未找到 Visual Studio MSBuild。' }
$taskReferences = Join-Path ${env:ProgramFiles(x86)} 'Reference Assemblies/Microsoft/Framework/.NETFramework/v4.8'
if (-not (Test-Path -LiteralPath $taskReferences)) { throw '请安装 .NET Framework 4.8 开发工具。' }
$taskSavedCache=$env:GOCACHE; $taskSavedTemp=$env:GOTMPDIR; $taskSavedOS=$env:GOOS; $taskSavedArch=$env:GOARCH
Push-Location $PSScriptRoot
try {
    $env:GOCACHE=Join-Path $PSScriptRoot '.cache/go-build'
    $env:GOTMPDIR=Join-Path $PSScriptRoot '.cache/tmp'
    $env:GOOS='windows'; $env:GOARCH='amd64'
    New-Item -ItemType Directory -Force $env:GOCACHE,$env:GOTMPDIR,'wpf/obj','dist' | Out-Null
    $taskLinkerFlags='-s -w -H=windowsgui'
    if($DefaultServer){$taskLinkerFlags += " -X=main.defaultServer=$DefaultServer"}
    go build -trimpath -ldflags $taskLinkerFlags -o wpf/obj/Core.exe .
    if($LASTEXITCODE -ne 0){throw '抓包内核构建失败。'}
    $taskInput=[IO.File]::OpenRead((Join-Path $PSScriptRoot 'wpf/obj/Core.exe'))
    $taskOutput=[IO.File]::Create((Join-Path $PSScriptRoot 'wpf/obj/Core.exe.gz'))
    $taskGzip=[IO.Compression.GZipStream]::new($taskOutput,[IO.Compression.CompressionLevel]::Optimal)
    try{$taskInput.CopyTo($taskGzip)}finally{$taskInput.Dispose();$taskGzip.Dispose();$taskOutput.Dispose()}
    $taskVersionSource=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'VERSION'))
    $taskVersion=$taskVersionSource.Trim()
    if($taskVersion -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+$'){throw 'VERSION 必须为 v1.0.0 格式。'}
    $taskAssemblyVersion=$taskVersion.TrimStart('v')+'.0'
    $taskBuildInfo=@"
using System.Reflection;
[assembly: AssemblyTitle("领克凭据助手")]
[assembly: AssemblyProduct("Lynk Sign Token Helper")]
[assembly: AssemblyVersion("$taskAssemblyVersion")]
[assembly: AssemblyFileVersion("$taskAssemblyVersion")]
namespace LynkSignTokenHelper { internal static class BuildInfo { public const string Version = "$taskVersion"; public const string DefaultServer = @"$DefaultServer"; } }
"@
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'wpf/obj/BuildInfo.cs'),$taskBuildInfo,[Text.UTF8Encoding]::new($false))
    $taskSkipCompatibility = $SkipMiniProgramCompatibilityChecks.IsPresent.ToString().ToLowerInvariant()
    & $taskMSBuild wpf/LynkSignTokenHelper.csproj /nologo /t:Build /p:Configuration=Release /p:Platform=x64 /p:SkipMiniProgramCompatibilityChecks=$taskSkipCompatibility /v:minimal /m:1 /nr:false
    if($LASTEXITCODE -ne 0){throw 'WPF 桌面程序构建失败。'}
    if ($SkipMiniProgramCompatibilityChecks) {
        Write-Host '已生成跳过小程序兼容性检查的测试版：dist/unchecked/LynkSignTokenHelper-windows-x64-unchecked.exe'
        return
    }
    Copy-Item -LiteralPath README.md -Destination dist/README.md -Force
    $taskLicenseDir=Join-Path $PSScriptRoot 'dist/licenses'
    if(Test-Path -LiteralPath $taskLicenseDir){
        if([IO.Path]::GetFullPath($taskLicenseDir) -ne (Join-Path $PSScriptRoot 'dist\licenses')){throw '构建目录不符合预期。'}
        Remove-Item -LiteralPath $taskLicenseDir -Recurse -Force
    }
    Copy-Item -LiteralPath licenses -Destination dist -Recurse -Force
    Copy-Item -LiteralPath LICENSE -Destination (Join-Path $taskLicenseDir 'LynkTokenHelper-LICENSE.txt') -Force
    Compress-Archive -LiteralPath dist/LynkSignTokenHelper-windows-x64.exe,dist/README.md,dist/licenses -DestinationPath dist/LynkSignTokenHelper-windows-x64.zip -Force
    Write-Host '已生成 Windows x64 凭据工具 EXE 和 zip。'
} finally {
    $env:GOCACHE=$taskSavedCache; $env:GOTMPDIR=$taskSavedTemp; $env:GOOS=$taskSavedOS; $env:GOARCH=$taskSavedArch
    Pop-Location
}
