#include <stdio.h>
#include <string.h>
#include "freertos/FreeRTOS.h"
#include "freertos/event_groups.h"
#include "freertos/task.h"
#include "esp_camera.h"
#include "esp_event.h"
#include "esp_http_server.h"
#include "esp_log.h"
#include "esp_netif.h"
#include "esp_timer.h"
#include "esp_wifi.h"
#include "nvs_flash.h"

static const char *TAG = "mouthtrap";
static EventGroupHandle_t wifi_events;
static int retries;
static bool stopping;
#define CONNECTED BIT0
#define FAILED BIT1
#define BOUNDARY "mouthtrap-frame"

static void wifi_event(void *arg, esp_event_base_t base, int32_t id, void *data)
{
    (void)arg;
    if (stopping) return;
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_START) {
        esp_wifi_connect();
    } else if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        xEventGroupClearBits(wifi_events, CONNECTED);
        if (retries++ < 8) esp_wifi_connect();
        else xEventGroupSetBits(wifi_events, FAILED);
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        ip_event_got_ip_t *event = data;
        retries = 0;
        ESP_LOGI(TAG, "Camera URL: http://" IPSTR "/stream", IP2STR(&event->ip_info.ip));
        xEventGroupSetBits(wifi_events, CONNECTED);
    }
}

static bool wifi_start(void)
{
    wifi_events = xEventGroupCreate();
    if (!wifi_events) return false;
    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    if (!esp_netif_create_default_wifi_sta()) return false;
    wifi_init_config_t init = WIFI_INIT_CONFIG_DEFAULT();
    ESP_ERROR_CHECK(esp_wifi_init(&init));
    ESP_ERROR_CHECK(esp_wifi_set_storage(WIFI_STORAGE_RAM));
    ESP_ERROR_CHECK(esp_event_handler_register(WIFI_EVENT, ESP_EVENT_ANY_ID, wifi_event, NULL));
    ESP_ERROR_CHECK(esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, wifi_event, NULL));
    wifi_config_t config = {0};
    memcpy(config.sta.ssid, CONFIG_MOUTHTRAP_WIFI_SSID, strlen(CONFIG_MOUTHTRAP_WIFI_SSID));
    memcpy(config.sta.password, CONFIG_MOUTHTRAP_WIFI_PASSWORD, strlen(CONFIG_MOUTHTRAP_WIFI_PASSWORD));
    config.sta.threshold.authmode = WIFI_AUTH_WPA2_PSK;
    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    ESP_ERROR_CHECK(esp_wifi_set_config(WIFI_IF_STA, &config));
    ESP_ERROR_CHECK(esp_wifi_start());
    EventBits_t bits = xEventGroupWaitBits(wifi_events, CONNECTED | FAILED, pdFALSE, pdFALSE, pdMS_TO_TICKS(45000));
    return (bits & CONNECTED) != 0;
}

static esp_err_t camera_start(void)
{
    // XIAO ESP32S3 Sense pin mapping from Seeed's camera guide.
    camera_config_t config = {
        .pin_pwdn = -1, .pin_reset = -1, .pin_xclk = 10,
        .pin_sccb_sda = 40, .pin_sccb_scl = 39,
        .pin_d0 = 15, .pin_d1 = 17, .pin_d2 = 18, .pin_d3 = 16,
        .pin_d4 = 14, .pin_d5 = 12, .pin_d6 = 11, .pin_d7 = 48,
        .pin_vsync = 38, .pin_href = 47, .pin_pclk = 13,
        .xclk_freq_hz = 20000000, .ledc_timer = LEDC_TIMER_0, .ledc_channel = LEDC_CHANNEL_0,
        .pixel_format = PIXFORMAT_JPEG, .frame_size = FRAMESIZE_VGA, .jpeg_quality = 12,
        .fb_count = 2, .fb_location = CAMERA_FB_IN_PSRAM, .grab_mode = CAMERA_GRAB_LATEST,
    };
    esp_err_t result = esp_camera_init(&config);
    if (result != ESP_OK) return result;
    sensor_t *sensor = esp_camera_sensor_get();
    if (sensor->id.PID == OV3660_PID || sensor->id.PID == OV2640_PID) sensor->set_vflip(sensor, 1);
    if (sensor->id.PID == OV3660_PID) {
        sensor->set_brightness(sensor, 1);
        sensor->set_saturation(sensor, -2);
    }
    ESP_LOGI(TAG, "Camera initialized, PID=0x%04x, 640x480 JPEG quality 12", sensor->id.PID);
    return ESP_OK;
}

