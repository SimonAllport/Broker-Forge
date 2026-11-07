using System;

namespace BrokerForge.Contracts
{
    /// <summary>Base of every typed failure. Messages never contain secrets.</summary>
    public abstract class BrokerForgeException : Exception
    {
        protected BrokerForgeException(string message, string correlationId, Exception inner = null)
            : base(message, inner)
        {
            CorrelationId = correlationId;
        }

        public string CorrelationId { get; }
    }

    /// <summary>A filter, operator, sort or page request the descriptor does not declare.</summary>
    public sealed class UnsupportedQueryException : BrokerForgeException
    {
        public UnsupportedQueryException(string message, string correlationId, Exception inner = null) : base(message, correlationId, inner) { }
    }

    /// <summary>A per-user mode has no usable credential. Never retried as another mode.</summary>
    public sealed class ConnectionRequiredException : BrokerForgeException
    {
        public ConnectionRequiredException(string message, string correlationId, string connectUrl, Exception inner = null)
            : base(message, correlationId, inner)
        {
            ConnectUrl = connectUrl;
        }

        public string ConnectUrl { get; }
    }

    /// <summary>The source failed: an unmapped status, a network failure (Status null) or a failed success-body check.</summary>
    public class SourceException : BrokerForgeException
    {
        public SourceException(string message, string correlationId, int? status = null, Exception inner = null)
            : base(message, correlationId, inner)
        {
            Status = status;
        }

        public int? Status { get; }
    }

    /// <summary>The 90-second request budget ran out.</summary>
    public sealed class SourceTimeoutException : BrokerForgeException
    {
        public SourceTimeoutException(string message, string correlationId, Exception inner = null) : base(message, correlationId, inner) { }
    }

    /// <summary>The source response does not match the descriptor's map.</summary>
    public sealed class MappingException : BrokerForgeException
    {
        public MappingException(string message, string correlationId, Exception inner = null) : base(message, correlationId, inner) { }
    }

    /// <summary>The plug-in could not reach the host.</summary>
    public sealed class BrokerUnavailableException : BrokerForgeException
    {
        public BrokerUnavailableException(string message, string correlationId, Exception inner = null) : base(message, correlationId, inner) { }
    }

    /// <summary>A source status the descriptor maps to a Dataverse error. Kind is a <see cref="SourceErrorKinds"/> value.</summary>
    public sealed class MappedSourceException : SourceException
    {
        public MappedSourceException(string kind, string message, string correlationId, int? status = null, Exception inner = null)
            : base(message, correlationId, status, inner)
        {
            Kind = kind;
        }

        public string Kind { get; }
    }
}
