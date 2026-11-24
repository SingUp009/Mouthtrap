#pragma once

/** The outcome of checking a request's Authorization header. Every value but AUTH_OK means 401. */
typedef enum {
    AUTH_OK = 0,
    AUTH_MISSING,      // no Authorization header
    AUTH_WRONG_SCHEME, // not "Bearer <token>"
    AUTH_WRONG_TOKEN,  // a bearer token that is not the stream token
} auth_result_t;

/** The longest Authorization header that can match: "Bearer " and the longest token. */
#define AUTH_HEADER_MAX (7 + 120)

/**
 * Check an Authorization header against the stream token. Pass NULL when the request has no header.
 * The comparison takes the same time for every token of the right length. An empty token matches nothing.
 */
auth_result_t auth_check(const char *token, const char *authorization);
