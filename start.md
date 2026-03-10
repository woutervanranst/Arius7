/opsx-explore  I want to make a restic clone in (mainly) C# that is specifically made for Azure Blob Archive tier. Drop support for all other storage providers.

I want a cli that is very similar to the restic one

Next to that, I want a File Explorer-alike web interface as a docker container that can browse repositories and perform the same actions as the CLI

For the CLI, use spectre
For the API, use aspnetcore minimal apis
Use Mediator (not MediatR) for reuse between the cli and api
For the web part, use Vue + Typescript. I will want streaming updates from the backend to the frontend

The restic source is avabable in #restic

---

executing a restore should restore whatever is available, and hydrate the necessary ones. Hydrate in a separate data-hydrated folder that we can delete after restore. Do not hydrate in place.

the repository should not be compatible with restic.

For chunking, use a simple gear chunker, but make it interchangeable through an interface

The pack size should be configurable

think through the  scale, as an extreme example consider a 1 TB archive consisting out of 2 KB files

Only the big chunks of data should be in the archive tier, the rest can be in Cool or Cold tier. Or use blob locks if relevant.

You cannot assume that a local database exists; the archive should be fully recoverable from the remote.