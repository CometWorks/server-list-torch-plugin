param(
    [Parameter(Mandatory)][string]$ZipPath,
    [string]$ManifestPath = 'manifest.xml'
)
$ErrorActionPreference = 'Stop'
if (!$env:TORCH_API_KEY) { throw 'Set the Actions secret TORCH_API_KEY for the OwendB Torch account.' }
[xml]$manifest = Get-Content $ManifestPath
$guid = $manifest.PluginManifest.Guid
$version = [version]$manifest.PluginManifest.Version
$existing = $null
try {
    $existing = Invoke-RestMethod "https://torchapi.com/api/plugins/search/$guid" -TimeoutSec 30
} catch {
    if ([int]$_.Exception.Response.StatusCode -ne 404) { throw 'Unable to read the current Torch plugin registration.' }
}
$action = 'upload'
if ($existing.guid) {
    if ($existing.guid -ne $guid) { throw 'Torch returned a different plugin identity.' }
    if ($existing.versions | Where-Object { $_.version.TrimStart('v') -eq $version.ToString() }) {
        Write-Host "Torch already has version $version; publishing retry complete."
        exit 0
    }
    if ($existing.latestVersion -and [version]$existing.latestVersion.TrimStart('v') -gt $version) {
        throw 'Refusing to publish an older version than the current Torch release.'
    }
    $action = 'update'
}
$form = @{
    username = 'OwendB'
    secret = $env:TORCH_API_KEY
    action = $action
    uploadZip = Get-Item $ZipPath
}
if ($action -eq 'upload') {
    $form.name = $manifest.PluginManifest.Name
    $form.description = Get-Content 'TORCH-DESCRIPTION.md' -Raw
    $form.isPrivate = 'false'
} else {
    $form.updateNote = "Release $version — https://github.com/CometWorks/server-list-torch-plugin/releases/tag/v$version"
}
$multipart = [System.Net.Http.MultipartFormDataContent]::new()
try {
    foreach ($field in $form.Keys) {
        if ($field -eq 'uploadZip') {
            $part = [System.Net.Http.StreamContent]::new($form.uploadZip.OpenRead())
            $part.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/zip')
            $multipart.Add($part, $field, $form.uploadZip.Name)
        } else {
            $multipart.Add([System.Net.Http.StringContent]::new([string]$form[$field]), $field)
        }
    }
    $result = Invoke-RestMethod 'https://torchapi.com/api/plugins/upload' -Method Post -Body $multipart -TimeoutSec 120
} finally { $multipart.Dispose() }
if ($result -is [string]) { $result = $result | ConvertFrom-Json }
if ($result.error_message) { throw ('Torch rejected the plugin upload: ' + ([string]$result.error_message).Replace($env:TORCH_API_KEY, '[redacted]')) }
if (!$result.success_message) { throw 'Torch returned no upload confirmation.' }
Write-Host "Published $($manifest.PluginManifest.Name) $version under OwendB."
Write-Host "https://torchapi.com/plugins/view/$guid"
