/opsx-explore  I want to make a restic clone in (mainly) C# that is specifically made for Azure Blob Archive tier. Drop support for all other storage providers.

I want a cli that is very similar to the restic one

Next to that, I want a File Explorer-alike web interface as a docker container that can browse repositories and perform the same actions as the CLI

For the CLI, use spectre
For the API, use aspnetcore minimal apis
Use Mediator (not MediatR) for reuse between the cli and api
For the web part, use Vue + Typescript. I will want streaming updates from the backend to the frontend

The restic source is avabable in #restic