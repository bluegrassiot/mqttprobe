# Vendor Provenance and License Notices

This directory contains vendored script snapshots used by the MQTTProbe AppImage
packaging pipeline. Each script is attributed to its upstream source with the
exact commit, hash, and license.

## linuxdeploy-plugin-gtk.sh

- **Source**: https://github.com/tauri-apps/tauri
- **Path**: `crates/tauri-bundler/src/bundle/linux/appimage/linuxdeploy-plugin-gtk.sh`
- **Commit**: `30da1fd6e17de6107ecc850c95dfb16b5729f2dd`
- **Original hash**: `ef6b9a980417243bc62e0241b51dc49876032afd1bab9b4762389f961b406d9b`
- **License**: Apache-2.0 OR MIT (Tauri project dual license)
- **Adaptation**: The blanket `sed` binary patch for libwebkit files at the end
  of the original script has been removed. MQTTProbe uses a narrow, checked
  relocation in `build-appimage.sh` that targets only the full
  `/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1` prefix in libwebkit* files,
  with ELF validation and occurrence counting. All other logic is unchanged.

## linuxdeploy-plugin-gstreamer.sh

- **Source**: https://github.com/tauri-apps/tauri
- **Path**: `crates/tauri-bundler/src/bundle/linux/appimage/linuxdeploy-plugin-gstreamer.sh`
- **Commit**: `30da1fd6e17de6107ecc850c95dfb16b5729f2dd`
- **Hash**: `2a15ce9da8de6e20159e1ab27861a7a5ef8758c81a6278ba4ab30cefa1d74c9f`
- **License**: Apache-2.0 OR MIT (Tauri project dual license)
- **Adaptation**: None. Exact snapshot.

## linuxdeploy (binary)

- **Source**: https://github.com/tauri-apps/binary-releases
- **Release**: `linuxdeploy-07333c6`
- **URL**: `https://github.com/tauri-apps/binary-releases/releases/download/linuxdeploy-07333c6/linuxdeploy-x86_64.AppImage`
- **SHA-256**: `36a2d7e274d12e1050d0e9ecfe11d339ed54720b2bec464c286d53f8b07f5c62`
- **License**: MIT (linuxdeploy project)

## Photino.Native

- **Source**: https://github.com/tryphotino/photino.Native
- **Commit**: `3ba4b937d6337b5c58344445db08a5a9a63f07c8` (v4.0.22)
- **License**: Apache-2.0
- **Usage**: Rebuilt from source on ubuntu-22.04 to restore glibc 2.35
  compatibility. The published NuGet .so requires glibc 2.38+.
- **Source archive**: Included in the third-party source artifact alongside
  the binary AppImage.

## Modified Bundled LGPL Binaries

The AppImage bundles `libwebkit2gtk-4.1.so.0` and related WebKit libraries
from the Ubuntu 22.04 `libwebkit2gtk-4.1-0` package. These are LGPL-2.1+.

### Relocation procedure

The build applies a narrow binary patch replacing the absolute prefix
`/usr/lib/x86_64-linux-gnu/webkit2gtk-4.1` (40 bytes) with
`././/lib/x86_64-linux-gnu/webkit2gtk-4.1` (40 bytes) in libwebkit* ELF
files only. Both prefixes are exactly 40 bytes; no padding or truncation
occurs. File size and ELF header are validated post-patch. This is the
minimum modification required for the AppImage runtime to locate bundled
WebKit helpers and the injected bundle.

### Corresponding source

The corresponding source for the modified WebKit libraries is available from
the Ubuntu 22.04 archive:

```bash
apt-get source libwebkit2gtk-4.1-0
```

Or from https://packages.ubuntu.com/jammy/

## Ubuntu 22.04 Jammy Package Sources

The following Ubuntu 22.04 (Jammy Jellyfish) packages are bundled or their
libraries are included in the AppImage. The list covers all libraries that
linuxdeploy and the GTK/GStreamer plugins pull into the AppDir.

### Core bundled packages

| Package | License | Source |
|---------|---------|--------|
| libwebkit2gtk-4.1-0 | LGPL-2.1+ | `apt-get source webkit2gtk` |
| libgtk-3-0 | LGPL-2.1+ | `apt-get source gtk+3.0` |
| libgstreamer1.0-0 | LGPL-2.0+ | `apt-get source gstreamer1.0` |
| gstreamer1.0-plugins-base | LGPL-2.0+ | `apt-get source gst-plugins-base1.0` |
| gstreamer1.0-plugins-good | LGPL-2.0+ | `apt-get source gst-plugins-good1.0` |
| librsvg2-common | LGPL-2.1+ | `apt-get source librsvg` |
| gobject-introspection | LGPL-2.0+, GPL-2.0+ | `apt-get source gobject-introspection` |

### Transitive dependencies bundled by linuxdeploy

These libraries are pulled in transitively by linuxdeploy when resolving
DT_NEEDED dependencies of the core packages above.

