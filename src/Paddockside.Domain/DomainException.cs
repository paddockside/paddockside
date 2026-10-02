namespace Paddockside.Domain;

/// <summary>Thrown when an operation would break a domain invariant.</summary>
public sealed class DomainException(string message) : Exception(message);
