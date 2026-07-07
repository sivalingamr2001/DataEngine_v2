namespace DataEngine.Core.Interfaces;

/// <summary>
/// Resolves the authenticated user identity for the current request.
/// </summary>
public interface IUserContext
{
    string? UserId { get; }

    bool IsAuthenticated { get; }
}
