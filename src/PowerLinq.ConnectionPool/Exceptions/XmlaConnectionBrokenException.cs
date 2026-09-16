namespace PowerLinq.ConnectionPool.Exceptions;

/// <summary>
/// Signals that the XMLA connection/session died (token, transport or a Premium node
/// reallocation), meaning the connection must be discarded and recreated. Wraps the ADOMD.NET
/// exception (<c>AdomdConnectionException</c>) so the service can retry without depending on the
/// ADOMD type. Not to be confused with DAX errors (an invalid query), which must NOT be retried.
/// </summary>
public sealed class XmlaConnectionBrokenException(string message, Exception innerException)
    : Exception(message, innerException);
