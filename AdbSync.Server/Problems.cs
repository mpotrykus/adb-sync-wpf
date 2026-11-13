namespace AdbSync.Server;

/// <summary>RFC 9457 problem responses carrying the contract's <c>code</c>, which clients branch on instead of the text.</summary>
public static class Problems
{
    public static IResult Create(int status, string code, string title, string? detail = null) =>
        Results.Problem(
            detail: detail,
            statusCode: status,
            title: title,
            type: $"https://adbsync.dev/problems/{code}",
            extensions: new Dictionary<string, object?> { ["code"] = code });

    public static IResult Unauthorized() =>
        Create(StatusCodes.Status401Unauthorized, "unauthorized", "A valid client token is required.");

    public static IResult Validation(string detail) =>
        Create(StatusCodes.Status400BadRequest, "validation_failed", "The request isn't valid.", detail);

    public static IResult NotFound(string detail) =>
        Create(StatusCodes.Status404NotFound, "not_found", "Not found.", detail);
}
