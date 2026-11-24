#include <stdio.h>
#include "stream_mjpeg.h"

mjpeg_error_t mjpeg_part_header(char *buffer, size_t size, size_t jpeg_length, size_t *written)
{
    int length = snprintf(buffer, size, "\r\n--" MJPEG_BOUNDARY "\r\nContent-Type: image/jpeg\r\nContent-Length: %zu\r\n\r\n",
                          jpeg_length);
    if (length < 0 || (size_t)length >= size) return MJPEG_BUFFER_TOO_SMALL;
    *written = (size_t)length;
    return MJPEG_OK;
}

mjpeg_error_t mjpeg_send_frame(const uint8_t *jpeg, size_t length, mjpeg_write_t write, void *context)
{
    char header[160];
    size_t header_length;
    mjpeg_error_t error = mjpeg_part_header(header, sizeof(header), length, &header_length);
    if (error != MJPEG_OK) return error;
    if (write(context, header, header_length) != 0) return MJPEG_WRITE_FAILED;
    if (write(context, (const char *)jpeg, length) != 0) return MJPEG_WRITE_FAILED;
    return MJPEG_OK;
}

uint32_t mjpeg_frame_delay_ms(int64_t started_us, int64_t now_us)
{
    int64_t remaining = MJPEG_FRAME_INTERVAL_US - (now_us - started_us);
    // Waits shorter than a millisecond, and frames that are already late, go out at once.
    return remaining > 1000 ? (uint32_t)(remaining / 1000) : 0;
}
