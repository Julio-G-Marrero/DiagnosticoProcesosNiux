namespace ExistenciasReset.Models;

public record TenantDiagnostics(
    bool HasPrimaryKey,
    string ConstraintName,
    int RowCount,
    IReadOnlyList<AttachmentInfo> ActiveAttachments,
    string ErrorMessage);
