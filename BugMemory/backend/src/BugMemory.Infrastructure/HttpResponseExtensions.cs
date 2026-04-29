using System.Net.Http;

namespace BugMemory.Infrastructure;

/// <summary>
/// Helpers for talking to external HTTP services (OpenAI, Qdrant) where the
/// response body on a failure carries the actual diagnostic. Stock
/// <see cref="HttpResponseMessage.EnsureSuccessStatusCode"/> throws
/// <see cref="HttpRequestException"/> with just the status code, dropping
/// the body — which is where every useful error message lives. Examples:
///
///   - OpenAI 401 body:    {"error":{"message":"Incorrect API key provided","type":"invalid_request_error"}}
///   - OpenAI 429 body:    {"error":{"message":"Rate limit reached for ...","type":"requests"}}
///   - Qdrant 4xx/5xx body: {"status":{"error":"Wrong vector size: expected 1536, got 768"}}
///
/// Without the body, the user sees "HttpRequestException: Response status
/// code does not indicate success: 401 (Unauthorized)." With the body,
/// they see exactly what's wrong.
/// </summary>
internal static class HttpResponseExtensions
{
    /// <summary>
    /// Like <c>EnsureSuccessStatusCode</c>, but on failure includes the
    /// response body in the exception message. Truncates very long bodies
    /// (Qdrant validation errors can be verbose) so the exception message
    /// stays loggable. The full body is also accessible via the original
    /// response if the caller wants finer detail.
    /// </summary>
    /// <param name="serviceLabel">
    /// Short label for which service this came from, e.g. "OpenAI" or
    /// "Qdrant". Prepended to the exception message so a stack trace
    /// surfacing somewhere unrelated still tells you which dependency
    /// failed.
    /// </param>
    public static async Task EnsureSuccessOrThrowWithBodyAsync(
        this HttpResponseMessage response,
        string serviceLabel,
        CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        // Read the body before throwing. We don't want to call
        // ReadAsStringAsync on a successful response — wastes work — and
        // we don't want a body-read failure to mask the original error,
        // hence the inner try.
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            body = $"(could not read body: {ex.GetType().Name}: {ex.Message})";
        }

        // Truncate for sanity. Real OpenAI errors are short JSON; some
        // Qdrant validation errors can run to a few KB. 2k is plenty.
        const int maxBodyLength = 2048;
        if (body.Length > maxBodyLength)
        {
            body = body[..maxBodyLength] + "... (truncated)";
        }

        throw new HttpRequestException(
            $"{serviceLabel} returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}",
            inner: null,
            statusCode: response.StatusCode);
    }
}
