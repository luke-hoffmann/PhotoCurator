# Third-party notices

PhotoCurator includes or uses the following open-source software.

| Component | Use | License |
|---|---|---|
| [Magick.NET](https://github.com/dlemstra/Magick.NET) | .NET bindings for ImageMagick | Apache-2.0 |
| [ImageMagick](https://imagemagick.org/) | Reads photos, thumbnails, web export (via Magick.NET's native library) | [ImageMagick License](https://imagemagick.org/license/) |
| [libheif](https://github.com/strukturag/libheif) and [libde265](https://github.com/strukturag/libde265) | HEIC/HEIF decoding, inside ImageMagick | LGPL-3.0 |
| Other ImageMagick delegate libraries (libjpeg-turbo, libpng, libwebp, libtiff, lcms, zlib and more) | Image formats and color management, inside ImageMagick | BSD/MIT/zlib-style, listed in Magick.NET's `Notice.txt` |
| [YamlDotNet](https://github.com/aaubry/YamlDotNet) | Reads and writes the `.md` sidecars' front matter | MIT |
| [Microsoft Edge WebView2 SDK](https://www.nuget.org/packages/Microsoft.Web.WebView2) | Shows the app's UI in its window | BSD-3-Clause-style (Microsoft) |
| [Playwright](https://playwright.dev/) (development only, `tools/e2e`) | Browser tests of the UI | Apache-2.0 |

The license texts are available at the project links above. Magick.NET's package ships the complete notice for
ImageMagick and every library bundled with it as `Notice.txt` (in the NuGet cache, for example
`~/.nuget/packages/magick.net-q8-anycpu/15.0.0/Notice.txt`). If you share `PhotoCurator.exe` with others, include
that file with it.
