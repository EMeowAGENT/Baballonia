# Maintainer: EMeowAGENT <ccendercatcc@gmail.com>
# Personal rolling fork of Project-Babble/Baballonia for Arch Linux.
# Build artifacts are intentionally kept out of this repository; use:
#   BUILDDIR="$HOME/build/baballonia" makepkg -si

pkgname=baballonia-git
pkgver=r627.a903946
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
    hicolor-icon-theme
    xdg-utils
    onnxruntime-cuda
    espflash
    babbletrainer
    babblecalibration-bin
)
optdepends=(
    'lttng-ust: CoreCLR tracing'
)
makedepends=(
    dotnet-sdk
    git
    ca-certificates
    unzip
)
provides=('baballonia')
conflicts=('baballonia')

source=("$pkgname::git+https://github.com/EMeowAGENT/Baballonia.git#branch=pkg")
sha256sums=('SKIP')

pkgver() {
    cd "$pkgname"
    printf 'r%s.%s' "$(git rev-list --count HEAD)" "$(git rev-parse --short=7 HEAD)"
}

prepare() {
    cd "$pkgname"
    # Only HyperText.Avalonia is referenced by the desktop build; the
    # VRCFaceTracking and espflash submodules are only needed for their own
    # modules and would otherwise pull a lot of unrelated history.
    git submodule update --init src/HyperText.Avalonia
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

    # Trainer: ship a patched copy of the AUR trainer scripts so that
    # unilateral blink labels are trained independently per eye. The
    # babble_data C++ module and Python dependencies still come from the
    # babbletrainer package.
    install -d "$pkgdir/opt/$pkgname/Calibration/Linux/Trainer/runtime"
    install -Dm644 "$srcdir/$pkgname/trainer/main.py" \
        "$pkgdir/opt/$pkgname/Calibration/Linux/Trainer/runtime/main.py"
    install -Dm644 "$srcdir/$pkgname/trainer/models.py" \
        "$pkgdir/opt/$pkgname/Calibration/Linux/Trainer/runtime/models.py"
    install -Dm644 "$srcdir/$pkgname/trainer/trainer_distsampler.py" \
        "$pkgdir/opt/$pkgname/Calibration/Linux/Trainer/runtime/trainer_distsampler.py"
    install -Dm755 "$srcdir/$pkgname/trainer/BabbleTrainer.sh" \
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
