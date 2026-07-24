namespace ExistenciasReset.Models;

public record StatementInfo(
    int Id,
    int AttachmentId,
    string RemoteProcess,
    int RemotePid,
    string Sql,
    string ProcedureName,
    int ProcedureLine,
    long SecondsRunning);
