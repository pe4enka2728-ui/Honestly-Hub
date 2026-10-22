# Windows PowerShell 5.1 WinRT bridge; ASCII source avoids legacy encoding issues.
$ErrorActionPreference = 'Stop'
$encoding = [System.Text.UTF8Encoding]::new($false)
[Console]::InputEncoding = $encoding
[Console]::OutputEncoding = $encoding
$OutputEncoding = $encoding
try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $storageType = [Windows.Storage.StorageFile, Windows.Storage, ContentType=WindowsRuntime]
    $streamType = [Windows.Storage.Streams.IRandomAccessStream, Windows.Storage.Streams, ContentType=WindowsRuntime]
    $decoderType = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Foundation, ContentType=WindowsRuntime]
    $bitmapType = [Windows.Graphics.Imaging.SoftwareBitmap, Windows.Foundation, ContentType=WindowsRuntime]
    $resultType = [Windows.Media.Ocr.OcrResult, Windows.Foundation, ContentType=WindowsRuntime]
    $engineType = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType=WindowsRuntime]
    $asTaskMethod = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1
    function Wait-WinRT($operation, [Type]$type) {
        $task = $asTaskMethod.MakeGenericMethod(@($type)).Invoke($null, @($operation))
        return $task.GetAwaiter().GetResult()
    }
    $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
    if ($null -eq $engine) { throw 'Windows OCR language pack is unavailable.' }
    [Console]::WriteLine('{"ready":true}')
} catch {
    [Console]::WriteLine((@{error=$_.Exception.Message} | ConvertTo-Json -Compress)); exit 1
}
while ($null -ne ($line = [Console]::ReadLine())) {
    $bitmap = $null
    $stream = $null
    try {
        $request = $line | ConvertFrom-Json
        $file = Wait-WinRT ([Windows.Storage.StorageFile]::GetFileFromPathAsync([string]$request.file)) $storageType
        $stream = Wait-WinRT ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) $streamType
        $decoder = Wait-WinRT ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) $decoderType
        $bitmap = Wait-WinRT ($decoder.GetSoftwareBitmapAsync([Windows.Graphics.Imaging.BitmapPixelFormat]::Bgra8,
            [Windows.Graphics.Imaging.BitmapAlphaMode]::Premultiplied)) $bitmapType
        $result = Wait-WinRT ($engine.RecognizeAsync($bitmap)) $resultType
        $lines = @($result.Lines | ForEach-Object {
            $words = @($_.Words)
            if ($words.Count -gt 0) {
                $left = ($words | ForEach-Object { $_.BoundingRect.X } | Measure-Object -Minimum).Minimum
                $top = ($words | ForEach-Object { $_.BoundingRect.Y } | Measure-Object -Minimum).Minimum
                $right = ($words | ForEach-Object { $_.BoundingRect.X + $_.BoundingRect.Width } | Measure-Object -Maximum).Maximum
                $bottom = ($words | ForEach-Object { $_.BoundingRect.Y + $_.BoundingRect.Height } | Measure-Object -Maximum).Maximum
                @{text=$_.Text; x=$left; y=$top; width=$right-$left; height=$bottom-$top}
            }
        })
        [Console]::WriteLine((@{lines=$lines} | ConvertTo-Json -Depth 5 -Compress))
    } catch { [Console]::WriteLine((@{error=$_.Exception.Message} | ConvertTo-Json -Compress)) }
    finally {
        if ($null -ne $bitmap) { $bitmap.Dispose() }
        if ($null -ne $stream) { $stream.Dispose() }
    }
}
