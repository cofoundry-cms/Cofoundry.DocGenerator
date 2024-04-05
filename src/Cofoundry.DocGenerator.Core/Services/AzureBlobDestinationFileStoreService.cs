using System.Text;
using Cofoundry.Core.Configuration;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace Cofoundry.DocGenerator.Core;

public class AzureBlobDestinationFileStoreService : IDestinationFileStoreService
{
    private readonly CloudBlobClient _blobClient;
    private readonly string _containerName = "docs";
    private bool _isInitialized;

    public AzureBlobDestinationFileStoreService(DocGeneratorSettings docGeneratorSettings)
    {
        if (string.IsNullOrWhiteSpace(docGeneratorSettings.BlobStorageConnectionString))
        {
            throw new InvalidConfigurationException(typeof(DocGeneratorSettings), "The BlobStorageConnectionString is required when writing docs to azure.");
        }

        var storageAccount = CloudStorageAccount.Parse(docGeneratorSettings.BlobStorageConnectionString);
        _blobClient = storageAccount.CreateCloudBlobClient();
    }

    public async Task ClearDirectoryAsync(string folderName)
    {
        folderName = FormatBlobFilePath(folderName);
        var container = await GetBlobContainerAsync();
        var directory = container.GetDirectoryReference(folderName);

        BlobContinuationToken? continuationToken = null;
        var blobs = new List<IListBlobItem>();

        do
        {
            // each segment is max 5000 items
            var segment = await directory.ListBlobsSegmentedAsync(true, BlobListingDetails.None, null, continuationToken, null, null);
            continuationToken = segment.ContinuationToken;
            blobs.AddRange(segment.Results);
        }
        while (continuationToken != null);

        await DeleteBlobsAsync(blobs);
    }

    public async Task<string[]> GetDirectoryNamesAsync(string path)
    {
        path = FormatBlobFilePath(path);
        var container = await GetBlobContainerAsync();

        BlobContinuationToken? continuationToken = null;
        var directories = new List<CloudBlobDirectory>();

        do
        {
            // each segment is max 5000 items
            var segment = await container.ListBlobsSegmentedAsync(string.Empty, false, BlobListingDetails.None, null, continuationToken, null, null);
            continuationToken = segment.ContinuationToken;
            directories.AddRange(segment.Results.OfType<CloudBlobDirectory>());
        }
        while (continuationToken != null);

        var directoryNames = directories
            .Select(d => d.Prefix.Trim('/'))
            .ToArray();

        return directoryNames;
    }

    public async Task CopyFile(string source, string destination)
    {
        destination = FormatBlobFilePath(destination);
        var container = await GetBlobContainerAsync();

        using (var stream = File.OpenRead(source))
        {
            var blockBlob = container.GetBlockBlobReference(destination);
            await blockBlob.UploadFromStreamAsync(stream);
        }
    }

    public async Task WriteText(string text, string destination)
    {
        destination = FormatBlobFilePath(destination);

        using (var stream = new MemoryStream())
        using (var writer = new StreamWriter(stream, Encoding.UTF8))
        {
            writer.Write(text);
            writer.Flush();
            stream.Position = 0;

            var container = await GetBlobContainerAsync();
            var blockBlob = container.GetBlockBlobReference(destination);

            await blockBlob.UploadFromStreamAsync(stream);
        }
    }

    public Task EnsureDirectoryExistsAsync(string relativePath)
    {
        // no concept of empty directories in azure blog service
        return Task.CompletedTask;
    }

    private static string FormatBlobFilePath(string path)
    {
        return path.TrimStart('/');
    }

    private static async Task DeleteBlobsAsync(IEnumerable<IListBlobItem> blobs)
    {
        foreach (var blobItem in blobs)
        {
            if (blobItem is CloudBlockBlob blockBlob)
            {
                await blockBlob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, null, null, null);
            }
            else if (blobItem is CloudPageBlob pageBlob)
            {
                await pageBlob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, null, null, null);
            }
        }
    }

    private async Task<CloudBlobContainer> GetBlobContainerAsync()
    {
        var containerName = _containerName.ToLower();
        var container = _blobClient.GetContainerReference(containerName);

        // initalize container
        if (!_isInitialized)
        {
            await container.CreateIfNotExistsAsync();
            _isInitialized = true;
        }

        return container;
    }
}
