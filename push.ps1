$filename = gci "./src/sharpArtillery/nupkg/*.nupkg" | `
                sort LastWriteTime | `
                select -last 1 | `
                select -ExpandProperty "Name"
$len = $filename.length

if ($len -gt 0) {
    dotnet nuget push  "./src/sharpArtillery/nupkg\$filename" -k oy2dvf3xgfpmei3gpyla4jmdhdq3pftuhe3yc53vnlzygi -s nuget.org
}