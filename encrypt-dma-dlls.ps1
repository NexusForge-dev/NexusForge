# Encrypt + compress new DMA DLLs into the format expected by ResourceCrypto.cs.
# Key derivation: SHA256("NexusForge-FPGA-DMA-2026-AES-KEY")
# IV  derivation: MD5  ("NexusForge-AES-IV-2026")
# Pipeline:  raw DLL  =>  GZip compress  =>  AES-256-CBC PKCS7 encrypt  =>  out file

$ErrorActionPreference = 'Stop'

# Sources
$Source = "$env:TEMP\memprocfs_new"
# Destination
$Dest = "D:\Firmware\NexusForge\src\NexusForge\Resources\dma"

# Files to update (matches NexusForge.csproj EmbeddedResource list + EmbeddedDlls array)
$Files = @(
    "vmm.dll",
    "leechcore.dll",
    "leechcore_driver.dll",
    "FTD3XX.dll",
    "FTD3XXWU.dll",
    "dbghelp.dll",
    "symsrv.dll",
    "tinylz4.dll",
    "vcruntime140.dll"
)

# --- Key + IV derivation (must match ResourceCrypto.cs exactly) ---
$keyStr = "NexusForge-FPGA-DMA-2026-AES-KEY"
$ivStr  = "NexusForge-AES-IV-2026"

$sha256 = [System.Security.Cryptography.SHA256]::Create()
$md5    = [System.Security.Cryptography.MD5]::Create()
$key = $sha256.ComputeHash([System.Text.Encoding]::ASCII.GetBytes($keyStr))
$iv  = $md5.ComputeHash([System.Text.Encoding]::ASCII.GetBytes($ivStr))
$sha256.Dispose()
$md5.Dispose()

Write-Host "Key (SHA256, 32B): $([BitConverter]::ToString($key) -replace '-','')"
Write-Host "IV  (MD5,    16B): $([BitConverter]::ToString($iv)  -replace '-','')"
Write-Host ""

function Encrypt-File {
    param([string]$inPath, [string]$outPath)

    # 1. Read raw bytes
    $raw = [System.IO.File]::ReadAllBytes($inPath)

    # 2. GZip compress (matches: new GZipStream(MemoryStream(compressed), Decompress) on decrypt side)
    $compressedStream = New-Object System.IO.MemoryStream
    $gz = New-Object System.IO.Compression.GZipStream($compressedStream, [System.IO.Compression.CompressionMode]::Compress, $true)
    $gz.Write($raw, 0, $raw.Length)
    $gz.Dispose()
    $compressed = $compressedStream.ToArray()
    $compressedStream.Dispose()

    # 3. AES-256-CBC PKCS7 encrypt
    $aes = [System.Security.Cryptography.Aes]::Create()
    $aes.Key = $key
    $aes.IV  = $iv
    $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
    $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
    $encryptor = $aes.CreateEncryptor()
    $encrypted = $encryptor.TransformFinalBlock($compressed, 0, $compressed.Length)
    $encryptor.Dispose()
    $aes.Dispose()

    # 4. Write
    [System.IO.File]::WriteAllBytes($outPath, $encrypted)

    return @{
        Raw = $raw.Length
        Compressed = $compressed.Length
        Encrypted = $encrypted.Length
    }
}

Write-Host ("{0,-22}  {1,-12}  {2,-12}  {3,-12}  {4}" -f "FILE", "RAW", "COMPRESSED", "ENCRYPTED", "OLD_SIZE")
Write-Host ("-" * 80)

foreach ($f in $Files) {
    $inPath  = Join-Path $Source $f
    $outPath = Join-Path $Dest   $f
    if (-not (Test-Path $inPath)) {
        Write-Host "[!] MISSING $inPath" -ForegroundColor Yellow
        continue
    }
    if (-not (Test-Path $outPath)) {
        Write-Host "[!] No destination $outPath (skipping)" -ForegroundColor Yellow
        continue
    }
    $oldSize = (Get-Item $outPath).Length
    $r = Encrypt-File -inPath $inPath -outPath $outPath
    Write-Host ("{0,-22}  {1,-12}  {2,-12}  {3,-12}  {4}" -f $f, $r.Raw, $r.Compressed, $r.Encrypted, $oldSize)
}

Write-Host ""
Write-Host "Done. Rebuild NexusForge to embed updated DLLs." -ForegroundColor Green
