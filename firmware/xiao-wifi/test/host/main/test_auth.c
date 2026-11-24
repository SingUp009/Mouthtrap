#include <stdio.h>
#include <string.h>
#include "stream_auth.h"
#include "unity.h"
#include "unity_fixture.h"

#define TOKEN "0123456789abcdef0123456789abcdef"

TEST_GROUP(auth_check);
TEST_SETUP(auth_check) {}
TEST_TEAR_DOWN(auth_check) {}

TEST(auth_check, BearerWithTheStreamToken_ReturnsOk)
{
    // When / Then
    TEST_ASSERT_EQUAL(AUTH_OK, auth_check(TOKEN, "Bearer " TOKEN));
}

TEST(auth_check, LongestToken_ReturnsOk)
{
    // Given a token of the longest length the settings allow, in a header of AUTH_HEADER_MAX bytes
    char token[121];
    memset(token, 'x', 120);
    token[120] = '\0';
    char header[AUTH_HEADER_MAX + 1];
    snprintf(header, sizeof(header), "Bearer %s", token);
    TEST_ASSERT_EQUAL_size_t(AUTH_HEADER_MAX, strlen(header));
    // When / Then
    TEST_ASSERT_EQUAL(AUTH_OK, auth_check(token, header));
}

TEST(auth_check, NoHeader_ReturnsMissing)
{
    // When / Then
    TEST_ASSERT_EQUAL(AUTH_MISSING, auth_check(TOKEN, NULL));
}

TEST(auth_check, HeaderWithoutBearerScheme_ReturnsWrongScheme)
{
    // The scheme is matched exactly, as Mouthtrap sends it.
    static const char *const headers[] = {
        "",
        TOKEN,
        "Basic " TOKEN,
        "bearer " TOKEN,
        "Bearer" TOKEN,
        " Bearer " TOKEN,
        "Bearer",
    };
    for (size_t i = 0; i < sizeof(headers) / sizeof(headers[0]); i++) {
        // When
        auth_result_t result = auth_check(TOKEN, headers[i]);
        // Then
        TEST_ASSERT_EQUAL_MESSAGE(AUTH_WRONG_SCHEME, result, headers[i]);
    }
}

TEST(auth_check, BearerWithAnotherToken_ReturnsWrongToken)
{
    static const char *const headers[] = {
        "Bearer ",
        "Bearer X123456789abcdef0123456789abcdef", // first character differs
        "Bearer 0123456789abcdef0123456789abcdeX", // last character differs
        "Bearer 0123456789ABCDEF0123456789ABCDEF", // tokens are case-sensitive
        "Bearer 0123456789abcdef0123456789abcde",  // a prefix of the token
        "Bearer 0123456789abcdef0123456789abcdef0", // the token and more
        "Bearer 0123456789abcdef0123456789abcdef ", // a trailing space
        "Bearer  0123456789abcdef0123456789abcdef", // two spaces after the scheme
    };
    for (size_t i = 0; i < sizeof(headers) / sizeof(headers[0]); i++) {
        // When
        auth_result_t result = auth_check(TOKEN, headers[i]);
        // Then
        TEST_ASSERT_EQUAL_MESSAGE(AUTH_WRONG_TOKEN, result, headers[i]);
    }
}

TEST(auth_check, EmptyStreamToken_RejectsAnEmptyBearerToken)
{
    // Given a token that settings validation would have refused
    const char *token = "";
    // When / Then
    TEST_ASSERT_EQUAL(AUTH_WRONG_TOKEN, auth_check(token, "Bearer "));
}

TEST_GROUP_RUNNER(auth_check)
{
    RUN_TEST_CASE(auth_check, BearerWithTheStreamToken_ReturnsOk);
    RUN_TEST_CASE(auth_check, LongestToken_ReturnsOk);
    RUN_TEST_CASE(auth_check, NoHeader_ReturnsMissing);
    RUN_TEST_CASE(auth_check, HeaderWithoutBearerScheme_ReturnsWrongScheme);
    RUN_TEST_CASE(auth_check, BearerWithAnotherToken_ReturnsWrongToken);
    RUN_TEST_CASE(auth_check, EmptyStreamToken_RejectsAnEmptyBearerToken);
}
