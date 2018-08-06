#if !COMPILED
#load "../.paket/load/net472/main.group.fsx"
#endif

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


let Run(eventGridEvent: JObject, log: TraceWriter, destBlob: CloudBlockBlob) =
    log.Verbose(sprintf "F# EventGrid trigger function processed blob\n Event: %s \n " (eventGridEvent.ToString()))   
    log.Info(eventGridEvent.ToString())
    let desiredTopic = Environment.GetEnvironmentVariable("DesiredTopic")
    if eventGridEvent.["topic"].ToString() = desiredTopic && eventGridEvent.["eventType"].ToString() = "Microsoft.Storage.BlobDeleted" then
        let table = getStubTable()
        let blobUrl = eventGridEvent.["data"].["url"].ToString()
        let stub = retrieveStub table blobUrl
        if stub = null then
            let cloudBlob = CloudBlockBlob(Uri(blobUrl))
            cloudBlob.Undelete()
            blobUrl |> addStub table |> ignore
            destBlob.StartCopy(Uri(blobUrl)) |> ignore
            cloudBlob.Delete()
        else
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

