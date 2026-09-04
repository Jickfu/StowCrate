namespace StowCrate.Application.StorageMaintenance;

/// <summary>只读现场重验已完成迁移；不删除文件，不独立释放 reservation，不产生可缓存授权。</summary>
public interface IStorageRelocationCompletionProbe
{
    Task VerifyCompletedAsync(StorageRelocationJournal journal, CancellationToken cancellationToken);
}
