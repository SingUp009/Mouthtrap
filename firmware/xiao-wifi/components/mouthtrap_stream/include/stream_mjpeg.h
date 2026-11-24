#pragma once

#include <stddef.h>
#include <stdint.h>

#define MJPEG_BOUNDARY "mouthtrap-frame"
#define MJPEG_CONTENT_TYPE "multipart/x-mixed-replace; boundary=" MJPEG_BOUNDARY
/** About 15 frames per second. */
#define MJPEG_FRAME_INTERVAL_US 67000

typedef enum {
    MJPEG_OK = 0,
    MJPEG_BUFFER_TOO_SMALL, // the part header does not fit the buffer
    MJPEG_WRITE_FAILED,     // the connection did not take the data
} mjpeg_error_t;

/** Sends bytes to the client; returns 0 on success. */
typedef int (*mjpeg_write_t)(void *context, const char *data, size_t length);

/** Write the multipart header that precedes a JPEG of the given length. On success, *written is its length. */
mjpeg_error_t mjpeg_part_header(char *buffer, size_t size, size_t jpeg_length, size_t *written);

/** Send one frame: the part header, then the JPEG. Stops at the first failed write. */
mjpeg_error_t mjpeg_send_frame(const uint8_t *jpeg, size_t length, mjpeg_write_t write, void *context);

/** How long to wait before the next frame, in milliseconds, so that frames come once per interval. */
uint32_t mjpeg_frame_delay_ms(int64_t started_us, int64_t now_us);
