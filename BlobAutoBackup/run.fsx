#if !COMPILED
#load "../.paket/load/net472/Hopac.fsx"
#load "../.paket/load/net472/main.group.fsx"
#else
#r "Microsoft.WindowsAzure.Storage"
#r "Newtonsoft.Json"
#r "../externalBin/Hopac.Core.dll"
#r "../externalBin/Hopac.Platform.dll"
#r "../externalBin/Hopac.dll"
#r "../externalBin/HttpFs.dll"
#endif



open System
open System.IO
open Microsoft.Azure.WebJobs.Host
open Microsoft.WindowsAzure.Storage
open Microsoft.WindowsAzure.Storage.Table
open Microsoft.WindowsAzure.Storage.Auth
open Microsoft.WindowsAzure.Storage.Blob
open Newtonsoft.Json.Linq
open Hopac
open HttpFs.Logging
open HttpFs.Client

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


// Utils and Constants

let optionDefaultValue<'a> (v: 'a) (op: 'a option) =
    match op with
    | Some a -> a
    | None -> v

let optionOfObj o =
    match o with
    | null -> None
    | v -> Some v

let envVar key = Environment.GetEnvironmentVariable key

module Keys =
    let SourceStorage = "SourceStorage"
    let SourceContainerName = "SourceContainerName"
    let DesiredTopic = "DesiredTopic"
    let DestStorage = "DestStorage"
    let DestContainerName = "DestContainerName"
    let TableAccountStorage = "TableAccountStorage"


// stub operations

let getStubTable() =
    let tableAccountConnStr = Keys.TableAccountStorage |> envVar
    let table = 
        CloudStorageAccount.Parse(tableAccountConnStr)
            .CreateCloudTableClient()
            .GetTableReference("deletionStubTable")
    table.CreateIfNotExists() |> ignore
    table

let sanitizeKey (keyStr: string) =
    let sb = System.Text.StringBuilder()
    let invalidChars = [| '/'; '\\'; '#'; '?' |]
    keyStr.ToCharArray() 
    |> Seq.fold (fun (s: System.Text.StringBuilder) c ->
            (Array.exists ((=) c) invalidChars || Char.IsControl(c))
            |> (function | true -> '_' | false -> c)
            |> s.Append |> ignore
            s
        ) sb
    |> ignore
    sb.ToString()

let addStub (table: CloudTable) blobUrl =
    let stub = TableEntity("blob", sanitizeKey blobUrl)
    stub
    |> TableOperation.Insert
    |> table.Execute

let retrieveStub (table: CloudTable) (blobUrl:string) =
    let result =
        TableOperation.Retrieve<TableEntity>("blob", sanitizeKey blobUrl)
        |> table.Execute
    result.Result :?> ITableEntity

let deleteStub (table: CloudTable) stub =
    stub
    |> TableOperation.Delete
    |> table.Execute


// blob operations

open System.Text.RegularExpressions

let retrieveContainerName (eventInfo: JObject) =
    eventInfo.["subject"] |> optionOfObj 
    |> Option.bind (fun o -> 
        let raw = o.ToString()
        String.IsNullOrEmpty(raw)
        |> function | true -> None | false -> Some raw)
    |> Option.bind (fun subject ->
        let regx = Regex("/containers/(?<containerName>[a-z0-9_-]+)/blobs/")
        let result = regx.Matches(subject)
        result.Count > 0
        |> function | true -> Some result.[0] | false -> None)
    |> Option.map (fun r -> r.Groups.["containerName"].Value)
    |> optionDefaultValue null

let retrieveBlobNameFileName (eventInfo: JObject) =
    let unknownNames = "unknown-blob-name", "unknown-file-name"
    let subject = eventInfo.["subject"] |> optionOfObj |> Option.map (fun o -> o.ToString()) |> optionDefaultValue null
    if String.IsNullOrEmpty(subject) then
        unknownNames
    else
        let regx = Regex("/blobs/(?<blobName>.+)$")
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
    let connStr, destContainerName = 
        envVar Keys.DestStorage,
        envVar Keys.DestContainerName
    let account = CloudStorageAccount.Parse(connStr)
    account.CreateCloudBlobClient()
        .GetContainerReference(destContainerName)
        .GetBlockBlobReference(sprintf "%s/%s/%s" bn (DateTime.UtcNow.ToString("yyyy-MM-ddTHH_mm_ss")) fln)

let getSourceBlob blobName =
    let connStr, sourceContainerName = 
        envVar Keys.SourceStorage,
        envVar Keys.SourceContainerName
    CloudStorageAccount.Parse(connStr)
        .CreateCloudBlobClient()
        .GetContainerReference(sourceContainerName)
        .GetBlockBlobReference blobName

let getBlobSAS (blob: CloudBlockBlob) =
    let policy = SharedAccessBlobPolicy()
    policy.SharedAccessStartTime <- Nullable DateTimeOffset.UtcNow
    policy.SharedAccessExpiryTime <- Nullable (DateTimeOffset.UtcNow.AddHours(1.))
    policy.Permissions <- SharedAccessBlobPermissions.List ||| SharedAccessBlobPermissions.Delete ||| SharedAccessBlobPermissions.Write
    blob.GetSharedAccessSignature(policy)

let undeleteBlob (blob: CloudBlockBlob) = job {
    let sas = getBlobSAS blob
    let uri = Uri(blob.Uri, (sprintf "%s&comp=undelete" sas))
    let request = 
        Request.create Put uri
        |> Request.setHeader (Custom ("x-ms-version", "2018-03-28"))
    let! resp = getResponse request
    return resp.statusCode = 200
}


// main func

let Run(eventGridEventStr: string, log: TraceWriter) =
    let eventGridEvent = JObject.Parse(eventGridEventStr)
    log.Info(eventGridEvent.ToString())
    let desiredTopic, sourceContainerName = 
        Keys.DesiredTopic |> envVar,
        Keys.SourceContainerName |> envVar
    let containerName = retrieveContainerName eventGridEvent
    
    if eventGridEvent.["topic"].ToString() = desiredTopic 
    && eventGridEvent.["eventType"].ToString() = "Microsoft.Storage.BlobDeleted"
    && containerName = sourceContainerName then
        let table = getStubTable()
        let blobUrl = eventGridEvent.["data"].["url"].ToString()
        let stub = retrieveStub table blobUrl
        if stub = null then
            let blobName, fileName = eventGridEvent |> retrieveBlobNameFileName
            let cloudBlob = getSourceBlob blobName
            job {
                let! success = undeleteBlob cloudBlob
                if success then
                    blobUrl |> addStub table |> ignore
                    let destBlob = getDestBlob(blobName, fileName)
                    destBlob.StartCopy(Uri blobUrl) |> ignore                    
                    cloudBlob.Delete()
            } |> Hopac.run
            
        else
            log.Info("delete stub branch..")
            deleteStub table stub |> ignore

(*
    Objectives:
    [*] 1. listen to source blob storage add/update events
    [*] 2. when an add/update come from src, write the same blob to dest
    [*] 3. explore the possibility of conditional write to dest blob
    [*] 4. test with real storage account
    [*] 5. set src blob storage to use soft deletion, to see if we can receive soft deletion event //, and the result is no, not with blobStorageBinding
    [*] 6. if 4. failed, try use Event Grid..
    [*] 7. deploy to Azure with continuous deployment

*)
