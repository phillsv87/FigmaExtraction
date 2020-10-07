#!/usr/local/bin/pwsh
param(
    [string]$themeFile=$(throw "-themeFile required"),
    [string]$infName=$(throw "-infName required"),
    [string]$output=$(throw "-output required"),
    [string]$tokenPath=$(throw "-tokenPath required"),
    [string]$fileId=$(throw "-fileId required"),
    [string]$nodeIds=$(throw "-nodeIds required")
)
$ErrorActionPreference="Stop"

$ts=dotnet run --project "$PSScriptRoot" -c Release -- `
    -themeFile $themeFile `
    -infName $infName `
    -tokenPath $tokenPath `
    -fileId $fileId `
    -nodeIds $nodeIds

if(!$?){
    throw "Figma Extration failed"
}

$ts=$ts | Out-String

$styleTs=Get-Content -Path $output -Raw

$start='////<Theme>';
$end='////</Theme>';

$si=$styleTs.IndexOf($start);
$se=$styleTs.IndexOf($end);
if($si -eq -1 -or $se -eq -1){
    throw "Start or end tag not found in $output"
}

$inserted= `
    $styleTs.Substring(0,$si+$start.Length)+ `
    "`n// Content within the <Theme> tag are auto generated using $themeFile`n" + `
    $ts+ `
    $styleTs.Substring($se)

$inserted=$inserted.Trim()

if($inserted -eq $styleTs.Trim()){
    Write-Host "No changes to $output"
}else{
    Set-Content -Path $output -Value $inserted
    Write-Host "Updated $output" -ForegroundColor DarkGreen
}