static bool authorized(httpd_req_t *request)
{
    char expected[128];
    char provided[128];
    int length = snprintf(expected, sizeof(expected), "Bearer %s", CONFIG_MOUTHTRAP_STREAM_TOKEN);
    if (length <= 0 || length >= sizeof(expected) || httpd_req_get_hdr_value_len(request, "Authorization") != length) return false;
    if (httpd_req_get_hdr_value_str(request, "Authorization", provided, sizeof(provided)) != ESP_OK) return false;
    unsigned char difference = 0;
    for (int i = 0; i < length; i++) difference |= (unsigned char)(expected[i] ^ provided[i]);
    return difference == 0;
}

static esp_err_t stream(httpd_req_t *request)
{
    if (!authorized(request)) {
        httpd_resp_set_status(request, "401 Unauthorized");
        return httpd_resp_sendstr(request, "A stream token is required.");
    }
    esp_err_t result = httpd_resp_set_type(request, "multipart/x-mixed-replace; boundary=" BOUNDARY);
    if (result != ESP_OK) return result;
    while (true) {
        int64_t started = esp_timer_get_time();
        camera_fb_t *frame = esp_camera_fb_get();
        if (!frame) return ESP_FAIL;
        char header[160];
        int length = snprintf(header, sizeof(header), "\r\n--" BOUNDARY "\r\nContent-Type: image/jpeg\r\nContent-Length: %u\r\n\r\n", (unsigned)frame->len);
        if (length <= 0 || length >= sizeof(header) || frame->format != PIXFORMAT_JPEG) result = ESP_FAIL;
        else {
            result = httpd_resp_send_chunk(request, header, length);
            if (result == ESP_OK) result = httpd_resp_send_chunk(request, (const char *)frame->buf, frame->len);
        }
        // The synchronous send has finished using the borrowed camera buffer on every path.
        esp_camera_fb_return(frame);
        if (result != ESP_OK) return result;
        int64_t remaining = 67000 - (esp_timer_get_time() - started);
        if (remaining > 1000) vTaskDelay(pdMS_TO_TICKS((uint32_t)(remaining / 1000)));
    }
}

void app_main(void)
{
    size_t ssid_length = strlen(CONFIG_MOUTHTRAP_WIFI_SSID);
    size_t password_length = strlen(CONFIG_MOUTHTRAP_WIFI_PASSWORD);
    size_t token_length = strlen(CONFIG_MOUTHTRAP_STREAM_TOKEN);
    bool valid_token = token_length >= 32 && token_length <= 120;
    for (size_t i = 0; i < token_length; i++) {
        char c = CONFIG_MOUTHTRAP_STREAM_TOKEN[i];
        valid_token &= (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
    }
    if (ssid_length == 0 || ssid_length > 32 || password_length < 8 || password_length > 63 || !valid_token) {
        ESP_LOGE(TAG, "Set the private Wi-Fi configuration and a 32-120 character stream token before flashing.");
        return;
    }
    esp_err_t result = nvs_flash_init();
    if (result != ESP_OK) {
        // Preserve existing NVS rather than erasing credentials or other device data automatically.
        ESP_LOGE(TAG, "NVS initialization failed: %s", esp_err_to_name(result));
        return;
    }
    if (!wifi_start()) {
        ESP_LOGE(TAG, "Wi-Fi did not connect; check the local configuration and reset to retry.");
        stopping = true;
        esp_wifi_stop();
        return;
    }
    result = camera_start();
    if (result == ESP_OK) {
        httpd_config_t config = HTTPD_DEFAULT_CONFIG();
        config.stack_size = 8192;
        config.max_open_sockets = 2;
        config.send_wait_timeout = 2;
        httpd_handle_t server = NULL;
        result = httpd_start(&server, &config);
        if (result == ESP_OK) {
            httpd_uri_t endpoint = { .uri = "/stream", .method = HTTP_GET, .handler = stream };
            result = httpd_register_uri_handler(server, &endpoint);
            if (result == ESP_OK) return;
            httpd_stop(server);
        }
        esp_camera_deinit();
    }
    ESP_LOGE(TAG, "Camera server startup failed: %s", esp_err_to_name(result));
    stopping = true;
    esp_wifi_stop();
}
