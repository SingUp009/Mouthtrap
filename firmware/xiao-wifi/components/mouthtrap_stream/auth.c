#include <string.h>
#include "stream_auth.h"

#define SCHEME "Bearer "

auth_result_t auth_check(const char *token, const char *authorization)
{
    if (authorization == NULL) return AUTH_MISSING;
    size_t scheme_length = strlen(SCHEME);
    if (strncmp(authorization, SCHEME, scheme_length) != 0) return AUTH_WRONG_SCHEME;
    const char *provided = authorization + scheme_length;
    size_t length = strlen(token);
    if (length == 0 || strlen(provided) != length) return AUTH_WRONG_TOKEN;
    unsigned char difference = 0;
    for (size_t i = 0; i < length; i++) difference |= (unsigned char)(token[i] ^ provided[i]);
    return difference == 0 ? AUTH_OK : AUTH_WRONG_TOKEN;
}