| Library | License | Source package |
|---------|---------|---------------|
| libharfbuzz-icu.so.0 | MIT | `harfbuzz` |
| libcairo.so.2 | MPL-1.1/LGPL-2.1+ | `cairo` |
| libpango-1.0.so.0 | LGPL-2.0+ | `pango1.0` |
| libgdk-3.so.0 | LGPL-2.1+ | `gtk+3.0` |
| libjavascriptcoregtk-4.1.so.0 | LGPL-2.1+ | `webkit2gtk` |
| libsoup-3.0.so.0 | LGPL-2.0+ | `libsoup3.0` |
| libglib-2.0.so.0 | LGPL-2.0+ | `glib2.0` |
| libgobject-2.0.so.0 | LGPL-2.0+ | `glib2.0` |
| libgio-2.0.so.0 | LGPL-2.0+ | `glib2.0` |
| libgdk_pixbuf-2.0.so.0 | LGPL-2.0+ | `gdk-pixbuf` |
| libpangocairo-1.0.so.0 | LGPL-2.0+ | `pango1.0` |
| libpangoft2-1.0.so.0 | LGPL-2.0+ | `pango1.0` |
| librsvg-2.so.2 | LGPL-2.1+ | `librsvg` |
| libgstgl-1.0.so.0 | LGPL-2.0+ | `gst-plugins-base1.0` |
| libicudata.so.70 / libicuuc.so.70 / libicui18n.so.70 | ICU | `icu` |
| libxml2.so.2 | MIT | `libxml2` |
| libxslt.so.1 | MIT | `libxslt` |
| libpng16.so.16 | libpng | `libpng1.6` |
| libjpeg.so.8 | IJG/libjpeg | `libjpeg-turbo` |
| libtiff.so.5 | MIT/libtiff | `tiff` |
| libwebp.so.7 | BSD | `libwebp` |
| libvpx.so.7 | BSD | `libvpx` |
| libopus.so.0 | BSD | `opus` |
| libvorbis.so.0 / libvorbisenc.so.2 | BSD | `libvorbis` |
| libogg.so.0 | BSD | `libogg` |
| libtheora.so.0 / libtheoraenc.so.1 / libtheoradec.so.1 | BSD | `libtheora` |
| libspeex.so.1 | BSD | `speex` |
| libFLAC.so.8 | BSD/GPL | `flac` |
| libmp3lame.so.0 / libmpg123.so.0 | LGPL/GPL | `lame` / `mpg123` |
| libpulse.so.0 | LGPL-2.1+ | `pulseaudio` |
| libcairo-gobject.so.2 | LGPL-2.0+ | `cairo` |
| libepoxy.so.0 | MIT | `libepoxy` |
| libatk-1.0.so.0 / libatk-bridge-2.0.so.0 | LGPL-2.0+ | `at-spi2-core` / `atk1.0` |
| libwayland-client.so.0 / libwayland-cursor.so.0 / libwayland-egl.so.1 | MIT | `wayland` |
| libxkbcommon.so.0 | MIT | `libxkbcommon` |
| libdbus-1.so.3 | AFL-2.1/MIT | `dbus` |
| libfontconfig.so.1 | MIT | `fontconfig` |
| libfreetype.so.6 | FTL/GPL-2.0+ | `freetype` |
| libfribidi.so.0 | LGPL-2.1+ | `fribidi` |
| libexpat.so.1 | MIT | `expat` |
| libharfbuzz.so.0 | MIT | `harfbuzz` |
| libnotify.so.4 | LGPL-2.1+ | `libnotify` |
| libsecret-1.so.0 | LGPL-2.1+ | `libsecret` |
| libgnutls.so.30 | LGPL-2.1+ | `gnutls28` |
| libgcrypt.so.20 | LGPL-2.1+ | `libgcrypt20` |
| libnettle.so.8 | LGPL-2.1+ / GPL-2.0+ | `nettle` |
| liborc-0.4.so.0 | BSD | `orc` |

### System runtime libraries (not bundled)

The AppImage expects these to be provided by the host system's desktop
runtime environment (any standard Linux desktop with X11 or Wayland):

- libX11.so.6, libxcb.so.1, libXext.so.6, and other X11 libraries
- libGL.so.1, libEGL.so.1, libGLESv2.so.2 (OpenGL/EGL/GLES)
- libdrm.so.2, libgbm.so.1 (DRM/GBM)
- libwayland-client.so.0 (Wayland)
- libexpat.so.1 (XML)
- libxkbcommon.so.0 (keyboard)

Font/text stack libraries (harfbuzz, fontconfig, freetype, fribidi) are
bundled inside the AppImage so it works on minimal hosts without a full
desktop font installation. GPU drivers are not bundled (standard baseline).

Source code for these packages can be obtained via `apt-get source <package-name>`
on an Ubuntu 22.04 system with source repositories enabled, or from
https://packages.ubuntu.com/jammy/.

## Deliberate Exclusions

### libcoreclrtraceptprovider.so (LTTng tracing provider)

The .NET runtime publishes `libcoreclrtraceptprovider.so`, an optional native
LTTng tracing provider. This library links `liblttng-ust.so.0`, which does
not exist on Ubuntu 22.04 (the system ships `liblttng-ust.so.1`). The library
is deliberately excluded from the AppImage.

**Impact**: Native LTTng tracing sinks are unavailable. .NET EventPipe and
the coreclr runtime are fully retained. The application functions without
any behavioral difference. Only the optional native LTTng export path is lost.

This exclusion is documented in the packaging manifest, tested in the
packaging test suite, and noted in the AppImage verification checks.

## Third-Party Source Archive

A companion source archive (`mqttprobe-third-party-sources-v*.tar.gz`) is
uploaded alongside the binary AppImage artifact. It contains:
- Photino.Native source at the pinned v4.0.22 commit (Apache-2.0)
- Vendored script snapshots with provenance
- Build and relocation scripts (font bundling is inlined in appdir.sh)
- Package version manifest from staged AppDir (actual dpkg-query results)
- Common license texts from /usr/share/common-licenses/
- Per-package copyright files from /usr/share/doc/<pkg>/copyright
- Ubuntu 22.04 source archives (.dsc, .orig.tar.*, .debian.tar.*)
- WebKit modification notice