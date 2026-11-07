# Third-party source references

ToolHub's Windows package includes the .NET runtime, Magick.NET / native ImageMagick, PdfPig, and separate, unmodified FFmpeg / FFprobe programs. Their licenses remain applicable to those components.

## FFmpeg / FFprobe 9.0.2

- Binary distributor: https://www.gyan.dev/ffmpeg/builds/
- Exact binary archive: https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip
- Archive SHA-256: `60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba`
- Source revision: https://github.com/FFmpeg/FFmpeg/commit/946fcce07b
- Source archive: https://github.com/FFmpeg/FFmpeg/archive/946fcce07b.tar.gz
- License: GPLv3. Its full text is in `tools/ffmpeg/LICENSE` inside the Windows package.
- The distributor's complete configuration, enabled components and external library versions are in `tools/ffmpeg/README.txt` inside the Windows package. No FFmpeg executable or library has been modified by ToolHub.

The release page provides the FFmpeg source archive alongside the Windows download. FFmpeg is invoked as a separate process; the ToolHub source archive includes both Windows and Android applications.

## Managed and native image / PDF components

- Magick.NET 15.1.0: https://github.com/dlemstra/Magick.NET/tree/15.1.0
- Magick.NET native dependencies and source links: `licenses/Magick.NET-ImageMagick-NOTICE.txt` inside the Windows package.
- PdfPig 0.1.16: https://github.com/UglyToad/PdfPig/tree/v0.1.16
- .NET runtime 8.0.33: https://github.com/dotnet/runtime
- Windows Desktop runtime: https://github.com/dotnet/wpf and https://github.com/dotnet/winforms
- ASP.NET Core runtime: https://github.com/dotnet/aspnetcore

License texts and third-party runtime notices are included in the Windows package's `licenses` directory. Additional interoperability attribution is in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md).
