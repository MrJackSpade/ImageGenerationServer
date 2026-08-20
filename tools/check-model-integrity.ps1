#requires -Version 7.0
<#
  check-model-integrity.ps1
  --------------------------------------------------------------------------
  Proves whether every model file under the models root is COMPLETE, rather than trusting that a
  download which exited cleanly wrote the whole thing.

  Both container formats declare, for every tensor, exactly where its bytes end. So a file is complete
  precisely when the furthest of those ends lands on its last byte, and anything else is short by a
  number this prints.

  This matters because a truncated model never says so. It surfaces from inside a loader as
  "shape '[1280, 2560, 3, 3]' is invalid for input of size 26292626", or "cannot reshape array of size
  24819168 into shape (12288,2520)", or an mmap that will not allocate. Each of those reads like a bug
  in the application and none of them is. Size alone does not catch it either: a 6.6 GB checkpoint
  missing its last gigabyte still looks about right.

  Run it after any bulk download, and before believing a test run that used those weights.
#>
[CmdletBinding()]
param(
    [string] $Root = 'E:\AI\models'
)

$ErrorActionPreference = 'Stop'

function Compare-Expected([long] $Expected, [IO.FileInfo] $File) {
    if ($Expected -eq $File.Length) { return @{ Verdict = 'ok'; Detail = '' } }
    $short = $Expected - $File.Length
    if ($short -gt 0) {
        return @{ Verdict = 'TRUNCATED'
                  Detail  = "needs $Expected bytes, has $($File.Length) - short by $([math]::Round($short/1MB)) MB" }
    }
    return @{ Verdict = 'OVERLONG'; Detail = "declares $Expected bytes but the file is $($File.Length)" }
}

# --- safetensors --------------------------------------------------------------------------------

function Test-Safetensors {
    param([Parameter(Mandatory)][IO.FileInfo] $File)

    $fs = [IO.File]::Open($File.FullName, 'Open', 'Read', 'ReadWrite')
    try {
        $lenBytes = [byte[]]::new(8)
        if ($fs.Read($lenBytes, 0, 8) -ne 8) { return @{ Verdict = 'BROKEN'; Detail = 'no header length' } }
        $headerSize = [BitConverter]::ToUInt64($lenBytes, 0)
        if ($headerSize -ge [uint64]$File.Length) {
            return @{ Verdict = 'BROKEN'; Detail = "header claims $headerSize bytes, file is $($File.Length)" }
        }

        $json = [byte[]]::new($headerSize)
        $read = 0
        while ($read -lt $headerSize) {
            $n = $fs.Read($json, $read, $headerSize - $read)
            if ($n -le 0) { return @{ Verdict = 'BROKEN'; Detail = "header ended at $read of $headerSize" } }
            $read += $n
        }

        $header = [Text.Encoding]::UTF8.GetString($json) | ConvertFrom-Json
        $max = 0L
        foreach ($p in $header.PSObject.Properties) {
            if ($p.Name -eq '__metadata__') { continue }
            $off = $p.Value.data_offsets
            if ($off -and $off[1] -gt $max) { $max = [long]$off[1] }
        }
        return Compare-Expected (8 + [long]$headerSize + $max) $File
    }
    finally { $fs.Dispose() }
}

# --- gguf ---------------------------------------------------------------------------------------

<#
  GGML block sizes: a quantised tensor packs `block` elements into `size` bytes, so a tensor occupies
  (elements / block) * size. These pairs belong to the format rather than to any convention here, and
  one wrong entry would mis-measure an entire file, so they are listed outright.
#>
$GgmlTypes = @{
    0  = @(1, 4);     1  = @(1, 2);     2  = @(32, 18);   3  = @(32, 20)
    6  = @(32, 22);   7  = @(32, 24);   8  = @(32, 34);   9  = @(32, 36)
    10 = @(256, 84);  11 = @(256, 110); 12 = @(256, 144); 13 = @(256, 176)
    14 = @(256, 210); 15 = @(256, 292); 16 = @(256, 66);  17 = @(256, 74)
    18 = @(256, 98);  19 = @(256, 50);  20 = @(32, 18);   21 = @(256, 110)
    22 = @(256, 82);  23 = @(256, 136); 24 = @(1, 1);     25 = @(1, 2)
    26 = @(1, 4);     27 = @(1, 8);     28 = @(1, 8);     29 = @(256, 56)
    30 = @(1, 2)
}

