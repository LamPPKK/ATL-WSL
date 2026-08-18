# ATL-WSL intentionally uses Mesa llvmpipe on Alpine/musl.
export LIBGL_ALWAYS_SOFTWARE=1
export GALLIUM_DRIVER=llvmpipe
export MESA_LOADER_DRIVER_OVERRIDE=llvmpipe
export GDK_BACKEND=wayland,x11

if [ -z "${PULSE_SERVER:-}" ] && [ -S /mnt/wslg/PulseServer ]; then
    export PULSE_SERVER=unix:/mnt/wslg/PulseServer
fi
