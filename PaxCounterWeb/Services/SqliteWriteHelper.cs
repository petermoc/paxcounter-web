using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace PaxCounterWeb.Services;

public static class SqliteWriteHelper
{
    public static async Task SaveChangesWithRetryAsync(DbContext db, CancellationToken ct, int maxAttempts = 4)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                await db.SaveChangesAsync(CancellationToken.None);
                return;
            }
            catch (DbUpdateException ex) when (IsDatabaseLocked(ex) && attempt < maxAttempts)
            {
                var delayMs = 150 * attempt * attempt;
                await Task.Delay(delayMs, ct);
            }
        }

        await db.SaveChangesAsync(CancellationToken.None);
    }

    public static bool IsDatabaseLocked(Exception ex)
    {
        if (ex is DbUpdateException dbUpdate && dbUpdate.InnerException is SqliteException sqliteInner)
        {
            return sqliteInner.SqliteErrorCode == 5 || sqliteInner.SqliteErrorCode == 6;
        }

        if (ex is SqliteException sqlite)
        {
            return sqlite.SqliteErrorCode == 5 || sqlite.SqliteErrorCode == 6;
        }

        return false;
    }
}
