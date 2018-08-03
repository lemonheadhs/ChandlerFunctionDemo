#if !COMPILED
#load "../.paket/load/net472/main.group.fsx"
#endif

open System
open System.IO
open Microsoft.Azure.WebJobs.Host
open Microsoft.WindowsAzure.Storage.Blob

let Run(myBlob: Stream, name: string, log: TraceWriter, destBlob: CloudBlockBlob) =
    log.Verbose(sprintf "F# Blob trigger function processed blob\n Name: %s \n Size: %d Bytes" name myBlob.Length)   
    log.Info(destBlob.Container.Name)
    log.Info(destBlob.Name) 
    //log.Info(sprintf "IsDeleted: %b" destBlob.IsDeleted)
    //log.Info(sprintf "Created: %A" destBlob.Properties.Created)
    destBlob.UploadFromStreamAsync(myBlob).Wait()
    log.Info("after action..")    
    //log.Info(sprintf "IsDeleted: %b" destBlob.IsDeleted)
    //log.Info(sprintf "Created: %A" destBlob.Properties.Created)


(*
    Objectives:
    [*] 1. listen to source blob storage add/update events
    [ ] 2. when an add/update come from src, write the same blob to dest
    [ ] 3. explore the possibility of conditional write to dest blob
    [ ] 4. test with real storage account
    [ ] 5. set src blob storage to use soft deletion, to see if we can receive soft deletion event
    [ ] 6. if 4. failed, try use Event Grid..
    [ ] 7. deploy to Azure with continuous deployment

*)

