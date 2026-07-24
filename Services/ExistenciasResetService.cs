using System.Diagnostics;
using ExistenciasReset.Models;
using FirebirdSql.Data.FirebirdClient;

namespace ExistenciasReset.Services;

public class ExistenciasResetService(ILogger<ExistenciasResetService> logger, FirebirdMonitoringService monitoringService)
{
    private const string TableName = "EXISTENCIAS_INICIO_DIA";
    private const string ConstraintName = "PK_EXISTENCIAS_INICIO_DIA";
    private const string ValidationProcedure = "INV_EXISTENCIAS_NOW";
    private const int LockWaitSeconds = 20;
    private const int MaxAttempts = 2;
    private const int ValidationCommandTimeoutSeconds = 1800;

    public async Task<TenantDiagnostics> DiagnoseAsync(TenantOptions tenant, CancellationToken cancellationToken)
    {
        TenantDiagnostics diagnostics;

        try
        {
            await using var connection = new FbConnection(tenant.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            var constraintName = await GetExistingConstraintNameAsync(connection, cancellationToken);
            var rowCount = await GetRowCountAsync(connection, cancellationToken);
            var attachments = await monitoringService.GetAttachmentsAsync(tenant, cancellationToken);

            diagnostics = new TenantDiagnostics(constraintName != string.Empty, constraintName, rowCount, attachments, string.Empty);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "No se pudo diagnosticar el tenant {TenantId}", tenant.Id);
            diagnostics = new TenantDiagnostics(false, string.Empty, 0, [], ex.Message);
        }

        return diagnostics;
    }

    public async Task<ResetResult> ResetAsync(TenantOptions tenant, CancellationToken cancellationToken)
    {
        var overallStopwatch = Stopwatch.StartNew();
        var result = new ResetResult(false, [], null, "No se intentó ningún reinicio.", 0);

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            result = await AttemptResetAsync(tenant, attempt, cancellationToken);

            if (result.Success || !IsLockConflict(result.ErrorMessage))
            {
                break;
            }
        }

        overallStopwatch.Stop();

