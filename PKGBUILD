# Maintainer: EMeowAGENT <ccendercatcc@gmail.com>
# Personal rolling fork of Project-Babble/Baballonia for Arch Linux.
# Build artifacts are intentionally kept out of this repository; use:
#   BUILDDIR="$HOME/build/baballonia" makepkg -si

pkgname=baballonia-git
pkgver=r0.0000000
pkgrel=1
pkgdesc="Cross-platform, hardware-agnostic XR eye and face tracking (personal Linux fork)"
arch=('x86_64')
url="https://github.com/EMeowAGENT/Baballonia"
license=('LicenseRef-Babble Software Distribution License 1.0')

depends=(
    gstreamer
    gst-plugins-base-libs
    glib2
    libjpeg-turbo
    fontconfig
    libglvnd
    lttng-ust
    hicolor-icon-theme
    xdg-utils
    onnxruntime-cuda
    espflash
    babbletrainer
    babblecalibration-bin
)
makedepends=(
    dotnet-sdk
    git
    ca-certificates
    unzip
)
provides=('baballonia')
conflicts=('baballonia' 'baballonia-git')

source=("$pkgname::git+https://github.com/EMeowAGENT/Baballonia.git#branch=pkg")
sha256sums=('SKIP')

pkgver() {
    cd "$pkgname"
    printf 'r%s.%s' "$(git rev-list --count HEAD)" "$(git rev-parse --short=7 HEAD)"
}

prepare() {
    cd "$pkgname"
    git submodule update --init --recursive
}

build() {
    cd "$pkgname/src/Baballonia.Desktop"
    export DOTNET_CLI_TELEMETRY_OPTOUT=1
    export DOTNET_NOLOGO=1
    dotnet publish -r linux-x64 -c Release --self-contained -f net10.0
}

package() {
    cd "$pkgname"

    local _publish="src/Baballonia.Desktop/bin/Release/net10.0/linux-x64/publish"

    install -d "$pkgdir/opt/$pkgname"
    cp -a "$_publish/." "$pkgdir/opt/$pkgname/"

    # Use the system onnxruntime so that a CUDA-enabled provider can be used.
    rm -f "$pkgdir/opt/$pkgname/libonnxruntime"*

    # Trainer: use the source-based AUR package instead of the bundled
    # PyInstaller CPU-only binary.
    install -d "$pkgdir/opt/$pkgname/Calibration/Linux/Trainer"
    rm -f "$pkgdir/opt/$pkgname/Calibration/Linux/Trainer/BabbleTrainer"
    ln -s /opt/babbletrainer/babbletrainer.sh \
        "$pkgdir/opt/$pkgname/Calibration/Linux/Trainer/BabbleTrainer"

    # Calibration overlay: use the system package.
    rm -rf "$pkgdir/opt/$pkgname/Calibration/Linux/Overlay"
    ln -s /opt/babblecalibration "$pkgdir/opt/$pkgname/Calibration/Linux/Overlay"

    # Firmware flashing: use the distro espflash.
    install -d "$pkgdir/opt/$pkgname/Firmware/Linux"
    ln -sf /usr/bin/espflash "$pkgdir/opt/$pkgname/Firmware/Linux/espflash"

    install -Dm755 "$srcdir/$pkgname/baballonia.sh" "$pkgdir/usr/bin/baballonia"
    install -Dm644 "$srcdir/$pkgname/baballonia.desktop" \
        "$pkgdir/usr/share/applications/baballonia.desktop"

    install -Dm644 "$_publish/Assets/Icon_512x512.png" \
        "$pkgdir/usr/share/icons/hicolor/512x512/apps/baballonia.png"
    install -Dm644 "$_publish/Assets/Icon_32x32.ico" \
        "$pkgdir/usr/share/icons/hicolor/32x32/apps/baballonia.ico"

    install -Dm644 LICENSE "$pkgdir/usr/share/licenses/$pkgname/LICENSE"
}