function Read-GgufString([IO.BinaryReader] $br) {
    $len = $br.ReadUInt64()
    return [Text.Encoding]::UTF8.GetString($br.ReadBytes([int]$len))
}

function Skip-GgufValue([IO.BinaryReader] $br, [uint32] $type) {
    switch ($type) {
        0  { $null = $br.ReadByte() }
        1  { $null = $br.ReadSByte() }
        2  { $null = $br.ReadUInt16() }
        3  { $null = $br.ReadInt16() }
        4  { $null = $br.ReadUInt32() }
        5  { $null = $br.ReadInt32() }
        6  { $null = $br.ReadSingle() }
        7  { $null = $br.ReadByte() }
        8  { $null = Read-GgufString $br }
        9  {
            $elem = $br.ReadUInt32(); $count = $br.ReadUInt64()
            for ($i = 0UL; $i -lt $count; $i++) { Skip-GgufValue $br $elem }
        }
        10 { $null = $br.ReadUInt64() }
        11 { $null = $br.ReadInt64() }
        12 { $null = $br.ReadDouble() }
        default { throw "unknown GGUF metadata value type $type" }
    }
}

function Test-Gguf {
    param([Parameter(Mandatory)][IO.FileInfo] $File)

    $fs = [IO.File]::Open($File.FullName, 'Open', 'Read', 'ReadWrite')
    $br = [IO.BinaryReader]::new($fs)
    try {
        if ([Text.Encoding]::ASCII.GetString($br.ReadBytes(4)) -ne 'GGUF') {
            return @{ Verdict = 'BROKEN'; Detail = 'not a GGUF file' }
        }
        $null = $br.ReadUInt32()                      # format version
        $tensorCount = $br.ReadUInt64()
        $kvCount = $br.ReadUInt64()

        # Alignment is itself a metadata key and it decides where the data section begins, so it is read
        # rather than assumed. 32 is the format's default when the key is absent.
        $alignment = 32L
        for ($i = 0UL; $i -lt $kvCount; $i++) {
            $key = Read-GgufString $br
            $type = $br.ReadUInt32()
            if ($key -eq 'general.alignment' -and $type -eq 4) { $alignment = [long]$br.ReadUInt32() }
            else { Skip-GgufValue $br $type }
        }

        $max = 0L
        for ($i = 0UL; $i -lt $tensorCount; $i++) {
            $null = Read-GgufString $br               # tensor name
            $dims = $br.ReadUInt32()
            $elements = 1L
            for ($d = 0; $d -lt $dims; $d++) { $elements *= [long]$br.ReadUInt64() }
            $type = $br.ReadUInt32()
            $offset = [long]$br.ReadUInt64()

            if (-not $GgmlTypes.ContainsKey([int]$type)) {
                return @{ Verdict = 'BROKEN'; Detail = "tensor $i uses unknown ggml type $type" }
            }
            $block, $size = $GgmlTypes[[int]$type]
            $end = $offset + ($elements / $block) * $size
            if ($end -gt $max) { $max = $end }
        }

        $dataStart = [long]([math]::Ceiling($br.BaseStream.Position / [double]$alignment) * $alignment)
        return Compare-Expected ($dataStart + $max) $File
    }
    finally { $br.Dispose(); $fs.Dispose() }
}

# --- run ----------------------------------------------------------------------------------------

try {
    # This list is the integrity gate's universe. An unreadable directory must not silently disappear from it.
    $files = @(Get-ChildItem -LiteralPath $Root -Force -Recurse -File -ErrorAction Stop |
        Where-Object { $_.Extension -in '.safetensors', '.gguf' })
}
catch {
    # Write-Error is terminating under this script's ErrorActionPreference=Stop and would replace the deliberate
    # operational-failure code with PowerShell's generic 1 before ui-smoke-ready can distinguish it.
    [Console]::Error.WriteLine("Could not completely enumerate model root '$Root': $($_.Exception.Message)")
    exit 2
}
"checking $($files.Count) model files under $Root"

$bad = 0
foreach ($f in $files) {
    $r = try {
        if ($f.Extension -eq '.gguf') { Test-Gguf $f } else { Test-Safetensors $f }
    }
    catch { @{ Verdict = 'UNREADABLE'; Detail = $_.Exception.Message } }

    if ($r.Verdict -ne 'ok') {
        $bad++
        "  {0,-11} {1}" -f $r.Verdict, $f.FullName.Substring($Root.Length + 1)
        "              {0}" -f $r.Detail
    }
}

""
if ($bad) { "$bad file(s) are not intact"; exit 1 } else { "all intact"; exit 0 }
