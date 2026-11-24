#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include "stream_mjpeg.h"
#include "unity.h"
#include "unity_fixture.h"

/** A connection that records what is written to it and can fail a given write. */
typedef struct {
    char data[512];
    size_t length;
    int writes;
    int fail_at; // 1-based index of the write that fails; 0 never fails
} fake_connection_t;

static fake_connection_t connection;

static int fake_write(void *context, const char *data, size_t length)
{
    fake_connection_t *target = context;
    target->writes++;
    if (target->writes == target->fail_at) return -1;
    TEST_ASSERT_LESS_OR_EQUAL_size_t(sizeof(target->data) - target->length, length);
    memcpy(target->data + target->length, data, length);
    target->length += length;
    return 0;
}

static const uint8_t JPEG[] = { 0xff, 0xd8, 0x01, 0x00, 0x02, 0xff, 0xd9 };
#define JPEG_HEADER "\r\n--mouthtrap-frame\r\nContent-Type: image/jpeg\r\nContent-Length: 7\r\n\r\n"

TEST_GROUP(mjpeg_part_header);
TEST_SETUP(mjpeg_part_header) {}
TEST_TEAR_DOWN(mjpeg_part_header) {}

TEST(mjpeg_part_header, AnyLength_WritesTheBoundaryTypeAndLength)
{
    // Given
    char buffer[160];
    size_t written = 0;
    // When
    mjpeg_error_t error = mjpeg_part_header(buffer, sizeof(buffer), 7, &written);
    // Then
    TEST_ASSERT_EQUAL(MJPEG_OK, error);
    TEST_ASSERT_EQUAL_size_t(strlen(JPEG_HEADER), written);
    TEST_ASSERT_EQUAL_STRING(JPEG_HEADER, buffer);
}

TEST(mjpeg_part_header, LargestLength_FitsTheBufferTheStreamUses)
{
    // Given
    char buffer[160];
    size_t written = 0;
    char expected[64];
    snprintf(expected, sizeof(expected), "Content-Length: %zu\r\n\r\n", SIZE_MAX);
    // When
    mjpeg_error_t error = mjpeg_part_header(buffer, sizeof(buffer), SIZE_MAX, &written);
    // Then
    TEST_ASSERT_EQUAL(MJPEG_OK, error);
    TEST_ASSERT_EQUAL_STRING(expected, buffer + written - strlen(expected));
}

TEST(mjpeg_part_header, BufferWithoutRoomForTheTerminator_ReturnsBufferTooSmall)
{
    // Given a buffer exactly as long as the header, so the terminator does not fit
    char buffer[sizeof(JPEG_HEADER) - 1];
    size_t written = 99;
    // When
    mjpeg_error_t error = mjpeg_part_header(buffer, sizeof(buffer), 7, &written);
    // Then
    TEST_ASSERT_EQUAL(MJPEG_BUFFER_TOO_SMALL, error);
    TEST_ASSERT_EQUAL_size_t(99, written);
}

TEST(mjpeg_part_header, BufferOfExactlyTheHeaderAndTerminator_ReturnsOk)
{
    // Given
    char buffer[sizeof(JPEG_HEADER)];
    size_t written = 0;
    // When
    mjpeg_error_t error = mjpeg_part_header(buffer, sizeof(buffer), 7, &written);
    // Then
    TEST_ASSERT_EQUAL(MJPEG_OK, error);
    TEST_ASSERT_EQUAL_STRING(JPEG_HEADER, buffer);
}

TEST_GROUP_RUNNER(mjpeg_part_header)
{
    RUN_TEST_CASE(mjpeg_part_header, AnyLength_WritesTheBoundaryTypeAndLength);
    RUN_TEST_CASE(mjpeg_part_header, LargestLength_FitsTheBufferTheStreamUses);
    RUN_TEST_CASE(mjpeg_part_header, BufferWithoutRoomForTheTerminator_ReturnsBufferTooSmall);
    RUN_TEST_CASE(mjpeg_part_header, BufferOfExactlyTheHeaderAndTerminator_ReturnsOk);
}

TEST_GROUP(mjpeg_send_frame);
TEST_SETUP(mjpeg_send_frame)
{
    memset(&connection, 0, sizeof(connection));
}
TEST_TEAR_DOWN(mjpeg_send_frame) {}

TEST(mjpeg_send_frame, Frame_WritesTheHeaderThenTheJpeg)
{
    // When
    mjpeg_error_t error = mjpeg_send_frame(JPEG, sizeof(JPEG), fake_write, &connection);
    // Then
    TEST_ASSERT_EQUAL(MJPEG_OK, error);
    TEST_ASSERT_EQUAL(2, connection.writes);
    TEST_ASSERT_EQUAL_size_t(strlen(JPEG_HEADER) + sizeof(JPEG), connection.length);
    TEST_ASSERT_EQUAL_MEMORY(JPEG_HEADER, connection.data, strlen(JPEG_HEADER));
    TEST_ASSERT_EQUAL_MEMORY(JPEG, connection.data + strlen(JPEG_HEADER), sizeof(JPEG));
}

TEST(mjpeg_send_frame, TwoFrames_EachStartsWithTheBoundaryOfTheContentType)
{
    // Given the boundary that a client reads from the Content-Type
    const char *parameter = strstr(MJPEG_CONTENT_TYPE, "boundary=");
    TEST_ASSERT_NOT_NULL(parameter);
    char delimiter[64];
    snprintf(delimiter, sizeof(delimiter), "\r\n--%s\r\n", parameter + strlen("boundary="));
    // When
    TEST_ASSERT_EQUAL(MJPEG_OK, mjpeg_send_frame(JPEG, sizeof(JPEG), fake_write, &connection));
    size_t second = connection.length;
    TEST_ASSERT_EQUAL(MJPEG_OK, mjpeg_send_frame(JPEG, 3, fake_write, &connection));
    // Then
    TEST_ASSERT_EQUAL_MEMORY(delimiter, connection.data, strlen(delimiter));
    TEST_ASSERT_EQUAL_MEMORY(delimiter, connection.data + second, strlen(delimiter));
    TEST_ASSERT_NOT_NULL(strstr(connection.data + second, "Content-Length: 3\r\n\r\n"));
}

