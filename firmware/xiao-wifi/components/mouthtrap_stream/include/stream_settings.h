#pragma once

/** Why the stored settings cannot be used. */
typedef enum {
    SETTINGS_OK = 0,
    SETTINGS_SSID_LENGTH,     // not 1-32 bytes
    SETTINGS_PASSWORD_LENGTH, // not 8-63 bytes, as WPA2-PSK requires
    SETTINGS_TOKEN_LENGTH,    // not 32-120 characters
    SETTINGS_TOKEN_CHARACTER, // not only letters, digits, '-' and '_'
} settings_error_t;

#define SETTINGS_SSID_MAX 32
#define SETTINGS_PASSWORD_MAX 63
#define SETTINGS_TOKEN_MAX 120

/** Check the settings in the order of the fields; the first problem found is returned. */
settings_error_t settings_validate(const char *ssid, const char *password, const char *token);

/** A short English description of an error, for the log. */
const char *settings_error_text(settings_error_t error);