        return result with { TotalDurationMs = overallStopwatch.ElapsedMilliseconds };
    }

    private async Task<ResetResult> AttemptResetAsync(TenantOptions tenant, int attempt, CancellationToken cancellationToken)
    {
        var steps = new List<ResetStepResult>();

        await using var connection = new FbConnection(tenant.ConnectionString);

        ResetResult result;
        var connectionError = await TryOpenAsync(connection, cancellationToken);

        if (connectionError != string.Empty)
        {
            result = new ResetResult(false, steps, null, connectionError, 0);
        }
        else
        {
            var existingConstraint = await GetExistingConstraintNameAsync(connection, cancellationToken);

            var transactionOptions = new FbTransactionOptions
            {
                TransactionBehavior = FbTransactionBehavior.ReadCommitted | FbTransactionBehavior.RecVersion | FbTransactionBehavior.Wait,
                WaitTimeout = TimeSpan.FromSeconds(LockWaitSeconds)
            };

            await using var transaction = await connection.BeginTransactionAsync(transactionOptions, cancellationToken);
            var ok = true;

            steps.Add(await ExecuteStepAsync(
                connection, transaction,
                $"[Intento {attempt}] Limpiar tabla de trabajo",
                $"DELETE FROM {TableName}",
                isScalar: false,
                cancellationToken));
            ok = steps[^1].Success;

            if (ok && existingConstraint != string.Empty)
            {
                steps.Add(await ExecuteStepAsync(
                    connection, transaction,
                    $"[Intento {attempt}] Eliminar llave primaria existente ({existingConstraint})",
                    $"ALTER TABLE {TableName} DROP CONSTRAINT {existingConstraint}",
                    isScalar: false,
                    cancellationToken));
                ok = steps[^1].Success;
            }

            if (ok)
            {
                steps.Add(await ExecuteStepAsync(
                    connection, transaction,
                    $"[Intento {attempt}] Reconstruir llave primaria",
                    $"ALTER TABLE {TableName} ADD CONSTRAINT {ConstraintName} PRIMARY KEY (CODIGO_BARRAS, NUM_ALMACEN)",
                    isScalar: false,
                    cancellationToken));
                ok = steps[^1].Success;
            }

            if (ok)
            {
                steps.Add(await ExecuteStepAsync(
                    connection, transaction,
                    $"[Intento {attempt}] Validar generación de existencias",
                    $"SELECT COUNT(*) FROM {ValidationProcedure}",
                    isScalar: true,
                    cancellationToken));
                ok = steps[^1].Success;
            }

            if (ok)
            {
                await transaction.CommitAsync(cancellationToken);
                var totalGenerado = ToNullableInt(steps[^1].ScalarResult);
                result = new ResetResult(true, steps, totalGenerado, string.Empty, 0);
            }
            else
            {
                await transaction.RollbackAsync(cancellationToken);
                result = new ResetResult(false, steps, null, steps[^1].Message, 0);
            }
        }

        return result;
    }

    private async Task<string> TryOpenAsync(FbConnection connection, CancellationToken cancellationToken)
    {
        string error = string.Empty;

        try
        {
            await connection.OpenAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "No se pudo conectar a la base Firebird");
            error = ex.Message;
        }

        return error;
    }

    private async Task<ResetStepResult> ExecuteStepAsync(
        FbConnection connection,
        FbTransaction transaction,
        string name,
        string sql,
        bool isScalar,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        bool success;
        string message;
        object? scalarResult = null;

        await using var command = new FbCommand(sql, connection, transaction);

        if (isScalar)
        {
            command.CommandTimeout = ValidationCommandTimeoutSeconds;
        }

        try
        {
            if (isScalar)
            {
                scalarResult = await command.ExecuteScalarAsync(cancellationToken);
                message = $"TOTAL_GENERADO = {scalarResult}";
            }
            else
            {
                await command.ExecuteNonQueryAsync(cancellationToken);
                message = "OK";
            }

            success = true;
        }
        catch (FbException fbEx)
        {
            success = false;
            message = $"[{fbEx.SQLSTATE}] {fbEx.Message}";
            logger.LogWarning(fbEx, "Fallo el paso '{Step}' con SQL: {Sql}", name, sql);
        }
        catch (Exception ex)
        {
            success = false;
            message = ex.Message;
            logger.LogWarning(ex, "Fallo el paso '{Step}' con SQL: {Sql}", name, sql);
        }

        stopwatch.Stop();

        return new ResetStepResult(name, sql, success, message, stopwatch.ElapsedMilliseconds, scalarResult);
    }

    private static async Task<string> GetExistingConstraintNameAsync(FbConnection connection, CancellationToken cancellationToken)
    {
        const string sql = @"
            SELECT RDB$CONSTRAINT_NAME
            FROM RDB$RELATION_CONSTRAINTS
            WHERE RDB$RELATION_NAME = @table AND RDB$CONSTRAINT_TYPE = 'PRIMARY KEY'";

        await using var command = new FbCommand(sql, connection);
        command.Parameters.Add("@table", FbDbType.VarChar).Value = TableName;
        var result = await command.ExecuteScalarAsync(cancellationToken);

        return result is null or DBNull ? string.Empty : result.ToString().Trim();
    }

    private static async Task<int> GetRowCountAsync(FbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new FbCommand($"SELECT COUNT(*) FROM {TableName}", connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);

        return Convert.ToInt32(result);
    }

    private static bool IsLockConflict(string message) =>
        message.Contains("lock conflict", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("lock time-out", StringComparison.OrdinalIgnoreCase);

    private static int? ToNullableInt(object? value)
    {
        int? result = null;

        if (value is not null and not DBNull)
        {
            result = Convert.ToInt32(value);
        }

        return result;
    }
}
