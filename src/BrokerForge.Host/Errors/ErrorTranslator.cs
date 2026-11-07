using BrokerForge.Abstractions;
using BrokerForge.Auth;
using BrokerForge.Contracts;
using BrokerForge.Host.Ingress;
using BrokerForge.Keys;

namespace BrokerForge.Host.Errors;

/// <summary>
/// Turns a failure into an HTTP status and <see cref="ErrorPayload"/>. <see cref="ErrorPayload.Error"/> is
/// the exception type name or the mapped kind, which the plug-in turns into the Dataverse error.
/// Unexpected failures return no internals, only the correlation ID.
/// </summary>
public static class ErrorTranslator
{
    public static (int Status, ErrorPayload Payload) Translate(Exception ex, string correlationId)
    {
        var (status, payload) = Map(ex);
        payload.CorrelationId = correlationId;
        return (status, payload);
    }

    private static (int Status, ErrorPayload Payload) Map(Exception ex) => ex switch
    {
        MappedSourceException m => (m.Kind switch
        {
            SourceErrorKinds.NotFound => 404,
            SourceErrorKinds.Conflict => 409,
            SourceErrorKinds.Forbidden => 403,
            _ => 400,
        }, Payload(m.Kind, m.Message)),
        UnsupportedQueryException => (400, Payload(ex)),
        ConnectionRequiredException c => (401, new ErrorPayload { Error = nameof(ConnectionRequiredException), Message = c.Message, ConnectUrl = c.ConnectUrl }),
        CallerRejectedException => (403, Payload(SourceErrorKinds.Forbidden, ex.Message)),
        InvalidKeyException => (404, Payload(SourceErrorKinds.NotFound, "No record exists with this id.")),
        MappingException or SourceException => (502, Payload(ex)),
        SourceTimeoutException => (504, Payload(ex)),
        CredentialUnavailableException => (500, Payload(ex)),
        ConfigurationException => (500, Payload("ConfigurationError", ex.Message)),
        _ => (500, Payload("InternalError", "Unexpected host failure.")),
    };

    private static ErrorPayload Payload(Exception ex) => Payload(ex.GetType().Name, ex.Message);

    private static ErrorPayload Payload(string error, string message) => new() { Error = error, Message = message };
}
