$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
$env:TORCH_API_KEY = 'test-only-publishing-secret'
$fixtureZip = Join-Path ([System.IO.Path]::GetTempPath()) "torch-publish-$([guid]::NewGuid()).zip"
Set-Content $fixtureZip 'fixture'
try {
    $global:ExistingRegistration = @{}
    $global:ExpectedAction = 'upload'
    $global:Uploads = 0
    function Invoke-RestMethod {
        param($Uri, $Method, $Body, $TimeoutSec)
        if (!$Method) { return $global:ExistingRegistration }
        $Form = @{}
        foreach ($part in $Body) {
            $name = $part.Headers.ContentDisposition.Name.Trim('"')
            $Form[$name] = $part.ReadAsStringAsync().GetAwaiter().GetResult()
            if ($name -eq 'uploadZip' -and ($part.Headers.ContentType.MediaType -ne 'application/zip' -or !$part.Headers.ContentDisposition.FileName)) { throw 'ZIP MIME type or filename missing.' }
        }
        if ($Uri -ne 'https://torchapi.com/api/plugins/upload' -or $Method -ne 'Post' -or $Form.username -ne 'OwendB' -or $Form.secret -ne $env:TORCH_API_KEY -or $Form.action -ne $global:ExpectedAction -or !$Form.uploadZip) {
            throw 'Unexpected upload contract.'
        }
        if ($Form.action -eq 'upload' -and (!$Form.name -or !$Form.description -or $Form.isPrivate -ne 'false')) { throw 'Missing first-upload metadata.' }
        if ($Form.action -eq 'update' -and !$Form.updateNote) { throw 'Missing update note.' }
        $global:Uploads++
        return '{"success_message":"fixture uploaded"}'
    }
    & ./ci/Publish-Torch.ps1 -ZipPath $fixtureZip
    $global:ExistingRegistration = @{ guid = 'e9adbc4e-5b8c-4bca-a52d-d5cff2a4a9b0'; latestVersion = '0.0.1'; versions = @(@{ version = '0.0.1' }) }
    $global:ExpectedAction = 'update'
    & ./ci/Publish-Torch.ps1 -ZipPath $fixtureZip
    if ($global:Uploads -ne 2) { throw 'Expected initial upload and update.' }
    $global:ExistingRegistration.latestVersion = '9.0.0'
    try { & ./ci/Publish-Torch.ps1 -ZipPath $fixtureZip; throw 'Downgrade was accepted.' }
    catch { if ($_.Exception.Message -notlike '*older version*') { throw } }
    Write-Host 'PASS native multipart upload/update contract and downgrade guard'
} finally { Remove-Item $fixtureZip; Remove-Item Env:TORCH_API_KEY }
