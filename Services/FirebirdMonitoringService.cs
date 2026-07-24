using ExistenciasReset.Models;
using FirebirdSql.Data.FirebirdClient;

namespace ExistenciasReset.Services;

public class FirebirdMonitoringService(ILogger<FirebirdMonitoringService> logger)
{
    private const int HealthCheckTimeoutSeconds = 12;

    public async Task<IReadOnlyList<AttachmentInfo>> GetAttachmentsAsync(TenantOptions tenant, CancellationToken cancellationToken)
    {
        await using var connection = new FbConnection(tenant.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        return await GetAttachmentsAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<StatementInfo>> GetActiveStatementsAsync(TenantOptions tenant, CancellationToken cancellationToken)
    {
        await using var connection = new FbConnection(tenant.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        return await GetActiveStatementsAsync(connection, cancellationToken);
    }

    public async Task<bool> CancelStatementAsync(TenantOptions tenant, int statementId, CancellationToken cancellationToken)
    {
        bool success;

        await using var connection = new FbConnection(tenant.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = new FbCommand("DELETE FROM MON$STATEMENTS WHERE MON$STATEMENT_ID = @id", connection);
            command.Parameters.Add("@id", FbDbType.Integer).Value = statementId;
            await command.ExecuteNonQueryAsync(cancellationToken);
            success = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo cancelar el statement {StatementId}", statementId);
            success = false;
        }

        return success;
    }

    public async Task<bool> ForceDisconnectAsync(TenantOptions tenant, int attachmentId, CancellationToken cancellationToken)
    {
        bool success;

        await using var connection = new FbConnection(tenant.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = new FbCommand("DELETE FROM MON$ATTACHMENTS WHERE MON$ATTACHMENT_ID = @id", connection);
            command.Parameters.Add("@id", FbDbType.Integer).Value = attachmentId;
            await command.ExecuteNonQueryAsync(cancellationToken);
            success = true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo forzar la desconexión del attachment {AttachmentId}", attachmentId);
            success = false;
        }

        return success;
    }

    public async Task<int> ForceDisconnectOlderThanAsync(TenantOptions tenant, DateTime cutoff, CancellationToken cancellationToken)
    {
        int disconnected = 0;

        await using var connection = new FbConnection(tenant.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = new FbCommand(
                @"DELETE FROM MON$ATTACHMENTS
                  WHERE MON$ATTACHMENT_ID <> CURRENT_CONNECTION
                    AND MON$REMOTE_PROCESS IS NOT NULL
                    AND MON$TIMESTAMP < @cutoff",
                connection);
            command.Parameters.Add("@cutoff", FbDbType.TimeStamp).Value = cutoff;
            disconnected = await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo desconectar conexiones anteriores a {Cutoff}", cutoff);
        }

        return disconnected;
    }

    public async Task<TenantHealthSummary> GetHealthSummaryAsync(TenantOptions tenant, CancellationToken cancellationToken)
    {
        TenantHealthSummary summary;

        try
        {
            await using var connection = new FbConnection(tenant.ConnectionString);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(HealthCheckTimeoutSeconds));
            await connection.OpenAsync(timeoutCts.Token);

            var attachments = await GetAttachmentsAsync(connection, timeoutCts.Token);
            var statements = await GetActiveStatementsAsync(connection, timeoutCts.Token);
            var longest = statements.OrderByDescending(s => s.SecondsRunning).FirstOrDefault();

            var longestDescription = longest is null
                ? string.Empty
                : $"{longest.RemoteProcess} (PID {longest.RemotePid})" +
                  (longest.ProcedureName == string.Empty ? string.Empty : $" en {longest.ProcedureName} línea {longest.ProcedureLine}");

            summary = new TenantHealthSummary(
                tenant.Id,
                tenant.Name,
                true,
                attachments.Count,
                statements.Count,
                longest?.SecondsRunning ?? 0,
                longestDescription,
                string.Empty);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo obtener el estado del tenant {TenantId}", tenant.Id);
            summary = new TenantHealthSummary(tenant.Id, tenant.Name, false, 0, 0, 0, string.Empty, ex.Message);
        }

        return summary;
    }

    private static async Task<IReadOnlyList<AttachmentInfo>> GetAttachmentsAsync(FbConnection connection, CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT MON$ATTACHMENT_ID, MON$USER, MON$REMOTE_ADDRESS, MON$REMOTE_PROCESS, MON$REMOTE_PID, MON$TIMESTAMP
            FROM MON$ATTACHMENTS
            WHERE MON$ATTACHMENT_ID <> CURRENT_CONNECTION
              AND MON$REMOTE_PROCESS IS NOT NULL
            ORDER BY MON$TIMESTAMP";

        var attachments = new List<AttachmentInfo>();

        await using var command = new FbCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt32(0);
            var user = reader.IsDBNull(1) ? string.Empty : reader.GetString(1).Trim();
            var remoteAddress = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var remoteProcess = reader.IsDBNull(3) ? string.Empty : reader.GetString(3);
            var remotePid = reader.IsDBNull(4) ? 0 : reader.GetInt32(4);
            var connectedAt = reader.GetDateTime(5);

            attachments.Add(new AttachmentInfo(id, user, remoteAddress, remoteProcess, remotePid, connectedAt));
        }

        return attachments;
    }

    private static async Task<IReadOnlyList<StatementInfo>> GetActiveStatementsAsync(FbConnection connection, CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT
                s.MON$STATEMENT_ID,
                s.MON$ATTACHMENT_ID,
                a.MON$REMOTE_PROCESS,
                a.MON$REMOTE_PID,
                s.MON$SQL_TEXT,
                DATEDIFF(SECOND FROM s.MON$TIMESTAMP TO CURRENT_TIMESTAMP) AS SECONDS_RUNNING,
                (SELECT FIRST 1 c.MON$OBJECT_NAME FROM MON$CALL_STACK c WHERE c.MON$STATEMENT_ID = s.MON$STATEMENT_ID ORDER BY c.MON$CALL_ID DESC) AS PROC_NAME,
                (SELECT FIRST 1 c.MON$SOURCE_LINE FROM MON$CALL_STACK c WHERE c.MON$STATEMENT_ID = s.MON$STATEMENT_ID ORDER BY c.MON$CALL_ID DESC) AS PROC_LINE
            FROM MON$STATEMENTS s
            JOIN MON$ATTACHMENTS a ON a.MON$ATTACHMENT_ID = s.MON$ATTACHMENT_ID
            WHERE s.MON$STATE = 1
              AND s.MON$ATTACHMENT_ID <> CURRENT_CONNECTION
            ORDER BY SECONDS_RUNNING DESC";

        var statements = new List<StatementInfo>();

        await using var command = new FbCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt32(0);
            var attachmentId = reader.GetInt32(1);
            var remoteProcess = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
            var remotePid = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            var sqlText = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
            var secondsRunning = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
            var procName = reader.IsDBNull(6) ? string.Empty : reader.GetString(6).Trim();
            var procLine = reader.IsDBNull(7) ? 0 : reader.GetInt32(7);

            statements.Add(new StatementInfo(id, attachmentId, remoteProcess, remotePid, sqlText, procName, procLine, secondsRunning));
        }

        return statements;
    }
}
