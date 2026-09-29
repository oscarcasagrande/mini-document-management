namespace DocReader.Domain.Storage;

/// <summary>Where the bytes of a document are kept.</summary>
public enum StorageProvider
{
    /// <summary>A directory on the local filesystem (a Docker volume). Implemented.</summary>
    FileSystem = 0,

    /// <summary>A table in the application database (<c>document_blobs</c>). Implemented.</summary>
    Database = 1,

    /// <summary>Azure Blob Storage. Implemented; needs <c>connectionString</c> and <c>container</c>.</summary>
    AzureBlobStorage = 2,

    /// <summary>Amazon S3 or a compatible service. Implemented; needs <c>bucket</c>, <c>accessKeyId</c> and <c>secretAccessKey</c>, with optional <c>region</c> and <c>serviceUrl</c>.</summary>
    AwsS3 = 3
}
