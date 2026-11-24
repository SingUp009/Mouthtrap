#include <stdbool.h>
#include <string.h>
#include "stream_settings.h"

static bool token_character(char c)
{
    return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
}

settings_error_t settings_validate(const char *ssid, const char *password, const char *token)
{
    size_t ssid_length = strlen(ssid);
    if (ssid_length == 0 || ssid_length > SETTINGS_SSID_MAX) return SETTINGS_SSID_LENGTH;
    size_t password_length = strlen(password);
    if (password_length < 8 || password_length > SETTINGS_PASSWORD_MAX) return SETTINGS_PASSWORD_LENGTH;
    size_t token_length = strlen(token);
    if (token_length < 32 || token_length > SETTINGS_TOKEN_MAX) return SETTINGS_TOKEN_LENGTH;
    for (size_t i = 0; i < token_length; i++) {
        if (!token_character(token[i])) return SETTINGS_TOKEN_CHARACTER;
    }
    return SETTINGS_OK;
}

const char *settings_error_text(settings_error_t error)
{
    switch (error) {
    case SETTINGS_OK: return "ok";
    case SETTINGS_SSID_LENGTH: return "the SSID must be 1-32 bytes";
    case SETTINGS_PASSWORD_LENGTH: return "the password must be 8-63 bytes";
    case SETTINGS_TOKEN_LENGTH: return "the stream token must be 32-120 characters";
    case SETTINGS_TOKEN_CHARACTER: return "the stream token may contain only letters, digits, '-' and '_'";
    }
    return "unknown error";
}
