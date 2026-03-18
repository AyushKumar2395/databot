using Application.Common.Models;

namespace Application.Common.Interfaces;

/// <summary>
/// Retrieves servers assigned to a user from stored procedures.
/// </summary>
public interface IUserServerRepository
{
    Task<List<UserServerEntry>> GetSqlServersAsync(string userId, CancellationToken cancellationToken);
    Task<List<UserServerEntry>> GetWinServersAsync(string userId, CancellationToken cancellationToken);
}
