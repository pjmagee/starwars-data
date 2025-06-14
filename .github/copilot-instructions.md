# Project coding standards

## General Guidelines

- Use .NET Aspire for orchestration
- Use CosmosDB for data storage (Aspire for local)
- Use Blazor (MudBlazor) for the front-end
- Use C# for the back-end
- Use latest .NET 10 SDK, and latest C# language features (e.g filescoped namespaces, top-level statements, etc.)
- Use optimal filtering, sorting, projecting techniques for data operations using CosmosDB
- Use the latest C# language features


## Error Handling

- Use try/catch blocks for async operations
- Implement proper error boundaries in React components
- Always log errors with contextual information


## Schema Information

- Use the MongoDB MCP Server which has tools to query and understand the schema and database.
- Database Name: starwars-data-raw
- You should read, understand and query the schema before writing any code when fetching data from the database.


## Documentation

- https://learn.microsoft.com/en-us/dotnet/aspire/
- https://learn.microsoft.com/en-us/dotnet/aspire/fundamentals/integrations-overview
- https://learn.microsoft.com/en-us/dotnet/aspire/database/mongodb-integration?tabs=dotnet-cli

