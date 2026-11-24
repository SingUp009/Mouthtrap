#include <stdio.h>
#include <string.h>
#include "stream_settings.h"
#include "unity.h"
#include "unity_fixture.h"

#define VALID_SSID "home"
#define VALID_PASSWORD "password123"
#define VALID_TOKEN "0123456789abcdef0123456789abcdef"

static char text[256];

/** A string of `length` copies of `c`, valid until the next call. */
static const char *repeat(char c, size_t length)
{
    TEST_ASSERT_LESS_THAN_size_t(sizeof(text), length);
    memset(text, c, length);
    text[length] = '\0';
    return text;
}

TEST_GROUP(settings_validate);
TEST_SETUP(settings_validate) {}
TEST_TEAR_DOWN(settings_validate) {}

TEST(settings_validate, ValidSettings_ReturnsOk)
{
    // When / Then
    TEST_ASSERT_EQUAL(SETTINGS_OK, settings_validate(VALID_SSID, VALID_PASSWORD, VALID_TOKEN));
}

TEST(settings_validate, SsidAtEachLimit_ReturnsOk)
{
    static const size_t lengths[] = { 1, 32 };
    for (size_t i = 0; i < sizeof(lengths) / sizeof(lengths[0]); i++) {
        // Given
        const char *ssid = repeat('s', lengths[i]);
        // When
        settings_error_t error = settings_validate(ssid, VALID_PASSWORD, VALID_TOKEN);
        // Then
        TEST_ASSERT_EQUAL_MESSAGE(SETTINGS_OK, error, ssid);
    }
}

TEST(settings_validate, EmptySsid_ReturnsSsidLength)
{
    // When / Then
    TEST_ASSERT_EQUAL(SETTINGS_SSID_LENGTH, settings_validate("", VALID_PASSWORD, VALID_TOKEN));
}

TEST(settings_validate, SsidLongerThan32Bytes_ReturnsSsidLength)
{
    // Given
    const char *ssid = repeat('s', 33);
    // When / Then
    TEST_ASSERT_EQUAL(SETTINGS_SSID_LENGTH, settings_validate(ssid, VALID_PASSWORD, VALID_TOKEN));
}

TEST(settings_validate, SsidOf11JapaneseCharacters_ReturnsSsidLengthBecauseItIs33Bytes)
{
    // Given eleven characters of three bytes each in UTF-8
    const char *ssid = "あいうえおかきくけこさ";
    // When / Then
    TEST_ASSERT_EQUAL(SETTINGS_SSID_LENGTH, settings_validate(ssid, VALID_PASSWORD, VALID_TOKEN));
}

TEST(settings_validate, PasswordAtEachLimit_ReturnsOk)
{
    static const size_t lengths[] = { 8, 63 };
    for (size_t i = 0; i < sizeof(lengths) / sizeof(lengths[0]); i++) {
        // Given
        const char *password = repeat('p', lengths[i]);
        // When
        settings_error_t error = settings_validate(VALID_SSID, password, VALID_TOKEN);
        // Then
        TEST_ASSERT_EQUAL_MESSAGE(SETTINGS_OK, error, password);
    }
}

TEST(settings_validate, PasswordOutsideWpa2Lengths_ReturnsPasswordLength)
{
    static const size_t lengths[] = { 0, 7, 64 };
    for (size_t i = 0; i < sizeof(lengths) / sizeof(lengths[0]); i++) {
        // Given
        const char *password = repeat('p', lengths[i]);
        // When
        settings_error_t error = settings_validate(VALID_SSID, password, VALID_TOKEN);
        // Then
        TEST_ASSERT_EQUAL_MESSAGE(SETTINGS_PASSWORD_LENGTH, error, password);
    }
}

TEST(settings_validate, TokenAtEachLimit_ReturnsOk)
{
    static const size_t lengths[] = { 32, 120 };
    for (size_t i = 0; i < sizeof(lengths) / sizeof(lengths[0]); i++) {
        // Given
        const char *token = repeat('t', lengths[i]);
        // When
        settings_error_t error = settings_validate(VALID_SSID, VALID_PASSWORD, token);
        // Then
        TEST_ASSERT_EQUAL_MESSAGE(SETTINGS_OK, error, token);
    }
}

