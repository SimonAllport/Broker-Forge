using BrokerForge.Contracts;
using Microsoft.Xrm.Sdk;

namespace BrokerForge.Plugin.Transport
{
    /// <summary>Host errors to the Dataverse error the user sees. Every message ends with the correlation ID.</summary>
    public static class ErrorRaiser
    {
        public const int ObjectDoesNotExist = -2147220969;   // 0x80040217
        public const int DuplicateRecord = -2147220937;      // 0x80040237
        public const int PrivilegeDenied = -2147220960;      // 0x80040220
        public const int GenericFailure = -2147220989;       // 0x80040203

        public static InvalidPluginExecutionException ToException(ErrorPayload payload)
        {
            int code;
            switch (payload.Error)
            {
                case SourceErrorKinds.NotFound: code = ObjectDoesNotExist; break;
                case SourceErrorKinds.Conflict: code = DuplicateRecord; break;
                case SourceErrorKinds.Forbidden: code = PrivilegeDenied; break;
                default: code = GenericFailure; break;
            }
            var message = string.Format("{0}: {1}{2} (correlationId {3})",
                payload.Error,
                payload.Message,
                string.IsNullOrEmpty(payload.ConnectUrl) ? "" : " Connect at " + payload.ConnectUrl + ".",
                payload.CorrelationId);
            return new InvalidPluginExecutionException(OperationStatus.Failed, code, message);
        }

        public static InvalidPluginExecutionException ToException(BrokerForgeException ex)
        {
            var connect = ex as ConnectionRequiredException;
            return ToException(new ErrorPayload { Error = ex.GetType().Name, Message = ex.Message, CorrelationId = ex.CorrelationId, ConnectUrl = connect?.ConnectUrl });
        }
    }
}
