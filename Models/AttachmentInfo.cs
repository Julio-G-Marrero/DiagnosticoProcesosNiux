namespace ExistenciasReset.Models;

public record AttachmentInfo(int Id, string User, string RemoteAddress, string RemoteProcess, int RemotePid, DateTime ConnectedAt);