TEST(settings_validate, TokenOutsideLengths_ReturnsTokenLength)
{
    static const size_t lengths[] = { 0, 31, 121 };
    for (size_t i = 0; i < sizeof(lengths) / sizeof(lengths[0]); i++) {
        // Given
        const char *token = repeat('t', lengths[i]);
        // When
        settings_error_t error = settings_validate(VALID_SSID, VALID_PASSWORD, token);
        // Then
        TEST_ASSERT_EQUAL_MESSAGE(SETTINGS_TOKEN_LENGTH, error, token);
    }
}

TEST(settings_validate, TokenOfEveryAllowedCharacter_ReturnsOk)
{
    // Given
    const char *token = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_";
    // When / Then
    TEST_ASSERT_EQUAL(SETTINGS_OK, settings_validate(VALID_SSID, VALID_PASSWORD, token));
}

TEST(settings_validate, TokenWithAnotherCharacter_ReturnsTokenCharacter)
{
    // An HTTP header cannot carry some of these, and the others are not in the token alphabet.
    static const char others[] = { ' ', '!', '"', '.', '/', '+', '=', ':', '\t', '\x7f', '\x80', '\xff' };
    for (size_t i = 0; i < sizeof(others); i++) {
        // Given
        char token[33];
        memset(token, 'a', 32);
        token[32] = '\0';
        token[16] = others[i];
        // When
        settings_error_t error = settings_validate(VALID_SSID, VALID_PASSWORD, token);
        // Then
        char message[32];
        snprintf(message, sizeof(message), "character 0x%02x", (unsigned char)others[i]);
        TEST_ASSERT_EQUAL_MESSAGE(SETTINGS_TOKEN_CHARACTER, error, message);
    }
}

TEST(settings_validate, SeveralInvalidFields_ReturnsTheFirstInFieldOrder)
{
    // When / Then
    TEST_ASSERT_EQUAL(SETTINGS_SSID_LENGTH, settings_validate("", "short", "bad"));
    TEST_ASSERT_EQUAL(SETTINGS_PASSWORD_LENGTH, settings_validate(VALID_SSID, "short", "bad"));
}

TEST_GROUP_RUNNER(settings_validate)
{
    RUN_TEST_CASE(settings_validate, ValidSettings_ReturnsOk);
    RUN_TEST_CASE(settings_validate, SsidAtEachLimit_ReturnsOk);
    RUN_TEST_CASE(settings_validate, EmptySsid_ReturnsSsidLength);
    RUN_TEST_CASE(settings_validate, SsidLongerThan32Bytes_ReturnsSsidLength);
    RUN_TEST_CASE(settings_validate, SsidOf11JapaneseCharacters_ReturnsSsidLengthBecauseItIs33Bytes);
    RUN_TEST_CASE(settings_validate, PasswordAtEachLimit_ReturnsOk);
    RUN_TEST_CASE(settings_validate, PasswordOutsideWpa2Lengths_ReturnsPasswordLength);
    RUN_TEST_CASE(settings_validate, TokenAtEachLimit_ReturnsOk);
    RUN_TEST_CASE(settings_validate, TokenOutsideLengths_ReturnsTokenLength);
    RUN_TEST_CASE(settings_validate, TokenOfEveryAllowedCharacter_ReturnsOk);
    RUN_TEST_CASE(settings_validate, TokenWithAnotherCharacter_ReturnsTokenCharacter);
    RUN_TEST_CASE(settings_validate, SeveralInvalidFields_ReturnsTheFirstInFieldOrder);
}

TEST_GROUP(settings_error_text);
TEST_SETUP(settings_error_text) {}
TEST_TEAR_DOWN(settings_error_text) {}

TEST(settings_error_text, EveryError_HasItsOwnText)
{
    // Given
    static const settings_error_t errors[] = {
        SETTINGS_SSID_LENGTH, SETTINGS_PASSWORD_LENGTH, SETTINGS_TOKEN_LENGTH, SETTINGS_TOKEN_CHARACTER,
    };
    const char *unknown = settings_error_text((settings_error_t)-1);
    for (size_t i = 0; i < sizeof(errors) / sizeof(errors[0]); i++) {
        // When
        const char *description = settings_error_text(errors[i]);
        // Then
        TEST_ASSERT_NOT_EQUAL(0, strcmp(unknown, description));
        for (size_t j = 0; j < i; j++) TEST_ASSERT_NOT_EQUAL(0, strcmp(settings_error_text(errors[j]), description));
    }
}

TEST_GROUP_RUNNER(settings_error_text)
{
    RUN_TEST_CASE(settings_error_text, EveryError_HasItsOwnText);
}
