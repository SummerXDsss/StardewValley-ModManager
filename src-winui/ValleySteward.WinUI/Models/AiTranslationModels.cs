using System.Text.Json.Serialization;

namespace ValleySteward.WinUI.Models;

public sealed record AiTranslationStatus(
    bool Configured,
    bool ApiKeyConfigured,
    string? BaseUrl,
    string? ModelId);

public sealed class AiTranslationStoredConfig
{
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    [JsonPropertyName("baseUrl")]
    public string BaseUrl { get; set; } = string.Empty;

    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = string.Empty;
}

public sealed record AiTranslationModel(string Id, string? OwnedBy)
{
    public string DisplayName => string.IsNullOrWhiteSpace(OwnedBy)
        ? Id
        : $"{Id}  ·  {OwnedBy}";
}

public sealed record AiTranslationRequestMetadata(
    string Method,
    string Endpoint,
    string? Body)
{
    public string MaskedRequest
    {
        get
        {
            var contentHeader = Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
                ? "\r\nContent-Type: application/json"
                : string.Empty;
            var body = string.IsNullOrEmpty(Body) ? string.Empty : $"\r\n\r\n{Body}";
            return $"{Method} {Endpoint}\r\nAccept: application/json\r\nAuthorization: Bearer [已隐藏]{contentHeader}{body}";
        }
    }
}

public sealed record AiTranslationResponseMetadata(
    int Status,
    string Summary,
    string? Content);

public sealed record AiTranslationModelListResult(
    IReadOnlyList<AiTranslationModel> Models,
    AiTranslationRequestMetadata Request,
    AiTranslationResponseMetadata Response);

public sealed record AiTranslationConnectionTestResult(
    string ModelId,
    string Message,
    AiTranslationRequestMetadata Request,
    AiTranslationResponseMetadata Response);

public sealed record AiTranslationResult(string Name, string Summary);

public sealed record AiTranslationBatchItem(string Id, string Name, string Summary);

public sealed record AiTranslationBatchResult(
    int Index,
    string Id,
    AiTranslationResult Translation);

public enum AiTranslationRequestKind
{
    ModelList,
    ConnectionTest,
    Translation,
}

public enum AiTranslationRequestStage
{
    Queued,
    Sending,
    ResponseReceived,
    Completed,
    Failed,
    TimedOut,
    Canceled,
}

public sealed class AiTranslationRequestActivityEventArgs : EventArgs
{
    public AiTranslationRequestActivityEventArgs(
        Guid requestId,
        AiTranslationRequestKind kind,
        AiTranslationRequestStage stage,
        AiTranslationRequestMetadata request,
        DateTimeOffset timestamp,
        long elapsedMilliseconds,
        int? statusCode,
        string? detail)
    {
        RequestId = requestId;
        Kind = kind;
        Stage = stage;
        Request = request;
        Timestamp = timestamp;
        ElapsedMilliseconds = elapsedMilliseconds;
        StatusCode = statusCode;
        Detail = detail;
    }

    public Guid RequestId { get; }
    public AiTranslationRequestKind Kind { get; }
    public AiTranslationRequestStage Stage { get; }
    public AiTranslationRequestMetadata Request { get; }
    public DateTimeOffset Timestamp { get; }
    public long ElapsedMilliseconds { get; }
    public int? StatusCode { get; }
    public string? Detail { get; }
}
