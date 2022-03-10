$filename = gci "./src/sharpArtillery/nupkg/*.nupkg" | `
                sort LastWriteTime | `
                select -last 1 | `
                select -ExpandProperty "Name"
$len = $filename.length

if ($len -gt 0) {
    # sign nuget?
#    nuget sign  "./src/sharpArtillery/nupkg\$filename"   `
#          -CertificateSubject "Nyviken" `
#          -timestamper " http://timestamp.comodoca.com"

    dotnet nuget push  "./src/sharpArtillery/nupkg\$filename" -source nuget.org
}