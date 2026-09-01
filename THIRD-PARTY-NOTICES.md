# Third-party notices

## 7-Zip 26.02

StowCrate release packaging uses the official 7-Zip 26.02 console executable, copyright Igor Pavlov, released 2026-06-25.

- Project and binary downloads: https://www.7-zip.org/
- Source release: https://github.com/ip7z/7zip/releases/tag/26.02
- License text: https://www.7-zip.org/license.txt

Most 7-Zip code is licensed under GNU LGPL 2.1 or later. Some portions use BSD 3-clause, BSD 2-clause, and the documented unRAR restriction. Binary redistributions must reproduce the related license information. Release packaging must include the upstream `License.txt` alongside this notice and retain a source link for the pinned version.

StowCrate does not modify or use the unRAR sources to recreate the proprietary RAR compression algorithm.

## ZstdSharp.Port 0.8.8

The managed TarZstd backend uses ZstdSharp.Port 0.8.8 under the MIT License. This release ports upstream Zstandard 1.5.7 and is used through its streaming compression/decompression API.

- Package: https://www.nuget.org/packages/ZstdSharp.Port/0.8.8
- Project and MIT license: https://github.com/oleg-st/ZstdSharp
- Upstream Zstandard license: https://github.com/facebook/zstd/blob/v1.5.7/LICENSE
