#if !COMPILED
#load "../.paket/load/net472/main.group.fsx"
#endif
// #r "../bin/Microsoft.Azure.KeyVault.Core.dll"
// #r "../bin/Newtonsoft.Json.dll"
// #r "../bin/Microsoft.WindowsAzure.Storage.dll"

open System
open System.IO
open Microsoft.Azure.WebJobs.Host
open Microsoft.WindowsAzure.Storage
open Microsoft.WindowsAzure.Storage.Table
open Microsoft.WindowsAzure.Storage.Auth
open Microsoft.WindowsAzure.Storage.Blob
open Newtonsoft.Json.Linq

let Run_WithBlobStorageBinding_Obsoleted(myBlob: Stream, name: string, log: TraceWriter, destBlob: CloudBlockBlob) =
    // some knowledge learned when experimenting Azure Blobs:
    //  1. blob deletions won't fire the blobStorageBinding trigger, (only creations and updates can,)
    //     and not even when the storage account had Soft Deletion enabled.
    //     so we'll have to resort to EventGrid
    //  2. azure storage emulator will not handle blobStorageBinding's return binding, so we have to use real storage account to test upload

    log.Verbose(sprintf "F# Blob trigger function processed blob\n Name: %s \n Size: %d Bytes" name myBlob.Length)   
    log.Info(destBlob.Container.Name)
    log.Info(destBlob.Name) 
    destBlob.UploadFromStreamAsync(myBlob).Wait()
    log.Info("after action..")    


let getStubTable() =
    let tableAccountConnStr = Environment.GetEnvironmentVariable("TableAccountStorage")
    let storageAccount = CloudStorageAccount.Parse(tableAccountConnStr)
    let tableClient = storageAccount.CreateCloudTableClient()
    let table = tableClient.GetTableReference("deletionStubTable")
    table.CreateIfNotExists() |> ignore
    table

let addStub (table: CloudTable) blobUrl =
    let stub = TableEntity("blob", blobUrl)
    stub
    |> TableOperation.Insert
    |> table.Execute

let retrieveStub (table: CloudTable) (blobUrl:string) =
    let result =
        TableOperation.Retrieve<TableEntity>("blob", blobUrl)
        |> table.Execute
    result.Result :?> ITableEntity

let deleteStub (table: CloudTable) stub =
    stub
    |> TableOperation.Delete
    |> table.Execute


let optionDefaultValue<'a> (v: 'a) (op: 'a option) =
    match op with
    | Some a -> a
    | None -> v

open System.Text.RegularExpressions

let retrieveBlobNameFileName (eventInfo: JObject) =
    let unknownNames = "unknown-blob-name", "unknown-file-name"
    let subject = eventInfo.["subject"] |> Option.ofObj |> Option.map (fun o -> o.ToString()) |> optionDefaultValue null
    if String.IsNullOrEmpty(subject) then
        unknownNames
    else
        let regx = Regex("/blobs/(?<blobName>\S+)$")
        let result = regx.Matches(subject)
        result.Count > 0
        |> function | true -> Some result.[0] | false -> None
        |> Option.map (fun r -> 
            let bn = r.Groups.["blobName"].Value
            let i = bn.LastIndexOf('/')
            let filename = bn.Substring(i + 1)
            bn, filename)
        |> optionDefaultValue unknownNames

let getDestBlob (bn: string, fln: string) =
    let connStr = Environment.GetEnvironmentVariable("DestStorage")
    let account = CloudStorageAccount.Parse(connStr)
    account.CreateCloudBlobClient()
    |> fun bc -> bc.GetContainerReference("destBlob")
    |> fun c -> c.GetBlockBlobReference(sprintf "%s/%s/%s" bn (DateTime.UtcNow.ToString("yyyy-MM-ddTHH_mm_ss")) fln)

let getSourceBlob bn =
    let connStr = Environment.GetEnvironmentVariable("SourceStorage")
    let container =
        CloudStorageAccount.Parse(connStr)
        |> fun a -> a.CreateCloudBlobClient()
        |> fun bc -> bc.GetContainerReference("samples-workitems")
        
    container.ListBlobs(prefix = bn, blobListingDetails = BlobListingDetails.Deleted)
    |> Seq.tryHead
    |> Option.map (fun h -> h :?> CloudBlockBlob)


let Run(eventGridEventStr: string, log: TraceWriter) =
    let eventGridEvent = JObject.Parse(eventGridEventStr)
    log.Info(eventGridEvent.ToString())
    let desiredTopic = Environment.GetEnvironmentVariable("DesiredTopic")
    if eventGridEvent.["topic"].ToString() = desiredTopic && eventGridEvent.["eventType"].ToString() = "Microsoft.Storage.BlobDeleted" then
        log.Info("before get table..")
        let table = getStubTable()
        log.Info("after got table..")
        let blobUrl = eventGridEvent.["data"].["url"].ToString()
        log.Info("retrieving stub..")
        let stub = retrieveStub table blobUrl
        log.Info("retrieved stub..")
        if stub = null then
            let blobName, fileName = eventGridEvent |> retrieveBlobNameFileName
            log.Info(sprintf "bn: %s; fn: %s" blobName fileName)
            match getSourceBlob blobName with
            | Some cloudBlob ->
                log.Info(cloudBlob.Uri.ToString())
                cloudBlob.Undelete()
                blobUrl |> addStub table |> ignore
                let destBlob = getDestBlob(blobName, fileName)
                destBlob.StartCopy(Uri(blobUrl)) |> ignore
                cloudBlob.Delete()
            | _ -> ()
        else
            log.Info("delete stub branch..")
            deleteStub table stub |> ignore

(*
    Objectives:
    [*] 1. listen to source blob storage add/update events
    [*] 2. when an add/update come from src, write the same blob to dest
    [ ] 3. explore the possibility of conditional write to dest blob
    [*] 4. test with real storage account
    [*] 5. set src blob storage to use soft deletion, to see if we can receive soft deletion event //, and the result is no, not with blobStorageBinding
    [ ] 6. if 4. failed, try use Event Grid..
    [ ] 7. deploy to Azure with continuous deployment

*)
