# OLX watcher

Two AWS Lambda projects targeting .NET 8 are deployed from one Serverless Framework configuration:

- `OlxWatcher.ListingsApi` — HTTP API endpoint at `GET /listings`.
- `OlxWatcher.ListingsWatcher` — scheduled listing check, every 15 minutes.

## Deploy

Install the Node and .NET dependencies, then deploy both functions together:

```bash
npm install
dotnet tool install -g Amazon.Lambda.Tools
dotnet restore
npm run deploy -- --stage dev --region eu-central-1
```

Use `npm run package` to build deployment artifacts without deploying. The `serverless-multi-dotnet` plugin packages each project into its configured `publish/deploy-package.zip`; this lets one service contain both .NET projects. AWS credentials must be configured for the target account.
