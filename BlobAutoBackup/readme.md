# BlobAutoBackup function

## What's it all about?

We used to rely on a plain `Azure Blob` instance to store files for our application. It works great until people start to complain about missing files - damn, how did we forgot the logs and backup solutions in the first place!

It's better for us not to touch the existing running app. Instead, a little piece of code to augments the storage service is more approachable. And it might be a good way to reuse the logic across different systems with similar needs.

## How it works

This is just a simple small `Azure Function` in F#. It can subscribe the source container's "BlobDelete" event delivered by `Event Grid`. The storage account of the source container should enable `Soft Delete`, thus our function can then temporarily "undelete" the file and copy it to the destination container, then delete the file again. We need to store the action info in a table, so when the second time the file is being deleted, we bypass the backup process. Yeah, sounds hacky, but it can do its job.

<TODO> AppSettings Explain..

