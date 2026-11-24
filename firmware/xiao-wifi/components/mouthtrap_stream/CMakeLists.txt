# Settings validation, stream authentication and MJPEG framing. It uses no hardware or ESP-IDF service,
# so test/host runs it on the build machine.
idf_component_register(SRCS "settings.c" "auth.c" "mjpeg.c" INCLUDE_DIRS "include")
