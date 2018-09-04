#if !COMPILED
#load "../.paket/load/net472/Hopac.fsx"
#load "../.paket/load/net472/main.group.fsx"
#else
#r "Microsoft.WindowsAzure.Storage"
#endif


open System
open System.IO
open Microsoft.Azure.WebJobs.Host
open Microsoft.WindowsAzure.Storage
open Microsoft.WindowsAzure.Storage.Blob

let envVar key = Environment.GetEnvironmentVariable key

module Keys =
    let SourceStorage = "SourceStorage"
    let SourceContainerName = "SourceContainerName"
    let DesiredTopic = "DesiredTopic"
    let DestStorage = "DestStorage"
    let DestContainerName = "DestContainerName"
    let TableAccountStorage = "TableAccountStorage"

let getDestBlob (bn: string, fln: string) =
    let connStr, destContainerName = 
        envVar Keys.DestStorage,
        envVar Keys.DestContainerName
    let account = CloudStorageAccount.Parse(connStr)
    account.CreateCloudBlobClient()
        .GetContainerReference(destContainerName)
        .GetBlockBlobReference(sprintf "%s/%s/%s" bn (DateTime.UtcNow.ToString("yyyy-MM-ddTHH_mm_ss")) fln)


let Run(srcBlob: CloudBlockBlob, name: string, log: TraceWriter) =
    log.Info(sprintf "F# Blob trigger function processed blob\n Name: %s" name)
    let blobName = name
    let i = blobName.LastIndexOf('/')
    let filename = blobName.Substring(i + 1)
    let destBlob = getDestBlob(blobName, filename)
    log.Info(sprintf "src: %s \ndest: %s" (srcBlob.Uri.ToString()) (destBlob.Uri.ToString()))
    destBlob.StartCopy(srcBlob.Uri) |> ignore