TEST(mjpeg_send_frame, EmptyJpeg_WritesAZeroLengthPart)
{
    // When
    mjpeg_error_t error = mjpeg_send_frame(JPEG, 0, fake_write, &connection);
    // Then
    TEST_ASSERT_EQUAL(MJPEG_OK, error);
    connection.data[connection.length] = '\0';
    TEST_ASSERT_NOT_NULL(strstr(connection.data, "Content-Length: 0\r\n\r\n"));
}

TEST(mjpeg_send_frame, HeaderWriteFails_ReturnsWriteFailedWithoutSendingTheJpeg)
{
    // Given
    connection.fail_at = 1;
    // When
    mjpeg_error_t error = mjpeg_send_frame(JPEG, sizeof(JPEG), fake_write, &connection);
    // Then
    TEST_ASSERT_EQUAL(MJPEG_WRITE_FAILED, error);
    TEST_ASSERT_EQUAL(1, connection.writes);
    TEST_ASSERT_EQUAL_size_t(0, connection.length);
}

TEST(mjpeg_send_frame, JpegWriteFails_ReturnsWriteFailed)
{
    // Given
    connection.fail_at = 2;
    // When
    mjpeg_error_t error = mjpeg_send_frame(JPEG, sizeof(JPEG), fake_write, &connection);
    // Then
    TEST_ASSERT_EQUAL(MJPEG_WRITE_FAILED, error);
    TEST_ASSERT_EQUAL(2, connection.writes);
}

TEST_GROUP_RUNNER(mjpeg_send_frame)
{
    RUN_TEST_CASE(mjpeg_send_frame, Frame_WritesTheHeaderThenTheJpeg);
    RUN_TEST_CASE(mjpeg_send_frame, TwoFrames_EachStartsWithTheBoundaryOfTheContentType);
    RUN_TEST_CASE(mjpeg_send_frame, EmptyJpeg_WritesAZeroLengthPart);
    RUN_TEST_CASE(mjpeg_send_frame, HeaderWriteFails_ReturnsWriteFailedWithoutSendingTheJpeg);
    RUN_TEST_CASE(mjpeg_send_frame, JpegWriteFails_ReturnsWriteFailed);
}

TEST_GROUP(mjpeg_frame_delay_ms);
TEST_SETUP(mjpeg_frame_delay_ms) {}
TEST_TEAR_DOWN(mjpeg_frame_delay_ms) {}

TEST(mjpeg_frame_delay_ms, FrameSentEarly_WaitsForTheRestOfTheInterval)
{
    // Given a clock that has run for a day, and a frame sent in 17 ms
    const int64_t started = 86400LL * 1000000;
    // When / Then
    TEST_ASSERT_EQUAL_UINT32(50, mjpeg_frame_delay_ms(started, started + 17000));
}

TEST(mjpeg_frame_delay_ms, NoTimeElapsed_WaitsTheWholeInterval)
{
    // When / Then
    TEST_ASSERT_EQUAL_UINT32(MJPEG_FRAME_INTERVAL_US / 1000, mjpeg_frame_delay_ms(0, 0));
}

TEST(mjpeg_frame_delay_ms, AtMostOneMillisecondLeft_DoesNotWait)
{
    // Given elapsed times that leave 1000 us and 1 us of the interval
    static const int64_t elapsed[] = { MJPEG_FRAME_INTERVAL_US - 1000, MJPEG_FRAME_INTERVAL_US - 1 };
    for (size_t i = 0; i < sizeof(elapsed) / sizeof(elapsed[0]); i++) {
        // When / Then
        TEST_ASSERT_EQUAL_UINT32(0, mjpeg_frame_delay_ms(0, elapsed[i]));
    }
}

TEST(mjpeg_frame_delay_ms, JustOverOneMillisecondLeft_WaitsOneMillisecond)
{
    // When / Then
    TEST_ASSERT_EQUAL_UINT32(1, mjpeg_frame_delay_ms(0, MJPEG_FRAME_INTERVAL_US - 1001));
}

TEST(mjpeg_frame_delay_ms, FrameLate_DoesNotWait)
{
    static const int64_t elapsed[] = { MJPEG_FRAME_INTERVAL_US, MJPEG_FRAME_INTERVAL_US + 1, 10LL * 1000000 };
    for (size_t i = 0; i < sizeof(elapsed) / sizeof(elapsed[0]); i++) {
        // When / Then
        TEST_ASSERT_EQUAL_UINT32(0, mjpeg_frame_delay_ms(0, elapsed[i]));
    }
}

TEST_GROUP_RUNNER(mjpeg_frame_delay_ms)
{
    RUN_TEST_CASE(mjpeg_frame_delay_ms, FrameSentEarly_WaitsForTheRestOfTheInterval);
    RUN_TEST_CASE(mjpeg_frame_delay_ms, NoTimeElapsed_WaitsTheWholeInterval);
    RUN_TEST_CASE(mjpeg_frame_delay_ms, AtMostOneMillisecondLeft_DoesNotWait);
    RUN_TEST_CASE(mjpeg_frame_delay_ms, JustOverOneMillisecondLeft_WaitsOneMillisecond);
    RUN_TEST_CASE(mjpeg_frame_delay_ms, FrameLate_DoesNotWait);
}
