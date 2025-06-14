# Star Wars Data

A Blazor WebAssembly application that downloads data from Wookiepedia. The data is processed and stored in several MongoDB databases and collections.

## Features

- Download data from Wookiepedia
- Process data and store in MongoDB
- Ask AI and render chart data from the MongoDB database
- Charts on various slices of star wars data such as wars, force powers, etc
- Data tables based off different categories of data
- A galaxy map that shows the regions, sectors, systems and planets of the star wars galaxy.
- A family tree that shows the relationships between characters
- A timeline that shows the events of the star wars galaxy filtered by various categories

## Job Management

Background jobs for data processing are managed using Hangfire with MongoDB persistence. The Hangfire dashboard is available at `/hangfire` when running the API service, providing:

- Real-time job status monitoring
- Job queue management
- Job retry and cancellation capabilities
- Detailed job execution logs
- Historical job performance metrics

## Technologies

- Blazor UI WebAssembly
- MudBlazor
- MongoDB
- Hangfire (Background job processing with MongoDB persistence)
- Semantic Kernel and Mongo MCP server
- C# .NET
- Aspire Orchestrator
