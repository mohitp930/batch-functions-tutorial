using Azure.Storage;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.Azure.Batch;
using Microsoft.Azure.Batch.Auth;
using Microsoft.Azure.Batch.Common;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

public static void Run(Stream myBlob, string name, ILogger log)
{
    BatchSharedKeyCredentials cred = new BatchSharedKeyCredentials("<YOUR_BATCH_ACCOUNT_ENDPOINT_HERE>", "<YOUR_BATCH_ACCOUNT_HERE>", "<YOUR_BATCH_PRIMARY_KEY_HERE>");
    const string JobId = "ocr-job";
    const string InputContainerConnectionString = "<YOUR_STORAGE_ACCOUNT_CONNECTION_STRING>";
    const string inputContainerName = "input";
    const string OutputContainerSAS = "<YOUR_OUTPUT_BLOB_SAS_HERE>";

    using (BatchClient batchClient = BatchClient.Open(cred))
    {
        CloudJob job = batchClient.JobOperations.GetJob(JobId);
        job.Commit();
        log.LogInformation("Creating job...");


        // Create the blob client, for use in obtaining references to blob storage containers
        var blobServiceClient = new BlobServiceClient(InputContainerConnectionString);

        // Use the blob client to create the input container in Azure Storage 
        var containerClient = blobServiceClient.GetBlobContainerClient(inputContainerName);
        containerClient.CreateIfNotExistsAsync().Wait();

        var blobClient = containerClient.GetBlobClient(name);

        List<ResourceFile> inputFiles = new List<ResourceFile>();
        // Check whether this BlobContainerClient object has been authorized with Shared Key.
        string blobSasUrl = null;
        if (containerClient.CanGenerateSasUri)
        {
            // Create a SAS token that's valid for one hour.
            BlobSasBuilder sasBuilder = new BlobSasBuilder()
            {
                BlobContainerName = containerClient.Name,
                BlobName = blobClient.Name,
                Resource = "b"
            };

            sasBuilder.ExpiresOn = DateTimeOffset.UtcNow.AddHours(1);
            sasBuilder.SetPermissions(BlobContainerSasPermissions.Read);

            blobSasUrl = blobClient.GenerateSasUri(sasBuilder).ToString();
            log.LogInformation("SAS URI for blob is: {0}", blobSasUrl);
        }
        else
        {
            log.LogInformation(@"BlobContainerClient must be authorized with Shared Key 
                          credentials to create a service SAS.");
            return;
        }

        inputFiles.Add(ResourceFile.FromUrl(blobSasUrl, blobClient.Name));
        log.LogInformation($"Adding \"{name}\" as a resource file...");

        List<CloudTask> tasks = new List<CloudTask>();

        // Create each of the tasks to process one of the input files. 
        string inputFilename = Path.GetFileNameWithoutExtension(name);
        string outputTextFilename = "ocr-" + inputFilename + ".txt";
        string outputPdfFilename = "ocr-" + inputFilename + ".pdf";
        log.LogInformation($"Name of output text file: \"{outputTextFilename}\"");
        log.LogInformation($"Name of output PDF file: \"{outputPdfFilename}\"");
        string uniqueIdentifier = Regex.Replace(Convert.ToBase64String(Guid.NewGuid().ToByteArray()), "[/+=]", "");
        string taskId = String.Format(inputFilename.Replace(".", string.Empty) + "-" + uniqueIdentifier);

        string taskCommandLine = String.Format("/bin/bash -c \"sudo -S ocrmypdf --sidecar {0} {1} {2}\"", outputTextFilename, name, outputPdfFilename);

        CloudTask task = new CloudTask(taskId, taskCommandLine);
        task.UserIdentity = new UserIdentity(new AutoUserSpecification(elevationLevel: ElevationLevel.Admin, scope: AutoUserScope.Task));

        List<OutputFile> outputFileList = new List<OutputFile>();
        OutputFileBlobContainerDestination outputContainer = new OutputFileBlobContainerDestination(OutputContainerSAS);
        OutputFile outputFileText = new OutputFile(outputTextFilename,
                                        new OutputFileDestination(outputContainer),
                                        new OutputFileUploadOptions(OutputFileUploadCondition.TaskSuccess));
        OutputFile outputFilePdf = new OutputFile(outputPdfFilename,
                                                new OutputFileDestination(outputContainer),
                                                new OutputFileUploadOptions(OutputFileUploadCondition.TaskSuccess));
        outputFileList.Add(outputFileText);
        outputFileList.Add(outputFilePdf);

        task.ResourceFiles = new List<ResourceFile> { inputFiles[0] };
        task.OutputFiles = outputFileList;
        tasks.Add(task);

        // Add all tasks to the job.
        batchClient.JobOperations.AddTask(JobId, tasks);
        log.LogInformation($"Adding OCR task \"{taskId}\" for \"{inputFilename}\" ({myBlob.Length} bytes)...");
    }
}