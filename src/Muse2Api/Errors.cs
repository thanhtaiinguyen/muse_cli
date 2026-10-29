namespace Muse2Api;

/// <summary>Every error maps to an HTTP status and an OpenAI-style error body,
/// exactly like the Python <c>Muse2APIError</c> hierarchy.</summary>
public class MuseException : Exception
{
    public int StatusCode { get; }
    public string ErrorType { get; }
    public string Code { get; }

    public MuseException(string message, int statusCode = 500,
        string errorType = "server_error", string code = "internal_error") : base(message)
    {
        StatusCode = statusCode; ErrorType = errorType; Code = code;
    }

    public Dictionary<string, object> ToBody() => new()
    {
        ["error"] = new Dictionary<string, object>
        {
            ["message"] = Message, ["type"] = ErrorType, ["code"] = Code,
        },
    };

    public static MuseException Invalid(string message)
        => new(message, 400, "invalid_request_error", "invalid_request");
    public static MuseException Unauthorized(string message = "invalid or missing API key")
        => new(message, 401, "authentication_error", "invalid_api_key");
    public static MuseException NotFound(string message)
        => new(message, 404, "invalid_request_error", "not_found");
    public static MuseException NotImplemented(string message)
        => new(message, 501, "not_implemented", "not_implemented");
    public static MuseException NoAccount(string message = "no usable account in pool")
        => new(message, 503, "server_error", "no_account_available");
}

public class UpstreamException : MuseException
{
    public bool Retryable { get; }
    public UpstreamException(string message, int statusCode = 502, string code = "upstream_error",
        bool retryable = true) : base(message, statusCode, "upstream_error", code)
    {
        Retryable = retryable;
    }
    public static UpstreamException Auth(string message)
        => new(message, 502, "upstream_auth_failed");
    public static UpstreamException Quota(string message)
        => new(message, 429, "upstream_quota_exhausted");
    public static UpstreamException Timeout(string message)
        => new(message, 504, "upstream_timeout");
    // A DOM/readback problem is not evidence that the account is rate limited.
    // The upstream may already have accepted this request; never auto-resubmit.
    public static UpstreamException ResponseUnavailable(string message)
        => new(message, 502, "upstream_response_unavailable", retryable: false);
    public static UpstreamException Refused(string message)
        => new(message, 502, "upstream_refused", retryable: false);
}
