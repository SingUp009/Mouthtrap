#include "unity.h"
#include "unity_fixture.h"

static void run_all_tests(void)
{
    RUN_TEST_GROUP(settings_validate);
    RUN_TEST_GROUP(settings_error_text);
    RUN_TEST_GROUP(auth_check);
    RUN_TEST_GROUP(mjpeg_part_header);
    RUN_TEST_GROUP(mjpeg_send_frame);
    RUN_TEST_GROUP(mjpeg_frame_delay_ms);
}

void app_main(void)
{
    // Exits with the number of failed tests.
    UNITY_MAIN_FUNC(run_all_tests);
}
