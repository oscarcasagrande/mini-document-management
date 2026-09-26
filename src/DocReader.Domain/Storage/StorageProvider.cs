namespace DocReader.Domain.Storage;

/// <summary>Where the bytes of a document are kept.</summary>
public enum StorageProvider
{
    /// <summary>A directory on the local filesystem (a Docker volume). Implemented.</summary>
    FileSystem = 0,

    /// <summary>A table in the application database (<c>document_blobs</c>). Implemented.</summary>
    Database = 1,

    /// <summary>Azure Blob Storage. Registered, adapter not implemented yet.</summary>
    AzureBlobStorage = 2,

    /// <summary>Amazon S3 or a compatible service. Registered, adapter not implemented yet.</summary>
    AwsS3 = 3
}
